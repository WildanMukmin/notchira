using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Notchira
{
    // Owns the prompter window and its editor so the rest of the app only asks "is it open?".
    static class Teleprompter
    {
        static PrompterWindow win;
        static PrompterEditor editor;

        public static bool Open { get { return win != null && !win.IsDisposed; } }

        public static string ScriptDir { get { return Path.Combine(Settings.Dir, "naskah"); } }
        public static string LastFile { get { return Path.Combine(Settings.Dir, "prompter.txt"); } }

        public static void ShowEditor()
        {
            if (editor == null || editor.IsDisposed) editor = new PrompterEditor();
            Drop.Show(editor);
        }

        public static void Start(string script)
        {
            if (!Open)
            {
                win = new PrompterWindow();
                win.SetScript(script);
                win.Show();
            }
            else win.SetScript(script);
            win.Begin();
            if (editor != null && !editor.IsDisposed) editor.Hide();
            if (Island.Current != null) Island.Current.RefreshVisible();
        }

        public static void Stop()
        {
            if (Open) win.Close();
            win = null;
            if (Island.Current != null) Island.Current.RefreshVisible();
            ShowEditor();
        }

        public static void Reconfigure() { if (Open) win.LoadSettings(); }

        public static int CountWords(string script)
        {
            int n = 0;
            foreach (string t in script.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                if (!t.Equals("[jeda]", StringComparison.OrdinalIgnoreCase)) n++;
            return n;
        }

        // Markdown formatting is dropped; only the text to be read stays.
        public static string StripMarkdown(string s)
        {
            s = Regex.Replace(s, @"^\s*```.*$", "", RegexOptions.Multiline);
            s = Regex.Replace(s, @"^\s{0,3}#{1,6}\s*", "", RegexOptions.Multiline);
            s = Regex.Replace(s, @"!?\[([^\]]*)\]\([^)]*\)", "$1");
            s = Regex.Replace(s, @"\*\*|__|\*|`", "");
            s = Regex.Replace(s, @"^\s*[-+]\s+", "", RegexOptions.Multiline);
            s = Regex.Replace(s, @"^\s*>\s?", "", RegexOptions.Multiline);
            return s;
        }

        public static void DemoEditor(string dumpDir)
        {
            ShowEditor();
            var t = new Timer { Interval = 1200 };
            t.Tick += delegate
            {
                t.Stop();
                if (!string.IsNullOrEmpty(dumpDir))
                {
                    Directory.CreateDirectory(dumpDir);
                    using (var bmp = new Bitmap(editor.Width, editor.Height))
                    {
                        editor.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                        bmp.Save(Path.Combine(dumpDir, "editor.png"), ImageFormat.Png);
                    }
                }
                Application.Exit();
            };
            t.Start();
        }

        // Dev hook: NOTCHIRA_DEMO=prompter runs a short scripted session and dumps frames.
        public static void Demo(string dumpDir)
        {
            Settings.Set("prompter.wpm", "400");
            const string text = "Halo semua, selamat datang di video kali ini. Hari ini kita bahas cara membuat aplikasi yang ringan " +
                "dan enak dipakai. [jeda]\n\nPertama, kita mulai dari ide dasarnya. Kedua, kita tentukan fitur yang benar-benar perlu. " +
                "Ketiga, kita bangun pelan-pelan sambil menguji setiap langkahnya sampai selesai.";
            win = new PrompterWindow();
            win.SetScript(text);
            win.Show();
            win.DemoRun();
            var t = new Timer { Interval = 1000 };
            int step = 0;
            t.Tick += delegate
            {
                step++;
                if (!string.IsNullOrEmpty(dumpDir))
                {
                    Directory.CreateDirectory(dumpDir);
                    win.Dump(Path.Combine(dumpDir, "prompter-" + step + ".png"));
                }
                if (step >= 4) { t.Stop(); Application.Exit(); }
            };
            t.Start();
        }
    }

    // The scrolling script. A layered window like the island: allocated once at the largest size,
    // and the visible shape inside it is resized by dragging the edges while paused.
    sealed class PrompterWindow : Form
    {
        const float MaxW = 1000, MaxH = 600, Pad = 28, Edge = 8;

        sealed class Line { public string Text = ""; public float Units; public bool Marker; }

        float S = 1f;
        Bitmap frame;
        float W = 560, H = 150;
        int wpm = 140, px = 19, bgPct = 55;
        bool mirror;

        string script = "";
        readonly List<Line> lines = new List<Line>();
        float[] cum = new float[0];
        float total;
        readonly List<float> pauses = new List<float>();
        int nextPause;
        bool laidOut;

        float units;
        bool running, counting, finished;
        int count;
        readonly Timer tick = new Timer { Interval = 16 };
        readonly Timer cd = new Timer { Interval = 1000 };
        readonly Stopwatch sw = new Stopwatch();
        double last;

        Font font;
        float lineH;
        int drag;

        static readonly StringFormat sfNear = Fmt(StringAlignment.Near);
        static readonly StringFormat sfFar = Fmt(StringAlignment.Far);
        static readonly StringFormat sfMid = Fmt(StringAlignment.Center);
        static readonly Font fStatus = new Font("Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Pixel);

        static StringFormat Fmt(StringAlignment a)
        {
            var f = new StringFormat(StringFormat.GenericTypographic);
            f.FormatFlags |= StringFormatFlags.NoWrap;
            f.Alignment = a;
            return f;
        }

        const int IdSpace = 21, IdRestart = 22, IdBack = 23, IdFwd = 24, IdFaster = 25, IdSlower = 26, IdStop = 27, IdEditor = 28;

        public PrompterWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Text = "Notchira Teleprompter";
            LoadSettings();
            tick.Tick += delegate { OnTick(); };
            cd.Tick += delegate
            {
                count--;
                if (count <= 0) { cd.Stop(); counting = false; SetRunning(true); }
                else Render();
            };
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

        public void LoadSettings()
        {
            wpm = Clamp(Settings.Get("prompter.wpm", "140"), 60, 400, 140);
            px = Clamp(Settings.Get("prompter.size", "19"), 14, 48, 19);
            bgPct = Clamp(Settings.Get("prompter.bg", "55"), 15, 100, 55);
            mirror = Settings.Get("prompter.mirror", "0") == "1";
            W = Clamp(Settings.Get("prompter.width", "560"), 360, (int)MaxW, 560);
            H = Clamp(Settings.Get("prompter.height", "150"), 90, (int)MaxH, 150);
            if (font != null) font.Dispose();
            font = new Font("Segoe UI Semibold", px, FontStyle.Regular, GraphicsUnit.Pixel);
            lineH = px * 1.4f;
            laidOut = false;
            if (IsHandleCreated) Render();
        }

        static int Clamp(string s, int lo, int hi, int def)
        {
            int v;
            if (!int.TryParse(s, out v)) v = def;
            return v < lo ? lo : v > hi ? hi : v;
        }

        static float Clamp01(float v) { return v < 0 ? 0 : v > 1 ? 1 : v; }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
            uint dpi = Native.GetDpiForWindow(Handle);
            S = dpi > 0 ? dpi / 96f : 1f;
            int fw = (int)Math.Ceiling(MaxW * S), fh = (int)Math.Ceiling(MaxH * S);
            frame = new Bitmap(fw, fh, PixelFormat.Format32bppPArgb);
            var scr = Screen.PrimaryScreen;
            SetBounds(scr.Bounds.Left + (scr.Bounds.Width - fw) / 2, scr.Bounds.Top, fw, fh);
            uint mods = Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT;
            Native.RegisterHotKey(Handle, IdSpace, mods, 0x20);
            Native.RegisterHotKey(Handle, IdRestart, mods, 0x52);
            Native.RegisterHotKey(Handle, IdBack, mods, 0x25);
            Native.RegisterHotKey(Handle, IdFwd, mods, 0x27);
            Native.RegisterHotKey(Handle, IdFaster, mods, 0xBB);
            Native.RegisterHotKey(Handle, IdSlower, mods, 0xBD);
            Native.RegisterHotKey(Handle, IdStop, mods, 0x58);
            Native.RegisterHotKey(Handle, IdEditor, mods, 0x50);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Render();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            tick.Stop();
            cd.Stop();
            for (int id = IdSpace; id <= IdEditor; id++) Native.UnregisterHotKey(Handle, id);
            if (frame != null) frame.Dispose();
            frame = null;
            base.OnFormClosed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY)
            {
                switch ((int)m.WParam)
                {
                    case IdSpace: TogglePause(); break;
                    case IdRestart: Begin(); break;
                    case IdBack: Skip(-5); break;
                    case IdFwd: Skip(5); break;
                    case IdFaster: ChangeSpeed(10); break;
                    case IdSlower: ChangeSpeed(-10); break;
                    case IdStop: Teleprompter.Stop(); break;
                    case IdEditor: Teleprompter.ShowEditor(); break;
                }
                return;
            }
            base.WndProc(ref m);
        }

        // ---- control ----------------------------------------------------------------------------------------

        public void SetScript(string s)
        {
            script = s ?? "";
            laidOut = false;
        }

        public void Begin()
        {
            running = false;
            tick.Stop();
            units = 0;
            nextPause = 0;
            finished = false;
            counting = true;
            count = 3;
            cd.Stop();
            cd.Start();
            UpdateClickThrough();
            Render();
        }

        // Dev hook: skip the countdown and run immediately.
        public void DemoRun()
        {
            Settings.Set("prompter.width", "560");
            LoadSettings();
            units = 0;
            nextPause = 0;
            counting = false;
            SetRunning(true);
        }

        void SetRunning(bool r)
        {
            running = r;
            UpdateClickThrough();
            if (r) { sw.Restart(); last = 0; tick.Start(); }
            else tick.Stop();
            Render();
        }

        void UpdateClickThrough()
        {
            if (IsHandleCreated) Native.SetClickThrough(Handle, running || counting);
        }

        void TogglePause()
        {
            if (counting) return;
            if (finished) { Begin(); return; }
            SetRunning(!running);
        }

        void Skip(float seconds)
        {
            DoLayout();
            units = Math.Max(0, Math.Min(total, units + seconds * wpm / 60f));
            finished = false;
            nextPause = 0;
            while (nextPause < pauses.Count && pauses[nextPause] <= units) nextPause++;
            Render();
        }

        void ChangeSpeed(int delta)
        {
            wpm = Math.Max(60, Math.Min(400, wpm + delta));
            Settings.Set("prompter.wpm", wpm.ToString());
            Render();
        }

        void OnTick()
        {
            double t = sw.Elapsed.TotalSeconds;
            float dt = (float)Math.Min(0.1, t - last);
            last = t;
            if (running)
            {
                units += dt * wpm / 60f;
                if (nextPause < pauses.Count && units >= pauses[nextPause])
                {
                    units = pauses[nextPause];
                    nextPause++;
                    SetRunning(false);
                    return;
                }
                if (units >= total) { units = total; finished = true; SetRunning(false); return; }
            }
            Render();
        }

        // ---- layout -----------------------------------------------------------------------------------------

        void DoLayout()
        {
            if (laidOut) return;
            laidOut = true;
            lines.Clear();
            pauses.Clear();
            using (var bmp = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(bmp))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                float maxW = W - 2 * Pad;
                float spaceW = Measure(g, "a a") - Measure(g, "aa");
                var cur = new System.Text.StringBuilder();
                float curW = 0, seen = 0;
                int curWords = 0;
                bool marker = false;

                Action flush = delegate
                {
                    if (cur.Length > 0)
                        lines.Add(new Line { Text = cur.ToString(), Units = curWords, Marker = marker });
                    cur.Length = 0; curW = 0; curWords = 0; marker = false;
                };

                foreach (string para in script.Replace("\r", "").Split('\n'))
                {
                    if (para.Trim().Length == 0)
                    {
                        flush();
                        lines.Add(new Line { Text = "", Units = 1.2f });
                        seen += 1.2f;
                        continue;
                    }
                    foreach (string word in para.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (word.Equals("[jeda]", StringComparison.OrdinalIgnoreCase))
                        {
                            pauses.Add(seen);
                            marker = true;
                            continue;
                        }
                        float ww = Measure(g, word);
                        if (curW > 0 && curW + spaceW + ww > maxW) flush();
                        if (curW > 0) { cur.Append(' '); curW += spaceW; }
                        cur.Append(word);
                        curW += ww;
                        curWords++;
                        seen += 1;
                    }
                    flush();
                }
            }
            cum = new float[lines.Count];
            float acc = 0;
            for (int i = 0; i < lines.Count; i++) { cum[i] = acc; acc += lines[i].Units; }
            total = acc;
            nextPause = 0;
            while (nextPause < pauses.Count && pauses[nextPause] <= units) nextPause++;
        }

        float Measure(Graphics g, string s)
        {
            return g.MeasureString(s, font, 100000, StringFormat.GenericTypographic).Width;
        }

        float YAt(float u)
        {
            if (lines.Count == 0) return 0;
            int lo = 0, hi = lines.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (cum[mid] <= u) lo = mid; else hi = mid - 1;
            }
            float frac = lines[lo].Units > 0 ? Clamp01((u - cum[lo]) / lines[lo].Units) : 0;
            return (lo + frac) * lineH;
        }

        // ---- drawing ----------------------------------------------------------------------------------------

        void Render()
        {
            if (!IsHandleCreated || frame == null) return;
            DoLayout();
            float x0 = (MaxW - W) / 2f;
            using (var g = Graphics.FromImage(frame))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                g.ScaleTransform(S, S);
                if (mirror)
                {
                    g.TranslateTransform(MaxW, 0);
                    g.ScaleTransform(-1, 1);
                }
                using (var path = Island.NotchPath(x0, W, H, 22))
                {
                    using (var b = new SolidBrush(Color.FromArgb(bgPct * 255 / 100, 8, 8, 10))) g.FillPath(b, path);
                    using (var pen = new Pen(Color.FromArgb(46, 255, 255, 255), 1f)) g.DrawPath(pen, path);
                    g.SetClip(path);
                    DrawText(g, x0);
                    DrawStatus(g, x0);
                    if (counting) DrawCount(g, x0);
                    g.ResetClip();
                }
            }
            Native.Present(Handle, frame, Left, Top);
        }

        void DrawText(Graphics g, float x0)
        {
            if (lines.Count == 0)
            {
                using (var b = new SolidBrush(Color.FromArgb(150, 255, 255, 255)))
                    g.DrawString("Naskah kosong", font, b, new RectangleF(x0, H * 0.3f, W, lineH), sfMid);
                return;
            }
            float anchor = H * 0.30f, y0 = YAt(units);
            int first = Math.Max(0, (int)Math.Floor((y0 - anchor) / lineH) - 1);
            int lastI = Math.Min(lines.Count - 1, (int)Math.Ceiling((y0 + H - anchor) / lineH) + 1);
            for (int i = first; i <= lastI; i++)
            {
                float y = anchor + i * lineH - y0;
                float d = (y - anchor) / lineH;
                float a = d <= -1 ? 0.28f : d < 0 ? 0.28f + 0.72f * (d + 1) : d < 1 ? 1 - 0.2f * d : 0.8f;
                var line = lines[i];
                if (line.Text.Length > 0)
                    using (var b = new SolidBrush(Color.FromArgb((int)(255 * a), 255, 255, 255)))
                        g.DrawString(line.Text, font, b, new RectangleF(x0 + Pad, y, W - 2 * Pad + 4, lineH), sfNear);
                if (line.Marker)
                    using (var b = new SolidBrush(Color.FromArgb((int)(220 * a), 255, 190, 60)))
                        g.FillEllipse(b, x0 + 11, y + lineH / 2 - 3, 6, 6);
            }
        }

        void DrawStatus(Graphics g, float x0)
        {
            float remain = Math.Max(0, total - units) / Math.Max(1, wpm) * 60f;
            string state = finished ? "Selesai" : (!running && !counting) ? "Jeda" : "";
            string txt = (state.Length > 0 ? state + "  ·  " : "") + FormatTime(remain) + "  ·  " + wpm + " wpm";
            float tw = Math.Min(260, g.MeasureString(txt, fStatus, 1000, StringFormat.GenericTypographic).Width + 18);
            var pill = new RectangleF(x0 + W - tw - 12, 3, tw, 17);
            using (var path = RoundPath(pill, 8))
            using (var b = new SolidBrush(Color.FromArgb(200, 8, 8, 10))) g.FillPath(b, path);
            using (var b = new SolidBrush(Color.FromArgb(185, 255, 255, 255)))
                g.DrawString(txt, fStatus, b, new RectangleF(pill.X, 5, pill.Width - 9, 14), sfFar);
        }

        static GraphicsPath RoundPath(RectangleF r, float rad)
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

        void DrawCount(Graphics g, float x0)
        {
            using (var b = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
                g.FillRectangle(b, x0, 0, W, H);
            using (var f = new Font("Segoe UI", 64f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var b = new SolidBrush(Color.White))
                g.DrawString(count.ToString(), f, b, new RectangleF(x0, H / 2f - 40, W, 80), sfMid);
        }

        static string FormatTime(float seconds)
        {
            int s = (int)Math.Round(seconds);
            return string.Format("{0}:{1:00}", s / 60, s % 60);
        }

        public void Dump(string path)
        {
            if (frame != null) frame.Save(path, ImageFormat.Png);
        }

        // ---- resizing while paused --------------------------------------------------------------------------

        int HitEdge(float mx, float my)
        {
            float x0 = (MaxW - W) / 2f;
            bool side = Math.Abs(mx - x0) <= Edge || Math.Abs(mx - (x0 + W)) <= Edge;
            bool bottom = Math.Abs(my - H) <= Edge && mx >= x0 - Edge && mx <= x0 + W + Edge;
            return (side ? 1 : 0) | (bottom ? 2 : 0);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && !running && !counting) drag = HitEdge(e.X / S, e.Y / S);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            float mx = e.X / S, my = e.Y / S;
            if (drag == 0)
            {
                int h = HitEdge(mx, my);
                Cursor = h == 3 ? Cursors.SizeNWSE : h == 1 ? Cursors.SizeWE : h == 2 ? Cursors.SizeNS : Cursors.Default;
                return;
            }
            if ((drag & 1) != 0) W = Math.Max(360, Math.Min(MaxW, 2 * Math.Abs(mx - MaxW / 2f)));
            if ((drag & 2) != 0) H = Math.Max(90, Math.Min(MaxH, my));
            laidOut = false;
            Render();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (drag == 0) return;
            drag = 0;
            Settings.Set("prompter.width", ((int)W).ToString());
            Settings.Set("prompter.height", ((int)H).ToString());
        }
    }
}
