using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Notchira
{
    // Layered, click-through-where-transparent window pinned to the top edge of the primary screen.
    // The window is allocated once at the largest size; only the notch shape inside it grows and shrinks.
    sealed class Island : Form
    {
        const float MaxW = 460, MaxH = 150;
        static readonly SizeF IdleSize = new SizeF(96, 24);
        static readonly SizeF CompSize = new SizeF(250, 32);
        static readonly SizeF ExpSize = new SizeF(440, 140);
        const float TextX = 130, TextW = 292, BarY = 78, BtnY = 118;

        static readonly SizeF ToastSize = new SizeF(300, 56);

        enum Mode { Idle, Compact, Toast, Expanded }

        sealed class ToastInfo { public string Title, Body, Id; public int Progress = -1; }
        ToastInfo toast;
        readonly Timer toastTimer = new Timer();
        long lastToastTick;

        Lyrics lyrics;
        string lyricKey;
        readonly Timer lyricTimer = new Timer();

        readonly Eyes eyes = new Eyes();
        readonly Timer look = new Timer { Interval = 100 };
        readonly Timer blinkTimer = new Timer();
        readonly Timer nodTimer = new Timer { Interval = 2400 };
        readonly Timer sleepTimer = new Timer { Interval = 60000 };
        readonly Random rng = new Random();
        readonly Timer trimTimer = new Timer { Interval = 3000 };
        long lastTrim;
        bool asleep, quiet;
        long lastSound;

        // Sounds stay off in fullscreen, Focus Assist, while hidden, and (except clicks) while music plays.
        void Ping(string name)
        {
            if (Settings.Get("sound", "1") != "1" || userHidden || fsHidden || quiet) return;
            if (name != "click" && track != null && track.Playing) return;
            long now = Environment.TickCount;
            if (now - lastSound < 400) return;
            lastSound = now;
            Sounds.Play(name);
        }

        float S = 1f;
        Bitmap frame, backdrop;
        readonly Spring sw = new Spring(IdleSize.Width);
        readonly Spring sh = new Spring(IdleSize.Height);
        readonly Timer anim = new Timer { Interval = 10 };
        readonly Timer tick = new Timer { Interval = 1000 };
        readonly Timer leave = new Timer { Interval = 350 };
        readonly Timer slow = new Timer { Interval = 3000 };
        readonly Timer pause = new Timer { Interval = 8000 };
        readonly Stopwatch clock = new Stopwatch();
        double lastT;
        int tickCount;

        readonly MediaWatcher media = new MediaWatcher();
        TrackInfo track;
        Mode cur = Mode.Idle;
        bool hover, pausedRecent, userHidden, fsHidden, glassOk;
        string material;

        NotifyIcon tray;
        ContextMenuStrip menu;
        ToolStripMenuItem miHide, miLiquid, miSolid, miAuto, miLyrics, miSound;

        static readonly Font fTitle = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel);
        static readonly Font fSub = new Font("Segoe UI", 12.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        static readonly Font fComp = new Font("Segoe UI", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
        static readonly Font fSmall = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Pixel);
        static readonly Font fClock = new Font("Segoe UI", 36f, FontStyle.Regular, GraphicsUnit.Pixel);
        static readonly Font fNote = new Font("Segoe UI Symbol", 26f, FontStyle.Regular, GraphicsUnit.Pixel);
        static readonly StringFormat sfNear = MakeFormat(StringAlignment.Near);
        static readonly StringFormat sfFar = MakeFormat(StringAlignment.Far);
        static readonly StringFormat sfMid = MakeFormat(StringAlignment.Center);

        static StringFormat MakeFormat(StringAlignment a)
        {
            var f = new StringFormat(StringFormat.GenericTypographic);
            f.Trimming = StringTrimming.EllipsisCharacter;
            f.FormatFlags |= StringFormatFlags.NoWrap;
            f.Alignment = a;
            return f;
        }

        public Island()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Text = "Notchira";
            material = Settings.Get("material", "liquid");
            Current = this;

            BuildTray();
            anim.Tick += OnAnim;
            tick.Tick += delegate { tickCount++; if (tickCount % 2 == 0) CaptureBackdrop(); Render(); };
            leave.Tick += delegate { leave.Stop(); hover = false; Retarget(); };
            slow.Tick += delegate { OnSlow(); };
            pause.Tick += delegate { pause.Stop(); pausedRecent = false; Retarget(); };
            look.Tick += delegate { OnLook(); };
            trimTimer.Tick += delegate
            {
                trimTimer.Stop();
                long now = Environment.TickCount;
                if (now - lastTrim <= 30000) return;
                if (anim.Enabled || cur == Mode.Expanded) { trimTimer.Start(); return; }
                lastTrim = now;
                Memory.Trim();
            };
            blinkTimer.Tick += delegate
            {
                blinkTimer.Stop();
                if (Visible) { eyes.Blink(rng.Next(5) == 0); Kick(); }
                ScheduleBlink();
            };
            nodTimer.Tick += delegate { if (Visible && track != null && track.Playing) { eyes.Nod(); Kick(); } };
            sleepTimer.Tick += delegate { sleepTimer.Stop(); asleep = true; UpdateMood(); };
            toastTimer.Tick += delegate { toastTimer.Stop(); toast = null; Retarget(); Render(); };
            lyricTimer.Tick += delegate { lyricTimer.Stop(); Render(); ScheduleLyric(); };
            media.Changed += OnTrack;
            LocalPlayer.I.Changed += OnLocal;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += delegate { Setup(); Render(); };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            glassOk = Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
            Native.RegisterHotKey(Handle, 1, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, 0x48);
            Setup();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            slow.Start();
            trimTimer.Start();
            look.Start();
            ScheduleBlink();
            sleepTimer.Start();
            string demo = Environment.GetEnvironmentVariable("NOTCHIRA_DEMO");
            if (demo != null) StartDemo(demo);
            else media.Start();
            Notify.StartServer(OnNotify);
            CaptureBackdrop();
            Render();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Native.UnregisterHotKey(Handle, 1);
            tray.Visible = false;
            tray.Dispose();
            base.OnFormClosed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY) { ToggleHidden(); return; }
            base.WndProc(ref m);
        }

        void Setup()
        {
            var scr = Screen.PrimaryScreen;
            uint dpi = Native.GetDpiForWindow(Handle);
            S = dpi > 0 ? dpi / 96f : 1f;
            int W = (int)Math.Ceiling(MaxW * S), H = (int)Math.Ceiling(MaxH * S);
            if (frame == null || frame.Width != W || frame.Height != H)
            {
                if (frame != null) frame.Dispose();
                frame = new Bitmap(W, H, PixelFormat.Format32bppPArgb);
            }
            SetBounds(scr.Bounds.Left + (scr.Bounds.Width - W) / 2, scr.Bounds.Top, W, H);
        }

        // ---- state ------------------------------------------------------------------------------------------

        Mode Target()
        {
            if (hover) return Mode.Expanded;
            if (toast != null) return Mode.Toast;
            if (track != null && (track.Playing || pausedRecent)) return Mode.Compact;
            return Mode.Idle;
        }

        void Retarget()
        {
            var m = Target();
            if (m != cur)
            {
                cur = m;
                CaptureBackdrop();
                tick.Enabled = m == Mode.Expanded;
                tickCount = 0;
            }
            SizeF z = m == Mode.Idle ? IdleSize : m == Mode.Compact ? CompSize : m == Mode.Toast ? ToastSize : ExpSize;
            sw.T = z.Width;
            sh.T = z.Height;
            if (!anim.Enabled) Kick();
            else Render();
            UpdateMood();
            ScheduleLyric();
        }

        // ---- toast (from --notify) --------------------------------------------------------------------------

        void OnNotify(Notify.Message m)
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke((Action)delegate { ShowToast(m); });
        }

        void ShowToast(Notify.Message m)
        {
            if (Teleprompter.Open) return;
            bool update = toast != null && m.Progress >= 0 && m.Id.Length > 0 && toast.Id == m.Id;
            long now = Environment.TickCount;
            if (!update && now - lastToastTick < 500) return;
            lastToastTick = now;
            Wake();
            if (m.Progress < 0) Ping("toast");
            else if (m.Progress >= 100) Ping("done");
            toast = new ToastInfo { Title = m.Title, Body = m.Body, Id = m.Id, Progress = m.Progress };
            toastTimer.Stop();
            toastTimer.Interval = m.Progress < 0 ? 3500 : m.Progress >= 100 ? 1200 : 120000;
            toastTimer.Start();
            Retarget();
            Render();
        }

        // ---- lyrics -----------------------------------------------------------------------------------------

        void LoadLyrics(TrackInfo t)
        {
            string key = t == null ? null : t.Title + "|" + t.Artist;
            if (key == lyricKey) return;
            lyricKey = key;
            lyrics = null;
            if (t == null || Settings.Get("lyrics", "1") != "1") return;
            string title = t.Title, artist = t.Artist, album = t.Album;
            Lyrics.Fetch(title, artist, album, t.Duration, Settings.Get("lyrics.online", "1") == "1", delegate(Lyrics l)
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((Action)delegate
                {
                    if (key != lyricKey) return;
                    lyrics = l;
                    ScheduleLyric();
                    Render();
                });
            });
        }

        void ScheduleLyric()
        {
            lyricTimer.Stop();
            if (lyrics == null || track == null || !track.Playing || cur == Mode.Idle) return;
            TimeSpan pos = track.Now(), next;
            if (!lyrics.NextTime(pos, out next)) return;
            lyricTimer.Interval = (int)Math.Max(30, Math.Min(60000, (next - pos).TotalMilliseconds + 10));
            lyricTimer.Start();
        }

        string CurrentLine()
        {
            if (lyrics == null || track == null) return "";
            return lyrics.LineAt(track.Now());
        }

        void OnAnim(object s, EventArgs e)
        {
            double t = clock.Elapsed.TotalSeconds;
            float dt = (float)Math.Min(0.05, t - lastT);
            lastT = t;
            bool a = sw.Step(dt);
            bool b = sh.Step(dt);
            bool m = eyes.Step(dt);
            Render();
            if (!a && !b && !m)
            {
                anim.Stop();
                if (!trimTimer.Enabled) trimTimer.Start();
                return;
            }
            int want = (a || b) ? 10 : 33;
            if (anim.Interval != want) anim.Interval = want;
        }

        void Kick()
        {
            if (anim.Enabled) return;
            anim.Interval = 10;
            clock.Restart();
            lastT = 0;
            anim.Start();
        }

        // ---- eyes -------------------------------------------------------------------------------------------

        void Wake()
        {
            sleepTimer.Stop();
            sleepTimer.Start();
            if (asleep) { asleep = false; UpdateMood(); }
        }

        void UpdateMood()
        {
            float happy = 0, wide = 0, droop = 0;
            bool focus = false;
            if (toast != null)
            {
                if (toast.Progress >= 0 && toast.Progress < 100) focus = true;
                else wide = 1;
            }
            else if (hover) wide = 0.35f;
            else if (track != null && track.Playing) happy = 1;
            else if (track != null) droop = 0.6f;
            else if (asleep) droop = 1.7f;
            eyes.Happy.T = happy;
            eyes.Wide.T = wide;
            eyes.Droop.T = droop;
            eyes.LookX.T = focus ? 0 : eyes.LookX.T;
            eyes.LookY.T = focus ? 0.9f : eyes.LookY.T;
            lookLocked = focus;
            if (happy > 0) { if (!nodTimer.Enabled) nodTimer.Start(); } else nodTimer.Stop();
            Kick();
        }

        bool lookLocked;

        void OnLook()
        {
            if (!Visible || lookLocked) return;
            var p = Cursor.Position;
            float ex = Left + (MaxW / 2f) * S, ey = Top + 14 * S;
            float tx = Clamp01(Math.Abs(p.X - ex) / (260 * S)) * Math.Sign(p.X - ex);
            float ty = Clamp01(Math.Abs(p.Y - ey) / (260 * S)) * Math.Sign(p.Y - ey);
            if (Math.Abs(tx - eyes.LookX.T) < 0.03f && Math.Abs(ty - eyes.LookY.T) < 0.03f) return;
            eyes.LookX.T = tx;
            eyes.LookY.T = ty;
            Kick();
        }

        void ScheduleBlink()
        {
            blinkTimer.Interval = rng.Next(2500, 6500);
            blinkTimer.Start();
        }

        TrackInfo smtcTrack, localTrack;

        void OnTrack(TrackInfo t)
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke((Action)delegate { smtcTrack = t; ApplyTrack(); });
        }

        void OnLocal()
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke((Action)delegate { localTrack = LocalPlayer.I.Snapshot(); ApplyTrack(); });
        }

        // The built-in player wins while it has a song; otherwise whatever Windows reports.
        void ApplyTrack()
        {
            TrackInfo t = localTrack ?? smtcTrack;
            track = t;
            Wake();
            LoadLyrics(t);
            pause.Stop();
            pausedRecent = t != null && !t.Playing;
            if (pausedRecent) pause.Start();
            Retarget();
            Render();
        }

        void Toggle() { if (LocalPlayer.I.Active) LocalPlayer.I.Toggle(); else media.Toggle(); }
        void SkipPrev() { if (LocalPlayer.I.Active) LocalPlayer.I.Prev(); else media.Prev(); }
        void SkipNext() { if (LocalPlayer.I.Active) LocalPlayer.I.Next(); else media.Next(); }
        void SeekTo(TimeSpan t) { if (LocalPlayer.I.Active) LocalPlayer.I.Seek(t); else media.Seek(t); }
        void OnSlow()
        {
            int st;
            bool ok = Native.SHQueryUserNotificationState(out st) == 0;
            bool fs = ok && (st == 2 || st == 3 || st == 4);
            quiet = ok && st == 6;
            if (fs != fsHidden) { fsHidden = fs; ApplyVisible(); }
            if (Visible) Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, 0x13);
        }

        void ToggleHidden() { userHidden = !userHidden; ApplyVisible(); }

        public static Island Current;

        // The prompter takes over the top edge, so the island steps aside while it is open.
        public void RefreshVisible() { ApplyVisible(); }

        void ApplyVisible()
        {
            bool want = !userHidden && !fsHidden && !Teleprompter.Open;
            if (want == Visible) return;
            Visible = want;
            if (want) { CaptureBackdrop(); Render(); }
        }

        // ---- input ------------------------------------------------------------------------------------------

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            leave.Stop();
            Wake();
            if (!hover) { hover = true; Retarget(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hover) leave.Start();
        }

        // Scrolling over the island while it is closed changes the system volume.
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (cur == Mode.Expanded) return;
            byte vk = e.Delta > 0 ? (byte)0xAF : (byte)0xAE;
            Native.keybd_event(vk, 0, 0, UIntPtr.Zero);
            Native.keybd_event(vk, 0, 2, UIntPtr.Zero);
        }

        static float Dist(float x1, float y1, float x2, float y2)
        {
            return (float)Math.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || cur != Mode.Expanded || track == null) return;
            if (sh.X < ExpSize.Height - 20) return;
            float x = e.X / S, y = e.Y / S, ex = (MaxW - ExpSize.Width) / 2f, cx = ex + TextX + TextW / 2;
            if (Dist(x, y, cx, BtnY) < 20) { Ping("click"); Toggle(); }
            else if (Dist(x, y, cx - 46, BtnY) < 18) { Ping("click"); SkipPrev(); }
            else if (Dist(x, y, cx + 46, BtnY) < 18) { Ping("click"); SkipNext(); }
            else if (y > BarY - 10 && y < BarY + 14 && x >= ex + TextX && x <= ex + TextX + TextW && track.Duration > TimeSpan.Zero)
            {
                double f = (x - (ex + TextX)) / TextW;
                SeekTo(TimeSpan.FromTicks((long)(track.Duration.Ticks * f)));
            }
        }

        // ---- backdrop for the glass material ----------------------------------------------------------------

        void CaptureBackdrop()
        {
            if (!glassOk || material != "liquid" || !IsHandleCreated || frame == null || !Visible) return;
            try
            {
                int W = frame.Width, H = frame.Height;
                using (var raw = new Bitmap(W, H, PixelFormat.Format32bppRgb))
                {
                    using (var g = Graphics.FromImage(raw)) g.CopyFromScreen(Left, Top, 0, 0, new Size(W, H));
                    int w2 = Math.Max(8, W / 10), h2 = Math.Max(4, H / 10);
                    using (var small = new Bitmap(w2, h2, PixelFormat.Format32bppRgb))
                    {
                        using (var g = Graphics.FromImage(small))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(raw, new Rectangle(0, 0, w2, h2));
                        }
                        if (backdrop == null || backdrop.Width != W || backdrop.Height != H)
                        {
                            if (backdrop != null) backdrop.Dispose();
                            backdrop = new Bitmap(W, H, PixelFormat.Format32bppRgb);
                        }
                        using (var g = Graphics.FromImage(backdrop))
                        using (var ia = new ImageAttributes())
                        {
                            ia.SetWrapMode(WrapMode.TileFlipXY);
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(small, new Rectangle(0, 0, W, H), 0, 0, w2, h2, GraphicsUnit.Pixel, ia);
                        }
                    }
                }
            }
            catch { backdrop = null; }
        }

        // ---- rendering --------------------------------------------------------------------------------------

        static float Clamp01(float v) { return v < 0 ? 0 : v > 1 ? 1 : v; }
        static Color Wh(float a, int baseAlpha) { return Color.FromArgb((int)(baseAlpha * Clamp01(a)), 255, 255, 255); }

        internal static GraphicsPath NotchPath(float x, float w, float h, float r)
        {
            r = Math.Min(r, Math.Min(w, h) / 2);
            const float top = -2;
            var p = new GraphicsPath();
            p.AddLine(x, top, x + w, top);
            p.AddLine(x + w, top, x + w, h - r);
            p.AddArc(x + w - 2 * r, h - 2 * r, 2 * r, 2 * r, 0, 90);
            p.AddLine(x + w - r, h, x + r, h);
            p.AddArc(x, h - 2 * r, 2 * r, 2 * r, 90, 90);
            p.CloseFigure();
            return p;
        }

        static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            float d = rad * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        void Render()
        {
            if (!IsHandleCreated || !Visible || frame == null) return;
            float w = sw.X, h = sh.X, x0 = (MaxW - w) / 2f;
            using (var g = Graphics.FromImage(frame))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.ScaleTransform(S, S);
                using (var path = NotchPath(x0, w, h, 22))
                {
                    DrawMaterial(g, path, x0, w, h);
                    g.SetClip(path);
                    DrawContent(g, x0, w, h);
                    g.ResetClip();
                }
            }
            Present();
        }

        void DrawMaterial(Graphics g, GraphicsPath p, float x0, float w, float h)
        {
            var box = new RectangleF(x0, -2, w, h + 2);
            bool liquid = material == "liquid" && backdrop != null;
            if (liquid)
            {
                using (var tb = new TextureBrush(backdrop))
                {
                    tb.ScaleTransform(1f / S, 1f / S);
                    g.FillPath(tb, p);
                }
                using (var b = new SolidBrush(Color.FromArgb(128, 10, 12, 18))) g.FillPath(b, p);
                using (var sheen = new LinearGradientBrush(box, Color.FromArgb(52, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                    g.FillPath(sheen, p);
            }
            else
            {
                using (var b = new SolidBrush(Color.FromArgb(255, 6, 6, 8))) g.FillPath(b, p);
            }
            int a0 = liquid ? 120 : 46, a1 = liquid ? 26 : 12;
            using (var rim = new LinearGradientBrush(box, Color.FromArgb(a0, 255, 255, 255), Color.FromArgb(a1, 255, 255, 255), 90f))
            using (var pen = new Pen(rim, 1f))
                g.DrawPath(pen, p);
        }

        void DrawContent(Graphics g, float x0, float w, float h)
        {
            if (cur == Mode.Toast && toast != null)
            {
                float ta = Clamp01((h - 30) / 24) * Clamp01((w - 180) / 100);
                if (ta > 0.02f) DrawToast(g, x0, w, ta);
                return;
            }
            float tExp = Clamp01((h - CompSize.Height) / (ExpSize.Height - CompSize.Height));
            float tCmp = Clamp01((w - 120) / (CompSize.Width - 120)) * (1 - tExp);
            float tIdle = (1 - Clamp01((w - 100) / 50)) * (1 - tExp);
            eyes.Draw(g, MaxW / 2f, 12, 1.2f, tIdle);
            if (tCmp > 0.02f && track != null) DrawCompact(g, x0, w, tCmp);
            if (tExp > 0.02f) DrawExpanded(g, x0, tExp * tExp);
        }

        void DrawCompact(Graphics g, float x0, float w, float a)
        {
            DrawArt(g, new RectangleF(x0 + 10, 5, 22, 22), 5, a);
            string line = track.Playing ? CurrentLine() : "";
            using (var b = new SolidBrush(Wh(a, 240)))
                g.DrawString(line.Length > 0 ? line : track.Title, fComp, b, new RectangleF(x0 + 40, 8, w - 40 - 44, 18), sfNear);
            eyes.Draw(g, x0 + w - 27, 16, 0.85f, a);
        }

        void DrawToast(Graphics g, float x0, float w, float a)
        {
            using (var b = new SolidBrush(Wh(a, 250)))
                g.DrawString(toast.Title, fSub, b, new RectangleF(x0 + 18, 8, w - 80, 18), sfNear);
            using (var b = new SolidBrush(Wh(a, 165)))
                g.DrawString(toast.Body, fComp, b, new RectangleF(x0 + 18, 26, w - 80, 18), sfNear);
            eyes.Draw(g, x0 + w - 36, 20, 0.95f, a);
            if (toast.Progress >= 0)
            {
                using (var b = new SolidBrush(Wh(a, 55)))
                    g.FillPath(b, RoundRect(new RectangleF(x0 + 18, 46, w - 36, 3), 1.5f));
                if (toast.Progress > 0)
                    using (var b = new SolidBrush(Wh(a, 235)))
                        g.FillPath(b, RoundRect(new RectangleF(x0 + 18, 46, Math.Max(3, (w - 36) * toast.Progress / 100f), 3), 1.5f));
            }
        }

        void DrawExpanded(Graphics g, float ex, float a)
        {
            eyes.Draw(g, ex + ExpSize.Width - 36, 20, 0.95f, a);
            if (track == null)
            {
                var now = DateTime.Now;
                CultureInfo ci;
                try { ci = new CultureInfo("id-ID"); } catch { ci = CultureInfo.InvariantCulture; }
                using (var b = new SolidBrush(Wh(a, 245)))
                    g.DrawString(now.ToString("HH:mm"), fClock, b, new RectangleF(ex, 34, ExpSize.Width, 50), sfMid);
                using (var b = new SolidBrush(Wh(a, 140)))
                    g.DrawString(now.ToString("dddd, d MMMM", ci), fSub, b, new RectangleF(ex, 86, ExpSize.Width, 20), sfMid);
                return;
            }

            DrawArt(g, new RectangleF(ex + 18, 22, 96, 96), 14, a);
            float tx = ex + TextX;
            using (var b = new SolidBrush(Wh(a, 250)))
                g.DrawString(track.Title, fTitle, b, new RectangleF(tx, 20, TextW - 50, 22), sfNear);
            using (var b = new SolidBrush(Wh(a, 160)))
                g.DrawString(track.Artist, fSub, b, new RectangleF(tx, 43, TextW, 20), sfNear);
            string lyric = CurrentLine();
            if (lyric.Length > 0)
                using (var b = new SolidBrush(Wh(a, 215)))
                    g.DrawString(lyric, fComp, b, new RectangleF(tx, 57, TextW, 18), sfNear);

            TimeSpan pos = track.Now(), dur = track.Duration;
            float frac = dur > TimeSpan.Zero ? (float)Math.Min(1.0, pos.TotalSeconds / dur.TotalSeconds) : 0f;
            using (var b = new SolidBrush(Wh(a, 55)))
                g.FillPath(b, RoundRect(new RectangleF(tx, BarY, TextW, 4), 2));
            if (frac > 0.002f)
                using (var b = new SolidBrush(Wh(a, 235)))
                    g.FillPath(b, RoundRect(new RectangleF(tx, BarY, Math.Max(4, TextW * frac), 4), 2));
            using (var b = new SolidBrush(Wh(a, 130)))
            {
                g.DrawString(Fmt(pos), fSmall, b, new RectangleF(tx, BarY + 8, 60, 14), sfNear);
                if (dur > TimeSpan.Zero)
                    g.DrawString(Fmt(dur), fSmall, b, new RectangleF(tx + TextW - 60, BarY + 8, 60, 14), sfFar);
            }

            float cx = tx + TextW / 2;
            using (var b = new SolidBrush(Wh(a, 235)))
            {
                DrawSkip(g, b, cx - 46, BtnY, -1);
                DrawSkip(g, b, cx + 46, BtnY, 1);
            }
            using (var b = new SolidBrush(Wh(a, 245))) g.FillEllipse(b, cx - 17, BtnY - 17, 34, 34);
            using (var b = new SolidBrush(Color.FromArgb((int)(255 * Clamp01(a)), 12, 12, 16)))
            {
                if (track.Playing)
                {
                    g.FillRectangle(b, cx - 6, BtnY - 7, 4, 14);
                    g.FillRectangle(b, cx + 2, BtnY - 7, 4, 14);
                }
                else
                {
                    g.FillPolygon(b, new[] { new PointF(cx - 4, BtnY - 8), new PointF(cx - 4, BtnY + 8), new PointF(cx + 8, BtnY) });
                }
            }
        }

        static void DrawSkip(Graphics g, Brush b, float cx, float cy, int dir)
        {
            g.FillPolygon(b, new[]
            {
                new PointF(cx - dir * 5, cy - 8), new PointF(cx - dir * 5, cy + 8), new PointF(cx + dir * 7, cy)
            });
            g.FillRectangle(b, dir > 0 ? cx + 7 : cx - 10, cy - 8, 3, 16);
        }

        static string Fmt(TimeSpan t)
        {
            return t.TotalHours >= 1
                ? string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds)
                : string.Format("{0}:{1:00}", t.Minutes, t.Seconds);
        }

        void DrawArt(Graphics g, RectangleF r, float rad, float a)
        {
            using (var path = RoundRect(r, rad))
            {
                if (track != null && track.Thumb != null)
                {
                    var img = track.Thumb;
                    var cm = new ColorMatrix();
                    cm.Matrix33 = Clamp01(a);
                    using (var ia = new ImageAttributes())
                    {
                        ia.SetColorMatrix(cm);
                        using (var tb = new TextureBrush(img, new Rectangle(0, 0, img.Width, img.Height), ia))
                        {
                            tb.ResetTransform();
                            tb.TranslateTransform(r.X, r.Y);
                            tb.ScaleTransform(r.Width / img.Width, r.Height / img.Height);
                            g.FillPath(tb, path);
                        }
                    }
                }
                else
                {
                    using (var lg = new LinearGradientBrush(r, Color.FromArgb((int)(255 * a), 92, 70, 160), Color.FromArgb((int)(255 * a), 28, 36, 80), 55f))
                        g.FillPath(lg, path);
                    if (r.Width > 40)
                        using (var b = new SolidBrush(Wh(a, 220)))
                            g.DrawString("♪", fNote, b, r, sfMid);
                }
            }
        }

        void Present()
        {
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memDc = Native.CreateCompatibleDC(screenDc);
            IntPtr hBmp = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                hBmp = frame.GetHbitmap(Color.FromArgb(0));
                old = Native.SelectObject(memDc, hBmp);
                var size = new Native.SIZE { cx = frame.Width, cy = frame.Height };
                var src = new Native.POINT();
                var dst = new Native.POINT { x = Left, y = Top };
                var bf = new Native.BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref bf, 2);
            }
            finally
            {
                if (old != IntPtr.Zero) Native.SelectObject(memDc, old);
                if (hBmp != IntPtr.Zero) Native.DeleteObject(hBmp);
                Native.DeleteDC(memDc);
                Native.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        // ---- tray and menu ----------------------------------------------------------------------------------

        void BuildTray()
        {
            menu = new ContextMenuStrip();
            miHide = new ToolStripMenuItem();
            miHide.Click += delegate { ToggleHidden(); };
            miLiquid = new ToolStripMenuItem("Liquid Glass");
            miLiquid.Click += delegate { SetMaterial("liquid"); };
            miSolid = new ToolStripMenuItem("Hitam pekat");
            miSolid.Click += delegate { SetMaterial("solid"); };
            var mat = new ToolStripMenuItem("Material");
            mat.DropDownItems.AddRange(new ToolStripItem[] { miLiquid, miSolid });
            miLyrics = new ToolStripMenuItem("Cari lirik online (LRCLIB)");
            miLyrics.Click += delegate
            {
                Settings.Set("lyrics.online", Settings.Get("lyrics.online", "1") == "1" ? "0" : "1");
            };
            miSound = new ToolStripMenuItem("Efek suara");
            miSound.Click += delegate { Settings.Set("sound", Settings.Get("sound", "1") == "1" ? "0" : "1"); };
            var miMusic = new ToolStripMenuItem("Musik YouTube...");
            miMusic.Click += delegate { MusicWindow.ShowWindow(); };
            var miPrompter = new ToolStripMenuItem("Teleprompter...");
            miPrompter.Click += delegate { Teleprompter.ShowEditor(); };
            miAuto = new ToolStripMenuItem("Mulai bersama Windows");
            miAuto.Click += delegate { SetAutostart(!IsAutostart()); };
            var miFolder = new ToolStripMenuItem("Buka folder data");
            miFolder.Click += delegate
            {
                try { Directory.CreateDirectory(Settings.Dir); Process.Start(Settings.Dir); } catch { }
            };
            var miQuit = new ToolStripMenuItem("Keluar");
            miQuit.Click += delegate { Application.Exit(); };
            menu.Items.AddRange(new ToolStripItem[] { miHide, miMusic, miPrompter, mat, miLyrics, miSound, miAuto, miFolder, new ToolStripSeparator(), miQuit });
            menu.Opening += delegate
            {
                miHide.Text = (userHidden ? "Tampilkan island" : "Sembunyikan island") + "\tCtrl+Alt+H";
                miLiquid.Checked = material == "liquid";
                miSolid.Checked = material != "liquid";
                miLyrics.Checked = Settings.Get("lyrics.online", "1") == "1";
                miSound.Checked = Settings.Get("sound", "1") == "1";
                miAuto.Checked = IsAutostart();
            };
            ContextMenuStrip = menu;

            tray = new NotifyIcon();
            tray.Icon = MakeIcon();
            tray.Text = "Notchira";
            tray.ContextMenuStrip = menu;
            tray.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ToggleHidden(); };
            tray.Visible = true;
        }

        void SetMaterial(string m)
        {
            material = m;
            Settings.Set("material", m);
            CaptureBackdrop();
            Render();
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        static bool IsAutostart()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("Notchira") != null;
            }
            catch { return false; }
        }

        static void SetAutostart(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (on) k.SetValue("Notchira", "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue("Notchira", false);
                }
            }
            catch { }
        }

        static Icon MakeIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = NotchPath(3, 26, 17, 8))
                {
                    g.TranslateTransform(0, 7);
                    using (var b = new SolidBrush(Color.FromArgb(245, 245, 247))) g.FillPath(b, path);
                    using (var b = new SolidBrush(Color.FromArgb(30, 30, 36))) g.FillEllipse(b, 12, 7, 8, 8);
                }
                IntPtr h = bmp.GetHicon();
                var ic = (Icon)Icon.FromHandle(h).Clone();
                Native.DestroyIcon(h);
                return ic;
            }
        }

        // ---- dev hooks: NOTCHIRA_DEMO=idle|compact|expanded, NOTCHIRA_DUMP=<folder> ----------------------------

        // Dev hook: install yt-dlp if needed, download NOTCHIRA_TEST_URL, play it, log and dump a frame.
        void DemoMusic()
        {
            string dir = Environment.GetEnvironmentVariable("NOTCHIRA_DUMP");
            Directory.CreateDirectory(dir);
            Action<string> log = s => File.AppendAllText(Path.Combine(dir, "music.log"), s + Environment.NewLine);
            string testUrl = Environment.GetEnvironmentVariable("NOTCHIRA_TEST_URL");
            var ctx = System.Threading.SynchronizationContext.Current;
            Action finish = delegate { LocalPlayer.I.Stop(); Application.Exit(); };
            Action<Song> play = delegate(Song song)
            {
                Library.Add(song);
                LocalPlayer.I.PlayList(Library.All(), 0);
                hover = true;
                Retarget();
                var t = new Timer { Interval = 1500 };
                int step = 0;
                t.Tick += delegate
                {
                    step++;
                    var s = LocalPlayer.I.Snapshot();
                    log("step " + step + ": active=" + LocalPlayer.I.Active + " playing=" + (s != null && s.Playing) +
                        " pos=" + (s == null ? "-" : s.Position.TotalSeconds.ToString("0.0")) + " dur=" + (s == null ? "-" : s.Duration.TotalSeconds.ToString("0.0")));
                    if (step == 1) LocalPlayer.I.Toggle();
                    if (step == 2) LocalPlayer.I.Toggle();
                    if (step == 3) { LocalPlayer.I.Seek(TimeSpan.FromSeconds(10)); }
                    if (step == 4) { Render(); frame.Save(Path.Combine(dir, "frame-music.png"), ImageFormat.Png); }
                    if (step >= 5) { t.Stop(); finish(); }
                };
                t.Start();
            };
            Action dl = delegate
            {
                YtDlp.Download(testUrl, p => { }, (song, err) => ctx.Post(_ =>
                {
                    if (song == null) { log("download failed: " + err); finish(); return; }
                    log("downloaded: " + song.Title + " / " + song.Artist + " / " + song.Seconds + "s, thumb=" + File.Exists(song.ThumbPath));
                    play(song);
                }, null));
            };
            if (!YtDlp.Installed)
                YtDlp.Install(m => log(m), (ok, m) => ctx.Post(_ => { log("install: " + m); if (ok) dl(); else finish(); }, null));
            else dl();
        }

        void StartDemo(string mode)
        {
            if (mode == "musicwin") { MusicWindow.Demo(Environment.GetEnvironmentVariable("NOTCHIRA_DUMP")); return; }
            if (mode == "music") { DemoMusic(); return; }
            if (mode == "musicplay")
            {
                string dir2 = Environment.GetEnvironmentVariable("NOTCHIRA_DUMP");
                LocalPlayer.I.PlayList(Library.All(), 0);
                var proc = System.Diagnostics.Process.GetCurrentProcess();
                TimeSpan cpu0 = TimeSpan.Zero;
                int n = 0;
                var mt = new Timer { Interval = 10000 };
                mt.Tick += delegate
                {
                    n++;
                    proc.Refresh();
                    if (n == 1) cpu0 = proc.TotalProcessorTime;
                    if (n == 4)
                    {
                        double ms = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
                        File.WriteAllText(Path.Combine(dir2, "musicplay.log"), string.Format("playing 30s: cpu={0:N0} ms ({1:N2}% of one core), WS={2} MB, private={3} MB{4}",
                            ms, ms / 300.0, proc.WorkingSet64 / 1048576, proc.PrivateMemorySize64 / 1048576, Environment.NewLine));
                        LocalPlayer.I.Stop();
                        Application.Exit();
                    }
                };
                mt.Start();
                return;
            }
            if (mode == "editor")
            {
                Teleprompter.DemoEditor(Environment.GetEnvironmentVariable("NOTCHIRA_DUMP"));
                return;
            }
            if (mode == "prompter")
            {
                Teleprompter.Demo(Environment.GetEnvironmentVariable("NOTCHIRA_DUMP"));
                return;
            }
            if (mode == "sleepy") { asleep = true; UpdateMood(); }
            if (mode != "idle" && mode != "toast" && mode != "sleepy")
            {
                bool real = mode == "lyrics";
                track = new TrackInfo
                {
                    Title = real ? "Shape of You" : "Aurora Borealis",
                    Artist = real ? "Ed Sheeran" : "Notchira Sound Lab",
                    Album = real ? "÷ (Divide)" : "",
                    Playing = true,
                    Position = TimeSpan.FromSeconds(real ? 60 : 45),
                    Duration = TimeSpan.FromSeconds(real ? 233 : 192),
                    Stamp = DateTime.UtcNow
                };
                LoadLyrics(track);
            }
            var t = new Timer { Interval = 700 };
            t.Tick += delegate
            {
                t.Stop();
                if (mode == "expanded" || mode == "lyrics") { hover = true; }
                Retarget();
                var done = new Timer { Interval = mode == "lyrics" ? 9000 : 2200 };
                done.Tick += delegate
                {
                    done.Stop();
                    CaptureBackdrop();
                    Render();
                    string dir = Environment.GetEnvironmentVariable("NOTCHIRA_DUMP");
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                        frame.Save(Path.Combine(dir, "frame-" + mode + ".png"), ImageFormat.Png);
                        foreach (string n in Sounds.Names) File.WriteAllBytes(Path.Combine(dir, n + ".wav"), Sounds.Synth(n));
                    }
                    Application.Exit();
                };
                done.Start();
            };
            t.Start();
        }
    }
}
