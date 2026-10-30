using System;
using System.Collections.Generic;
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
    sealed partial class Island : Form
    {
        const float MaxW = 460, MaxH = 360;
        static readonly SizeF IdleSize = new SizeF(96, 24);
        static readonly SizeF MiniSize = new SizeF(56, 11);
        static readonly SizeF CompSize = new SizeF(250, 32);
        static readonly SizeF ExpSize = new SizeF(440, 140);
        const float TextX = 130, TextW = 292;

        // With synced lyrics the open panel grows to fit a three-line block between the artist and the progress bar.
        float LyrOff { get { return lyrics != null ? 44f : 0f; } }
        float ExpH { get { return ExpSize.Height + LyrOff + TabH; } }
        float BarY { get { return 78 + LyrOff + TabH; } }
        float BtnY { get { return 118 + LyrOff + TabH; } }

        static readonly SizeF ToastSize = new SizeF(300, 56);

        enum Mode { Idle, Compact, Toast, Expanded, Mini }

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
        readonly Timer miniTimer = new Timer();
        bool miniOn, covered;
        int coverVotes, wakeVotes;
        long holdUntil, lastActivity = Environment.TickCount;
        uint myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
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

        // Accent colour: tints the thin rim and the selected tab and pills. Neutral (silver) is the default; two colours make the rim gradient.
        static readonly string[] AccentIds = { "neutral", "blue", "violet", "purple", "emerald", "orange" };
        static readonly string[] AccentNames = { "Netral", "Electric Blue", "Violet", "Purple", "Emerald", "Orange" };
        static readonly Color[] AccentA = { Color.FromArgb(236, 240, 248), Color.FromArgb(70, 140, 255), Color.FromArgb(200, 120, 255), Color.FromArgb(140, 92, 255), Color.FromArgb(52, 211, 153), Color.FromArgb(255, 164, 64) };
        static readonly Color[] AccentB = { Color.FromArgb(150, 160, 180), Color.FromArgb(150, 96, 255), Color.FromArgb(245, 120, 215), Color.FromArgb(86, 124, 255), Color.FromArgb(40, 190, 235), Color.FromArgb(255, 100, 130) };

        static int AccentIndex
        {
            get { int i = Array.IndexOf(AccentIds, Settings.Get("accent", "neutral")); return i < 0 ? 0 : i; }
        }
        static Color AccA { get { return AccentA[AccentIndex]; } }
        static Color AccB { get { return AccentB[AccentIndex]; } }
        // the accent mixed with white: the fill of a selected pill, which keeps dark text
        static Color AccLight(float a)
        {
            Color c = AccA;
            return Color.FromArgb((int)(240 * Clamp01(a)), c.R + (255 - c.R) * 55 / 100, c.G + (255 - c.G) * 55 / 100, c.B + (255 - c.B) * 55 / 100);
        }
        int glassTint = 140;   // alpha of the dark layer over the glass, raised on bright backgrounds

        NotifyIcon tray;
        ContextMenuStrip menu;
        ToolStripMenuItem miHide;

        static readonly Font fTitle = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel);
        static readonly Font fLyric = new Font("Segoe UI Semibold", 14.5f, FontStyle.Regular, GraphicsUnit.Pixel);
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
            AllowDrop = true;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Text = "Notchira";
            material = Settings.Get("material", "liquid");
            Current = this;

            BuildTray();
            anim.Tick += OnAnim;
            tick.Tick += delegate { tickCount++; if (tickCount % 2 == 0) CaptureBackdrop(true); Render(); };
            leave.Tick += delegate
            {
                leave.Stop();
                if (Typing || InsideIsland(Cursor.Position)) return;
                if (Environment.TickCount < openGrace) { leave.Start(); return; }
                hover = false;
                HoldThenMini();
                Retarget();
            };
            miniTimer.Tick += delegate { miniTimer.Stop(); Retarget(); };
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
            Secrets.Switched += delegate(string prov, string from, string to)
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((Action)delegate
                {
                    aiStatus = "Kunci " + prov + " \"" + from + "\" kena batas" + (to != null ? ", pindah ke \"" + to + "\"." : ".");
                    ShowToast(new Notify.Message { Title = "Kunci " + prov + " kena batas", Body = to != null ? "Pindah ke \"" + to + "\"" : "Tidak ada kunci cadangan" });
                });
            };
            SongLyrics.Updated += delegate(string id)
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((Action)delegate
                {
                    if (track != null && track.SongId == id) { lyricKey = null; LoadLyrics(track); }
                });
            };
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

        // Dropping music files on the island adds them to the library (Pustaka tab). The island opens that tab
        // as soon as files are dragged over it. Not available in Mini mode, which is click-through.
        static bool HasFiles(DragEventArgs e) { return e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop); }

        protected override void OnDragEnter(DragEventArgs e)
        {
            if (HasFiles(e))
            {
                e.Effect = DragDropEffects.Copy;
                if (page != Page.Library || cur != Mode.Expanded) OpenPage(Page.Library, false);
            }
            else e.Effect = DragDropEffects.None;
        }

        protected override void OnDragOver(DragEventArgs e)
        {
            e.Effect = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
            if (HasFiles(e)) { openGrace = Environment.TickCount + 1500; leave.Stop(); }
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            if (!HasFiles(e)) return;
            string[] paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            BeginInvoke((Action)delegate { if (!IsDisposed) ImportPaths(paths); });
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            glassOk = Native.ExcludeFromCapture(Handle);
            Native.HideNewWindowsFromCapture();
            Native.RegisterHotKey(Handle, 1, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, 0x48);
            Native.RegisterHotKey(Handle, 2, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, 0x41);
            Native.RegisterHotKey(Handle, 3, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, 0x4B);
            Native.RegisterHotKey(Handle, 4, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, 0x56);
            Setup();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            slow.Start();
            InitAutostart();
            InitStartShortcut();
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
            Native.UnregisterHotKey(Handle, 2);
            Native.UnregisterHotKey(Handle, 3);
            Native.UnregisterHotKey(Handle, 4);
            StopVoice();
            tray.Visible = false;
            tray.Dispose();
            base.OnFormClosed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY)
            {
                int id = (int)m.WParam;
                if (id == 2) OpenPage(Page.Ai, true);
                else if (id == 3) ToggleMini();
                else if (id == 4) { if (voice == null) OpenPage(Page.Ai, false); ToggleVoice(); }
                else ToggleHidden();
                return;
            }
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
            if (voice != null) return Mode.Compact;
            if (miniOn && Environment.TickCount > holdUntil) return Mode.Mini;
            if (track != null && (track.Playing || pausedRecent) && Settings.Get("music.compact", "text") == "text") return Mode.Compact;
            return Mode.Idle;
        }

        void Retarget()
        {
            var m = Target();
            if (m != cur)
            {
                long now = Environment.TickCount;
                if (cur == Mode.Expanded) closedAt = now;
                else if (m == Mode.Expanded && now - closedAt > 20000) { page = Page.Music; OnPageShown(); }
                cur = m;
                if (IsHandleCreated) Native.SetClickThrough(Handle, m == Mode.Mini);
                CaptureBackdrop();
                tick.Enabled = m == Mode.Expanded;
                tickCount = 0;
            }
            SizeF z = m == Mode.Mini ? MiniSize : m == Mode.Idle ? IdleSize : m == Mode.Compact ? CompSize : m == Mode.Toast ? ToastSize : new SizeF(ExpSize.Width, PageH);
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
            if (miniOn && Settings.Get("mini.notif", "1") != "1") return;
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
            string key = t == null ? null : (t.SongId != null ? "song:" + t.SongId : t.Title + "|" + t.Artist);
            if (key == lyricKey) return;
            lyricKey = key;
            SetLyrics(null);
            if (t == null || Settings.Get("lyrics", "1") != "1") return;
            if (t.SongId != null) { ResolveSong(t.SongId, key); return; }
            string title = t.Title, artist = t.Artist, album = t.Album;
            Lyrics.Fetch(title, artist, album, t.Duration, Settings.Get("lyrics.online", "1") == "1", delegate(Lyrics l)
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((Action)delegate
                {
                    if (key != lyricKey) return;
                    SetLyrics(l);
                    ScheduleLyric();
                    Render();
                });
            });
        }

        // Songs from the built-in player: cache, LRCLIB, YouTube captions, then a transcription if a key is stored.
        void ResolveSong(string id, string key)
        {
            Song s = Library.Find(id);
            if (s == null) return;
            SongLyrics.Resolve(s, false, delegate(Lyrics l, string source, bool fresh)
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((Action)delegate
                {
                    if (key != lyricKey) return;
                    SetLyrics(l);
                    ScheduleLyric();
                    Render();
                    if (fresh && l != null && source != null)
                        ShowToast(new Notify.Message { Title = "Teks lagu ditemukan", Body = source });
                });
            });
        }

        void SetLyrics(Lyrics l)
        {
            bool resize = (lyrics != null) != (l != null);
            lyrics = l;
            if (resize && cur == Mode.Expanded) Retarget();
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
            lastActivity = Environment.TickCount;
            sleepTimer.Stop();
            sleepTimer.Start();
            if (asleep) { asleep = false; UpdateMood(); }
        }

        void UpdateMood()
        {
            float happy = 0, wide = 0, droop = 0;
            bool focus = false;
            var vs = voice != null ? voice.State : VoiceSession.St.Ended;
            if (thinking || vs == VoiceSession.St.Thinking || vs == VoiceSession.St.Connecting) focus = true;
            else if (vs == VoiceSession.St.Speaking) happy = 1;
            else if (vs == VoiceSession.St.Listening) wide = 0.6f;
            else if (toast != null)
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

        bool lookLocked, thinking;

        void OnLook()
        {
            if (!Visible) return;
            var p = Cursor.Position;
            UpdateMiniState(p);
            if (lookLocked) return;
            float ex = Left + (MaxW / 2f) * S, ey = Top + 14 * S;
            float tx = Clamp01(Math.Abs(p.X - ex) / (260 * S)) * Math.Sign(p.X - ex);
            float ty = Clamp01(Math.Abs(p.Y - ey) / (260 * S)) * Math.Sign(p.Y - ey);
            if (Math.Abs(tx - eyes.LookX.T) < 0.03f && Math.Abs(ty - eyes.LookY.T) < 0.03f) return;
            eyes.LookX.T = tx;
            eyes.LookY.T = ty;
            Kick();
        }

        // ---- mini mode --------------------------------------------------------------------------------------
        // The island shrinks to a click-through sliver at the top edge so it never covers a tab bar. Normal keeps it
        // as it is, Mini always shrinks, Auto shrinks while a window covers the top edge or after a minute of calm.

        bool MiniWanted()
        {
            if (voice != null) return false;
            string m = Settings.Get("mini", "auto");
            if (m == "mini") return true;
            if (m == "normal") return false;
            if (covered) return true;
            return Environment.TickCount - lastActivity > 60000 && !thinking && toast == null;
        }

        // Runs from the 10 Hz look timer, because a click-through window gets no mouse events of its own.
        void UpdateMiniState(Point cursor)
        {
            if (Settings.Get("mini", "auto") == "auto")
            {
                bool c = TopCovered();
                coverVotes = c == covered ? 0 : coverVotes + 1;
                if (coverVotes >= 5) { covered = c; coverVotes = 0; }
            }
            bool want = MiniWanted();
            if (want != miniOn)
            {
                miniOn = want;
                if (!miniOn) holdUntil = 0;
                Retarget();
            }

            if (cur == Mode.Mini)
            {
                float cx = Left + (MaxW / 2f) * S;
                bool zone = cursor.Y <= Top + MiniSize.Height * S && Math.Abs(cursor.X - cx) <= (MiniSize.Width / 2f) * S;
                wakeVotes = zone ? wakeVotes + 1 : 0;
                if (wakeVotes >= 5)
                {
                    wakeVotes = 0;
                    hover = true;
                    holdUntil = 0;
                    Wake();
                    Retarget();
                }
            }
            else if (hover)
            {
                // the pointer may never have entered the window (it grew under a resting cursor)
                float x0 = Left + ((MaxW - sw.X) / 2f) * S;
                bool inside = cursor.X >= x0 && cursor.X <= x0 + sw.X * S && cursor.Y <= Top + sh.X * S;
                if (inside || Typing || Environment.TickCount < openGrace) leave.Stop(); else if (!leave.Enabled) leave.Start();
            }
        }

        void HoldThenMini()
        {
            if (!miniOn) return;
            holdUntil = Environment.TickCount + 1500;
            miniTimer.Stop();
            miniTimer.Interval = 1600;
            miniTimer.Start();
        }

        void SetMiniSetting(string mode)
        {
            Settings.Set("mini", mode);
            if (mode == "mini") { hover = false; leave.Stop(); holdUntil = 0; }
            lastActivity = Environment.TickCount;
            UpdateMiniState(Cursor.Position);
            Retarget();
        }

        void ToggleMini() { SetMiniSetting(miniOn ? "normal" : "mini"); }

        // True when a window from another app reaches the top edge over the island.
        bool TopCovered()
        {
            IntPtr h = Native.GetForegroundWindow();
            if (h == IntPtr.Zero || h == Handle) return false;
            uint pid;
            Native.GetWindowThreadProcessId(h, out pid);
            if (pid == myPid || !Native.IsWindowVisible(h) || Native.IsIconic(h)) return false;
            var cls = new System.Text.StringBuilder(64);
            Native.GetClassName(h, cls, 64);
            string c = cls.ToString();
            if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd") return false;
            Native.RECT r;
            if (!Native.GetWindowRect(h, out r)) return false;
            var b = Screen.PrimaryScreen.Bounds;
            float cx = b.Left + b.Width / 2f;
            return r.Top <= b.Top + 2 && r.Left <= cx && r.Right >= cx;
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
            bool want = !userHidden && !fsHidden && !Teleprompter.Open && !Drop.AnyOpen;
            if (want == Visible) return;
            if (!want)
            {
                hover = false;
                leave.Stop();
                if (input != null && input.Visible) { SaveDraft(); input.Hide(); }
                Retarget();
            }
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
            if (hot != null) { hot = null; Render(); }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Hit h = HitAt(new PointF(e.X / S, e.Y / S));
            string id = h == null ? null : h.Id;
            Cursor = h == null ? Cursors.Default : Cursors.Hand;
            if (id != hot) { hot = id; Render(); }
        }

        // Scrolling over the island while it is closed changes the volume; while open it scrolls the page. While Notchira's own
        // player is playing only its volume changes, so a meeting call keeps its level; otherwise it is the system volume.
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (cur == Mode.Expanded) { ScrollPage(e.Delta); return; }
            if (LocalPlayer.I.Active)
            {
                int v = LocalPlayer.I.AdjustVolume(e.Delta > 0 ? 5 : -5);
                ShowToast(new Notify.Message { Title = "Volume musik " + v + "%", Body = "Hanya musik Notchira, suara lain tidak ikut" });
                return;
            }
            byte vk = e.Delta > 0 ? (byte)0xAF : (byte)0xAE;
            Native.keybd_event(vk, 0, 0, UIntPtr.Zero);
            Native.keybd_event(vk, 0, 2, UIntPtr.Zero);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right) { OpenPage(Page.Setup, false); return; }
            if (e.Button != MouseButtons.Left || cur != Mode.Expanded) return;
            var p = new PointF(e.X / S, e.Y / S);
            Hit h = HitAt(p);
            if (h == null) return;
            Wake();
            h.Do(p);
            Render();
        }

        // ---- backdrop for the glass material ----------------------------------------------------------------

        // Where the screen behind the island comes from. When Windows lets the island hide itself from screen capture
        // (glassOk) the screen is simply copied. Some Windows 10 builds refuse that for a layered window (error 8), so
        // there the island is blanked for a moment (about 60 ms), the screen is copied, and the island is drawn again.
        // That only happens when the panel opens, never on the periodic live refresh, so it does not flicker.
        Bitmap blankFrame;

        void CaptureBackdrop(bool live = false)
        {
            // dev hook: NOTCHIRA_BACKDROP=<png> stands in for the screen behind the island (for dumped frames)
            string fake = Environment.GetEnvironmentVariable("NOTCHIRA_BACKDROP");
            bool useFake = !string.IsNullOrEmpty(fake) && File.Exists(fake);
            if (material != "liquid" || !IsHandleCreated || frame == null) return;
            if (!useFake && !Visible) return;
            bool blank = !useFake && !glassOk;
            if (blank && (live || (cur != Mode.Expanded && cur != Mode.Toast))) return;
            try
            {
                int W = frame.Width, H = frame.Height;
                using (var raw = new Bitmap(W, H, PixelFormat.Format32bppRgb))
                {
                    using (var g = Graphics.FromImage(raw))
                    {
                        if (useFake) using (var img = Image.FromFile(fake)) g.DrawImage(img, 0, 0, W, H);
                        else if (blank)
                        {
                            if (blankFrame == null || blankFrame.Width != W || blankFrame.Height != H)
                            {
                                if (blankFrame != null) blankFrame.Dispose();
                                blankFrame = new Bitmap(W, H, PixelFormat.Format32bppPArgb);
                            }
                            Present(blankFrame);
                            System.Threading.Thread.Sleep(60);
                            try { g.CopyFromScreen(Left, Top, 0, 0, new Size(W, H)); }
                            finally { Present(); }
                        }
                        else g.CopyFromScreen(Left, Top, 0, 0, new Size(W, H));
                    }
                    int w2 = Math.Max(8, W / 24), h2 = Math.Max(4, H / 24);   // coarse on purpose: a smooth frost, not shapes from the window behind
                    using (var small = new Bitmap(w2, h2, PixelFormat.Format32bppRgb))
                    {
                        using (var g = Graphics.FromImage(small))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(raw, new Rectangle(0, 0, w2, h2));
                        }
                        // the brighter the screen behind, the darker the tint, so white text stays readable
                        // measured at the 90th percentile, not the mean: one bright window behind the island is enough to
                        // lose the text contrast there
                        var lums = new List<double>();
                        for (int py = 0; py < h2; py++)
                            for (int px = 0; px < w2; px++)
                            {
                                Color c = small.GetPixel(px, py);
                                lums.Add((0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0);
                            }
                        lums.Sort();
                        double lum = lums.Count > 0 ? lums[(int)(lums.Count * 0.9)] : 0;
                        glassTint = 140 + (int)(Math.Max(0, Math.Min(1, (lum - 0.25) / 0.35)) * 80);
                        if (backdrop == null || backdrop.Width != W || backdrop.Height != H)
                        {
                            if (backdrop != null) backdrop.Dispose();
                            backdrop = new Bitmap(W, H, PixelFormat.Format32bppRgb);
                        }
                        using (var g = Graphics.FromImage(backdrop))
                        using (var ia = new ImageAttributes())
                        {
                            ia.SetWrapMode(WrapMode.TileFlipXY);
                            // frosted look: colours pushed up and a little lighter than the screen behind
                            const float sat = 1.6f, lift = 0.05f;
                            float r = 0.3086f * (1 - sat), gC = 0.6094f * (1 - sat), bC = 0.0820f * (1 - sat);
                            ia.SetColorMatrix(new ColorMatrix(new[]
                            {
                                new[] { r + sat, r, r, 0f, 0f },
                                new[] { gC, gC + sat, gC, 0f, 0f },
                                new[] { bC, bC, bC + sat, 0f, 0f },
                                new[] { 0f, 0f, 0f, 1f, 0f },
                                new[] { lift, lift, lift, 0f, 1f }
                            }));
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
            building = new List<Hit>();
            fieldId = null;
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
            hits = building ?? new List<Hit>();
            building = null;
            Present();
            SnapLive();
            SyncInput();
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
                using (var b = new SolidBrush(Color.FromArgb(glassTint, 14, 18, 30))) g.FillPath(b, p);
                using (var veil = new SolidBrush(Color.FromArgb(26, 255, 255, 255))) g.FillPath(veil, p);
                // a light band along the top edge only: a sheen over the whole height washes the text areas out
                var band = new RectangleF(x0, -2, w, Math.Min(h + 2, 120f));
                var kept = g.Save();
                g.SetClip(p);
                using (var sheen = new LinearGradientBrush(band, Color.FromArgb(64, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                    g.FillRectangle(sheen, band);
                g.Restore(kept);
            }
            else
            {
                using (var b = new SolidBrush(Color.FromArgb(255, 6, 6, 8))) g.FillPath(b, p);
            }
            Color ca = AccA, cb = AccB;
            var saved = g.Save();
            g.SetClip(p);
            using (var wash = new LinearGradientBrush(box, Color.FromArgb(liquid ? 70 : 16, ca), Color.FromArgb(0, cb), 90f))
            using (var pen = new Pen(wash, 6f))
                g.DrawPath(pen, p);
            g.Restore(saved);
            using (var rim = new LinearGradientBrush(box, Color.FromArgb(liquid ? 235 : 70, ca), Color.FromArgb(liquid ? 70 : 18, cb), 90f))
            using (var pen = new Pen(rim, liquid ? 1.4f : 1.1f))
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
            float tExp = Clamp01((h - CompSize.Height) / (PageH - CompSize.Height));
            if (tExp < 0.95f || cur != Mode.Expanded) building = null;
            float tCmp = Clamp01((w - 120) / (CompSize.Width - 120)) * (1 - tExp);
            float tIdle = (1 - Clamp01((w - 100) / 50)) * (1 - tExp);
            float km = Clamp01((h - MiniSize.Height) / (IdleSize.Height - MiniSize.Height));
            eyes.Draw(g, MaxW / 2f, MiniSize.Height / 2f * (1 - km) + 12f * km, 0.5f * (1 - km) + 1.2f * km, tIdle);
            if (tCmp > 0.02f && (track != null || voice != null)) DrawCompact(g, x0, w, tCmp);
            if (tExp > 0.02f) DrawExpanded(g, x0, tExp * tExp, building != null);
        }

        void DrawCompact(Graphics g, float x0, float w, float a)
        {
            if (voice != null)
            {
                // a spoken conversation is going on: state colour around the microphone, and the latest words
                var v = voice;
                using (var b = new SolidBrush(StateColor(v.State, a * 0.9f))) g.FillEllipse(b, x0 + 9, 5, 22, 22);
                DrawMicGlyph(g, x0 + 20, 16, Color.FromArgb((int)(255 * Clamp01(a)), 14, 14, 18));
                string said = (v.Live ?? "").Trim();
                using (var b = new SolidBrush(Wh(a, 240)))
                    g.DrawString(said.Length > 0 ? said : StateText(v.State) + "...", fComp, b, new RectangleF(x0 + 40, 8, w - 40 - 44, 18), sfNear);
                eyes.Draw(g, x0 + w - 27, 16, 0.85f, a);
                return;
            }
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
            using (var b = new SolidBrush(Wh(a, 210)))
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

        static void DrawLyricLine(Graphics g, float x, float y, string text, Font f, Color c)
        {
            if (text == null) return;
            using (var b = new SolidBrush(c))
                g.DrawString(text.Length > 0 ? text : "\u266A", f, b, new RectangleF(x, y, TextW, 20), sfNear);
        }

        void DrawExpanded(Graphics g, float ex, float a, bool live)
        {
            // the ^ in the corner shrinks the island to Mini
            using (var pen = new Pen(Wh(a, hot == "mini" ? 245 : 150), 1.8f))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                g.DrawLines(pen, new[] { new PointF(ex + 12, 18), new PointF(ex + 17, 13), new PointF(ex + 22, 18) });
            }
            AddHit("mini", new RectangleF(ex + 4, 3, 26, 24), delegate { SetMiniSetting("mini"); });
            eyes.Draw(g, ex + ExpSize.Width - 34, 16, 0.85f, a);
            DrawTabs(g, ex, a, live);
            switch (page)
            {
                case Page.Library: DrawLibrary(g, ex, a); break;
                case Page.Ai: DrawAi(g, ex, a); break;
                case Page.Prompter: DrawPrompter(g, ex, a); break;
                case Page.Setup: DrawSetup(g, ex, a); break;
                default: DrawMusic(g, ex, a); break;
            }
        }

        void DrawMusic(Graphics g, float ex, float a)
        {
            const float T = TabH;
            if (track == null)
            {
                var now = DateTime.Now;
                CultureInfo ci;
                try { ci = new CultureInfo("id-ID"); } catch { ci = CultureInfo.InvariantCulture; }
                using (var b = new SolidBrush(Wh(a, 245)))
                    g.DrawString(now.ToString("HH:mm"), fClock, b, new RectangleF(ex, 34 + T, ExpSize.Width, 50), sfMid);
                using (var b = new SolidBrush(Wh(a, 210)))
                    g.DrawString(now.ToString("dddd, d MMMM", ci), fSub, b, new RectangleF(ex, 86 + T, ExpSize.Width, 20), sfMid);
                return;
            }

            DrawArt(g, new RectangleF(ex + 18, 22 + LyrOff / 2 + T, 96, 96), 14, a);
            float tx = ex + TextX;
            using (var b = new SolidBrush(Wh(a, 250)))
                g.DrawString(track.Title, fTitle, b, new RectangleF(tx, 20 + T, TextW, 22), sfNear);
            using (var b = new SolidBrush(Wh(a, 210)))
                g.DrawString(track.Artist, fSub, b, new RectangleF(tx, 43 + T, TextW, 20), sfNear);
            if (lyrics != null)
            {
                int idx = lyrics.IndexAt(track.Now());
                DrawLyricLine(g, tx, 62 + T, lyrics.Line(idx - 1), fSub, Wh(a, 210));
                string now = idx >= 0 ? lyrics.Line(idx) : "";
                DrawLyricLine(g, tx, 80 + T, now.Length > 0 ? now : "♪", fLyric, Wh(a, 255));
                DrawLyricLine(g, tx, 101 + T, lyrics.Line(idx + 1), fSub, Wh(a, 210));
            }

            TimeSpan pos = track.Now(), dur = track.Duration;
            float frac = dur > TimeSpan.Zero ? (float)Math.Min(1.0, pos.TotalSeconds / dur.TotalSeconds) : 0f;
            using (var b = new SolidBrush(Wh(a, hot == "seek" ? 80 : 55)))
                g.FillPath(b, RoundRect(new RectangleF(tx, BarY, TextW, 4), 2));
            if (frac > 0.002f)
                using (var b = new SolidBrush(Wh(a, 235)))
                    g.FillPath(b, RoundRect(new RectangleF(tx, BarY, Math.Max(4, TextW * frac), 4), 2));
            using (var b = new SolidBrush(Wh(a, 210)))
            {
                g.DrawString(Fmt(pos), fSmall, b, new RectangleF(tx, BarY + 8, 60, 14), sfNear);
                if (dur > TimeSpan.Zero)
                    g.DrawString(Fmt(dur), fSmall, b, new RectangleF(tx + TextW - 60, BarY + 8, 60, 14), sfFar);
            }
            if (dur > TimeSpan.Zero)
                AddHitAt("seek", new RectangleF(tx, BarY - 8, TextW, 18), p =>
                    SeekTo(TimeSpan.FromTicks((long)(track.Duration.Ticks * Clamp01((p.X - tx) / TextW)))));

            float cx = tx + TextW / 2;
            using (var b = new SolidBrush(Wh(a, 235)))
            {
                DrawSkip(g, b, cx - 46, BtnY, -1);
                DrawSkip(g, b, cx + 46, BtnY, 1);
            }
            if (LocalPlayer.I.Active) DrawVolume(g, tx + 226, BtnY, tx + TextW, a);
            AddHit("prev", new RectangleF(cx - 64, BtnY - 18, 36, 36), delegate { Ping("click"); SkipPrev(); });
            AddHit("next", new RectangleF(cx + 28, BtnY - 18, 36, 36), delegate { Ping("click"); SkipNext(); });
            AddHit("play", new RectangleF(cx - 20, BtnY - 20, 40, 40), delegate { Ping("click"); Toggle(); });
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

        // Volume of Notchira's own player only; the system volume and other apps (a Meet or Zoom call) stay as they are.
        void DrawVolume(Graphics g, float x, float y, float right, float a)
        {
            float vol = (float)LocalPlayer.I.Volume, bx = x + 18, bw = right - bx;
            bool hov = hot == "vol";
            using (var b = new SolidBrush(Wh(a, 235)))
            {
                g.FillPolygon(b, new[] { new PointF(x, y - 2), new PointF(x + 3, y - 2), new PointF(x + 8, y - 6), new PointF(x + 8, y + 6), new PointF(x + 3, y + 2), new PointF(x, y + 2) });
            }
            using (var pen = new Pen(Wh(a, vol <= 0 ? 90 : 235), 1.4f))
            {
                if (vol > 0) g.DrawArc(pen, x + 6, y - 4, 7, 8, -50, 100);
                if (vol > 0.5f) g.DrawArc(pen, x + 6, y - 7, 11, 14, -50, 100);
            }
            using (var b = new SolidBrush(Wh(a, hov ? 80 : 55)))
                g.FillPath(b, RoundRect(new RectangleF(bx, y - 2, bw, 4), 2));
            if (vol > 0.01f)
                using (var b = new SolidBrush(Wh(a, 235)))
                    g.FillPath(b, RoundRect(new RectangleF(bx, y - 2, Math.Max(4, bw * vol), 4), 2));
            using (var b = new SolidBrush(Wh(a, 250)))
                g.FillEllipse(b, bx + bw * vol - 4, y - 4, 8, 8);
            AddHitAt("vol", new RectangleF(x - 2, y - 14, right - x + 4, 28), p =>
            {
                int target = (int)Math.Round(Clamp01((p.X - bx) / bw) * 100);
                LocalPlayer.I.AdjustVolume(target - (int)Math.Round(LocalPlayer.I.Volume * 100));
            });
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

        // dev hook: NOTCHIRA_SNAP=<folder> makes the running app save its own frame (the island is excluded from
        // screenshots) once the panel is open, with a line saying whether the glass backdrop was captured
        static readonly string snapDir = Environment.GetEnvironmentVariable("NOTCHIRA_SNAP");
        int snapAt;

        void SnapLive()
        {
            if (string.IsNullOrEmpty(snapDir) || cur != Mode.Expanded || Math.Abs(sw.X - sw.T) > 1f || Math.Abs(sh.X - sh.T) > 1f) return;
            if (Environment.TickCount - snapAt < 1500) return;
            snapAt = Environment.TickCount;
            try
            {
                Directory.CreateDirectory(snapDir);
                frame.Save(Path.Combine(snapDir, "live-" + page.ToString().ToLowerInvariant() + ".png"), ImageFormat.Png);
                File.AppendAllText(Path.Combine(snapDir, "live.log"), DateTime.Now.ToString("HH:mm:ss") + " page=" + page + " material=" + material +
                    " glassOk=" + glassOk + " backdrop=" + (backdrop != null) + " tint=" + glassTint + Environment.NewLine);
            }
            catch { }
        }

        void Present() { Present(frame); }

        void Present(Bitmap bmp)
        {
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memDc = Native.CreateCompatibleDC(screenDc);
            IntPtr hBmp = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                hBmp = bmp.GetHbitmap(Color.FromArgb(0));
                old = Native.SelectObject(memDc, hBmp);
                var size = new Native.SIZE { cx = bmp.Width, cy = bmp.Height };
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

        // Only a fallback for when the island is hidden: every setting lives in the island's Atur tab.
        void BuildTray()
        {
            menu = new ContextMenuStrip();
            miHide = new ToolStripMenuItem();
            miHide.Click += delegate { ToggleHidden(); };
            var miQuit = new ToolStripMenuItem("Keluar");
            miQuit.Click += delegate { Application.Exit(); };
            menu.Items.AddRange(new ToolStripItem[] { miHide, new ToolStripSeparator(), miQuit });
            menu.Opening += delegate { miHide.Text = (userHidden ? "Tampilkan island" : "Sembunyikan island") + "\tCtrl+Alt+H"; };

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

        static string RunKey
        {
            get { return Environment.GetEnvironmentVariable("NOTCHIRA_RUNKEY") ?? @"Software\Microsoft\Windows\CurrentVersion\Run"; }
        }

        // Notchira starts with Windows by default, so it is already waiting after every boot. The first run turns this
        // on once; after that the tray menu decides, and "Keluar" only closes it until the next sign-in.
        // Test copies (NOTCHIRA_DIR) never touch the registry unless a test key is given.
        void InitAutostart()
        {
            if (Environment.GetEnvironmentVariable("NOTCHIRA_DIR") != null && Environment.GetEnvironmentVariable("NOTCHIRA_RUNKEY") == null) return;
            if (Settings.Get("autostart.init", "") != "1")
            {
                SetAutostart(true);
                Settings.Set("autostart.init", "1");
            }
            else if (IsAutostart()) SetAutostart(true); // keeps the stored path right if the exe was moved
        }

        // Dev hook: NOTCHIRA_DEMO=autostart checks the default, the opt-out, and the path refresh on a test key.
        void DemoAutostart()
        {
            string dir = Environment.GetEnvironmentVariable("NOTCHIRA_DUMP");
            Directory.CreateDirectory(dir);
            var sb = new System.Text.StringBuilder();
            string exe = Application.ExecutablePath;
            Func<string> stored = delegate
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) { object v = k == null ? null : k.GetValue("Notchira"); return v == null ? "(none)" : v.ToString(); }
            };
            InitAutostart();
            sb.AppendLine("1 first run enables it: " + IsAutostart() + " -> " + stored() + " | init flag=" + Settings.Get("autostart.init", ""));
            SetAutostart(false);
            InitAutostart();
            sb.AppendLine("2 after the user turns it off, a later start leaves it off: " + !IsAutostart() + " -> " + stored());
            SetAutostart(true);
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey)) k.SetValue("Notchira", "\"C:\\old\\place\\Notchira.exe\"");
            InitAutostart();
            sb.AppendLine("3 a moved exe path is refreshed: " + (stored() == "\"" + exe + "\"") + " -> " + stored());
            Registry.CurrentUser.DeleteSubKeyTree(RunKey, false);
            File.WriteAllText(Path.Combine(dir, "autostart.log"), sb.ToString());
            Application.Exit();
        }

        // A shortcut in the per-user Start Menu is what makes Notchira show up in Windows search.
        static string StartLink
        {
            get
            {
                string dir = Environment.GetEnvironmentVariable("NOTCHIRA_STARTDIR") ??
                             Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                return Path.Combine(dir, "Notchira.lnk");
            }
        }

        // First run adds the Start Menu entry so Windows search finds the app; after that the setting decides,
        // and an existing shortcut is rewritten so it follows the exe if it was moved.
        static void InitStartShortcut()
        {
            if (Environment.GetEnvironmentVariable("NOTCHIRA_DIR") != null && Environment.GetEnvironmentVariable("NOTCHIRA_STARTDIR") == null) return;
            if (Settings.Get("start.init", "") != "1")
            {
                SetStartShortcut(true);
                Settings.Set("start.init", "1");
            }
            else if (HasStartShortcut()) SetStartShortcut(true);
        }

        static bool HasStartShortcut() { return File.Exists(StartLink); }

        static void SetStartShortcut(bool on)
        {
            try
            {
                string link = StartLink;
                if (!on) { if (File.Exists(link)) File.Delete(link); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(link));
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(t);
                object lnk = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { link });
                Type lt = lnk.GetType();
                Action<string, string> set = (name, value) =>
                    lt.InvokeMember(name, System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { value });
                string exe = Application.ExecutablePath;
                set("TargetPath", exe);
                set("WorkingDirectory", Path.GetDirectoryName(exe));
                set("Description", "Notchira - dynamic island untuk Windows");
                set("IconLocation", exe + ",0");
                lt.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, lnk, null);
                System.Runtime.InteropServices.Marshal.ReleaseComObject(lnk);
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
            }
            catch { }
        }

        // Dev hook: NOTCHIRA_DEMO=startmenu creates and removes the shortcut in NOTCHIRA_STARTDIR and logs it.
        void DemoStartMenu()
        {
            string dir = Environment.GetEnvironmentVariable("NOTCHIRA_DUMP");
            Directory.CreateDirectory(dir);
            var sb = new System.Text.StringBuilder();
            SetStartShortcut(true);
            sb.AppendLine("created: " + HasStartShortcut() + " at " + StartLink);
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(t);
                object lnk = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { StartLink });
                foreach (string p in new[] { "TargetPath", "WorkingDirectory", "IconLocation", "Description" })
                    sb.AppendLine("  " + p + " = " + lnk.GetType().InvokeMember(p, System.Reflection.BindingFlags.GetProperty, null, lnk, null));
            }
            catch (Exception ex) { sb.AppendLine("read back failed: " + ex.Message); }
            SetStartShortcut(false);
            sb.AppendLine("removed: " + !HasStartShortcut());
            File.WriteAllText(Path.Combine(dir, "startmenu.log"), sb.ToString());
            Application.Exit();
        }

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
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
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

        string ForegroundInfo()
        {
            IntPtr h = Native.GetForegroundWindow();
            var cls = new System.Text.StringBuilder(64);
            Native.GetClassName(h, cls, 64);
            Native.RECT r;
            Native.GetWindowRect(h, out r);
            return "foreground=" + cls + " rect=" + r.Left + "," + r.Top + "," + r.Right + "," + r.Bottom + " TopCovered=" + TopCovered() + " covered=" + covered;
        }

        // Dev hook: NOTCHIRA_DEMO=mini drives the mini mode with the real cursor and logs what happened.
        void DemoMini()
        {
            string dir = Environment.GetEnvironmentVariable("NOTCHIRA_DUMP");
            Directory.CreateDirectory(dir);
            Action<string> log = s => File.AppendAllText(Path.Combine(dir, "mini.log"), s + Environment.NewLine);
            Action<string> shot = name =>
            {
                Render();
                frame.Save(Path.Combine(dir, name + ".png"), ImageFormat.Png);
                log(name + ": mode=" + cur + " size=" + sw.X.ToString("0") + "x" + sh.X.ToString("0") + " clickThrough=" + Native.IsClickThrough(Handle) + " hover=" + hover + " | " + ForegroundInfo());
            };
            var scr = Screen.PrimaryScreen.Bounds;
            int cx = scr.Left + scr.Width / 2;
            Settings.Set("mini", "mini");
            int step = 0;
            var t = new Timer { Interval = 1000 };
            Form cover = null;
            t.Tick += delegate
            {
                step++;
                switch (step)
                {
                    case 1: Cursor.Position = new Point(cx + 400, 500); break;
                    case 2: shot("mini-1-sliver"); Cursor.Position = new Point(cx, 3); break;
                    case 3: shot("mini-2-woken"); Cursor.Position = new Point(cx + 400, 500); break;
                    case 4: shot("mini-3-holding"); break;
                    case 6: shot("mini-4-returned"); Settings.Set("mini", "normal"); break;
                    case 7: shot("mini-5-normal"); Settings.Set("mini", "auto"); break;
                    case 8:
                        shot("mini-6-auto-idle");
                        cover = new Form { WindowState = FormWindowState.Maximized, Text = "cover", ShowInTaskbar = false };
                        cover.Show();
                        Native.keybd_event(0x12, 0, 0, UIntPtr.Zero);
                        Native.keybd_event(0x12, 0, 2, UIntPtr.Zero);
                        cover.Activate();
                        break;
                    case 11:
                        log("foreground is the cover window: " + (Native.GetForegroundWindow() == cover.Handle) + ", covered=" + covered);
                        shot("mini-7-auto-covered");
                        cover.Close();
                        break;
                    case 14:
                        log("after the cover closed: covered=" + covered);
                        shot("mini-8-uncovered");
                        t.Stop();
                        Application.Exit();
                        break;
                }
            };
            t.Start();
        }
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

        // Dev hook: NOTCHIRA_DEMO=pages opens every tab with sample data and dumps a frame of each.
        void DemoPages()
        {
            string dir = Environment.GetEnvironmentVariable("NOTCHIRA_DUMP");
            Directory.CreateDirectory(dir);
            Action<string> log = s => File.AppendAllText(Path.Combine(dir, "pages.log"), s + Environment.NewLine);
            track = new TrackInfo
            {
                Title = "Aurora Borealis", Artist = "Notchira Sound Lab", Album = "", Playing = true,
                Position = TimeSpan.FromSeconds(45), Duration = TimeSpan.FromSeconds(192), Stamp = DateTime.UtcNow
            };
            lyricKey = "demo";
            SetLyrics(Lyrics.Parse("[00:40.00]Pagi datang membawa cahaya\n[00:44.00]Langkahku ringan menyusuri kota\n[00:50.00]Di tepi layar aku menunggu"));
            string[] titles = { "Lagu Pagi", "Senja di Kota", "Hujan Bulan Juni", "Jalan Pulang", "Langit Biru", "Kopi Hitam", "Bintang Kecil", "Malam Minggu" };
            for (int i = 0; i < titles.Length; i++)
                Library.Add(new Song { Id = "demo" + i, Title = titles[i], Artist = "Artis " + (i + 1), Seconds = 180 + i * 17, Added = i });
            Library.CreatePlaylist("Santai");
            Library.AddToPlaylist("Santai", "demo1");
            history.Clear();
            history.Add(new AiMessage("user", "Apa ibu kota Australia?"));
            history.Add(new AiMessage("assistant", "Ibu kota Australia adalah Canberra, bukan Sydney. Canberra dipilih sebagai jalan tengah antara Sydney dan Melbourne, dan resmi menjadi ibu kota pada 1913."));
            drafts["url"] = "";
            // dev hook: NOTCHIRA_NEWLIST=1 dumps the Pustaka tab while a new list is being named
            if (Environment.GetEnvironmentVariable("NOTCHIRA_NEWLIST") == "1") { newList = true; drafts["url"] = "https://youtube.com/playlist?list=PL123"; }
            var order = new[] { Page.Music, Page.Library, Page.Ai, Page.Prompter, Page.Setup };
            int step = 0;
            var t = new Timer { Interval = 1100 };
            t.Tick += delegate
            {
                if (step > 0)
                {
                    Page shown = order[step - 1];
                    CaptureBackdrop();
                    Render();
                    frame.Save(Path.Combine(dir, "page-" + shown.ToString().ToLowerInvariant() + ".png"), ImageFormat.Png);
                    log(shown + ": size=" + sw.X.ToString("0") + "x" + sh.X.ToString("0") + " hits=" + hits.Count +
                        " field=" + (fieldId ?? "-") + " input=" + (input != null && input.Visible ? input.Bounds.ToString() : "hidden"));
                }
                if (step == order.Length)
                {
                    // hotkey path: the assistant opens with the cursor far away and the text box takes the keyboard
                    t.Stop();
                    Cursor.Position = new Point(Left + (int)(MaxW * S) + 300, Top + 500);
                    OpenPage(Page.Ai, true);
                    var k = new Timer { Interval = 1200 };
                    int ks = 0;
                    k.Tick += delegate
                    {
                        ks++;
                        if (ks == 1) { log("hotkey open: mode=" + cur + " focus=" + Typing); return; }
                        log("still open after the grace period while focused: " + (cur == Mode.Expanded));
                        k.Stop();
                        Application.Exit();
                    };
                    k.Start();
                    return;
                }
                Cursor.Position = new Point(Left + (int)(MaxW / 2 * S), Top + (int)(150 * S));
                OpenPage(order[step], false);
                step++;
            };
            t.Start();
        }

        // Dev hook: NOTCHIRA_DEMO=models opens the model picker, tries a wrong and a right model name against the
        // provider (needs keys.dat in NOTCHIRA_DIR), asks one question with the added model, and dumps frames.
        void DemoModels()
        {
            string dir = Environment.GetEnvironmentVariable("NOTCHIRA_DUMP");
            Directory.CreateDirectory(dir);
            Action<string> log = s => File.AppendAllText(Path.Combine(dir, "models.log"), s + Environment.NewLine);
            Action<string> shot = name => { CaptureBackdrop(); Render(); frame.Save(Path.Combine(dir, name + ".png"), ImageFormat.Png); };
            Settings.Set("ai.provider", "groq");
            int step = 0;
            var t = new Timer { Interval = 2500 };
            t.Tick += delegate
            {
                Cursor.Position = new Point(Left + (int)(MaxW / 2 * S), Top + (int)(150 * S));
                step++;
                switch (step)
                {
                    case 1: OpenPage(Page.Ai, false); modelPick = true; voiceTab = Environment.GetEnvironmentVariable("NOTCHIRA_VOICE_TAB") != null; break;
                    case 2: shot("models-1-list"); addModel = true; AddModel("openai/gpt-oss-20bx"); break;
                    case 4: log("wrong name: " + aiStatus); shot("models-2-wrong"); AddModel("openai/gpt-oss-safeguard-20b"); break;
                    case 6:
                        log("right name: " + aiStatus + " | selected=" + Ai.Model(Ai.Find("groq")) + " | list=" + string.Join(", ", Ai.Models(Ai.Find("groq")).ToArray()));
                        modelPick = true;
                        shot("models-3-added");
                        modelPick = false;
                        Send("Jawab satu kata saja: ibu kota Jepang?");
                        break;
                    case 9:
                        log("answer with the added model: " + (history.Count > 1 ? history[history.Count - 1].Text : "(none)") + " | " + aiStatus);
                        shot("models-4-answer");
                        t.Stop();
                        Application.Exit();
                        break;
                }
            };
            t.Start();
        }

        // Dev hook: NOTCHIRA_DEMO=voice with NOTCHIRA_VOICE_WAV (the question) and NOTCHIRA_VOICE_MUTE=1 runs one spoken
        // conversation with the provider in voice.provider, logs every state and turn, and dumps frames along the way.
        void DemoVoice()
        {
            string dir = Environment.GetEnvironmentVariable("NOTCHIRA_DUMP");
            Directory.CreateDirectory(dir);
            var clock = Stopwatch.StartNew();
            Action<string> log = s => File.AppendAllText(Path.Combine(dir, "voice.log"), clock.ElapsedMilliseconds.ToString().PadLeft(6) + " ms  " + s + Environment.NewLine);
            Action<string> shot = name => { CaptureBackdrop(); Render(); frame.Save(Path.Combine(dir, name + ".png"), ImageFormat.Png); };
            history.Clear();
            Cursor.Position = new Point(Left + (int)(MaxW / 2 * S), Top + (int)(150 * S));
            OpenPage(Page.Ai, false);
            StartVoice();
            var v = voice;
            if (v == null) { log("not started: " + aiStatus); Application.Exit(); return; }
            log("start " + v.Name + " " + v.Model);
            var seen = new HashSet<string>();
            VoiceSession.St last = VoiceSession.St.Connecting;
            int turns = 0, shots = 0;
            v.Turn += (role, text) => { turns++; log("turn " + role + ": " + text); };
            var t = new Timer { Interval = 100 };
            t.Tick += delegate
            {
                var st = v.State;
                if (st != last)
                {
                    log("state " + st + (st == VoiceSession.St.Ended ? " error=" + (v.Error ?? "-") + " quiet=" + v.Quiet : "") + " key=" + v.KeyLabel);
                    last = st;
                    if (!seen.Contains(st.ToString()) && st != VoiceSession.St.Ended) { seen.Add(st.ToString()); shot("voice-" + (++shots) + "-" + st.ToString().ToLowerInvariant()); }
                }
                if (st == VoiceSession.St.Ended || clock.ElapsedMilliseconds > 60000)
                {
                    log("audio received: " + (v.AudioBytes / 48000.0).ToString("0.0") + " s, status: " + aiStatus);
                    if (st != VoiceSession.St.Ended) { log("timeout, stopping"); v.Stop(); }
                    hover = false;
                    Retarget();
                    var end = new Timer { Interval = 800 };
                    end.Tick += delegate { end.Stop(); hover = true; Retarget(); var e2 = new Timer { Interval = 900 }; e2.Tick += delegate { e2.Stop(); shot("voice-after"); Application.Exit(); }; e2.Start(); };
                    end.Start();
                    t.Stop();
                }
                else if (turns >= 2 && st == VoiceSession.St.Listening && clock.ElapsedMilliseconds > 3000 && !seen.Contains("compact") &&
                         Environment.GetEnvironmentVariable("NOTCHIRA_VOICE_WAIT") == null)
                {
                    // after the first answer: show the compact island too, then end the session by hand
                    seen.Add("compact");
                    hover = false;
                    Retarget();
                    var c = new Timer { Interval = 900 };
                    c.Tick += delegate { c.Stop(); shot("voice-compact"); log("stop by hand"); StopVoice(); };
                    c.Start();
                }
            };
            t.Start();
        }

        // Dev hook: NOTCHIRA_DEMO=mictest opens the real microphone for three seconds, entirely offline, and logs what came in.
        void DemoMic()
        {
            string dir = Environment.GetEnvironmentVariable("NOTCHIRA_DUMP");
            Directory.CreateDirectory(dir);
            int pieces = 0, bytes = 0;
            float peak = 0;
            var mic = new Mic(pcm => { pieces++; bytes += pcm.Length; peak = Math.Max(peak, Mic.Level(pcm)); });
            string result;
            try
            {
                mic.Start();
                System.Threading.Thread.Sleep(3000);
                mic.Dispose();
                result = "opened: " + pieces + " pieces, " + bytes + " bytes (" + (bytes / 32000.0).ToString("0.00") + " s of audio), peak level " + peak.ToString("0.000");
            }
            catch (Exception ex) { result = "failed: " + ex.Message; }
            var spk = new Speaker();
            try
            {
                spk.Start();
                spk.Write(new byte[Speaker.Rate * 2 / 10]);
                bool busy = spk.Busy;
                System.Threading.Thread.Sleep(400);
                result += Environment.NewLine + "speaker: opened, busy while playing 100 ms of silence=" + busy + ", busy afterwards=" + spk.Busy;
            }
            catch (Exception ex) { result += Environment.NewLine + "speaker failed: " + ex.Message; }
            spk.Dispose();
            File.WriteAllText(Path.Combine(dir, "mictest.log"), result);
            Application.Exit();
        }

        void StartDemo(string mode)
        {
            if (mode == "keyswin")
            {
                Secrets.Add("groq", "Key utama", "gsk_demo_1234");
                Secrets.Add("groq", "Key cadangan", "gsk_demo_5678");
                KeysWindow.ShowWindow("groq");
                var wt = new Timer { Interval = 1200 };
                wt.Tick += delegate
                {
                    wt.Stop();
                    string wd = Environment.GetEnvironmentVariable("NOTCHIRA_DUMP");
                    foreach (Form f in System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Cast<Form>(Application.OpenForms)))
                    {
                        if (f == this || f is IslandInput || f.Width < 300) continue;
                        using (var bmp = new Bitmap(f.Width, f.Height))
                        {
                            f.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                            bmp.Save(Path.Combine(wd, mode + ".png"), ImageFormat.Png);
                        }
                    }
                    Application.Exit();
                };
                wt.Start();
                return;
            }
            if (mode == "lyricstest")
            {
                SelfTest.Run(Environment.GetEnvironmentVariable("NOTCHIRA_DUMP"), delegate { BeginInvoke((Action)Application.Exit); });
                return;
            }
            if (mode == "pages") { DemoPages(); return; }
            if (mode == "models") { DemoModels(); return; }
            if (mode == "voice") { DemoVoice(); return; }
            if (mode == "mictest") { DemoMic(); return; }
            if (mode == "startmenu") { DemoStartMenu(); return; }
            if (mode == "autostart") { DemoAutostart(); return; }
            if (mode == "mini") { DemoMini(); return; }
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
                if (real)
                {
                    lyricKey = "demo";
                    SetLyrics(Lyrics.Parse("[00:50.00]Pagi datang membawa cahaya\n[00:56.00]Langkahku ringan menyusuri kota\n[01:02.00]Di tepi layar aku menunggu\n[01:08.00]Sebuah lagu yang kau putar untukku\n[01:14.00]Dan malam pun terasa hangat"));
                }
                else LoadLyrics(track);
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
