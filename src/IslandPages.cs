using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace Notchira
{
    // Everything that used to live in the tray menu and separate windows, drawn inside the open island:
    // a row of tabs at the top (Musik, Pustaka, Asisten, Prompter, Atur) and one page under it.
    sealed partial class Island
    {
        enum Page { Music, Library, Ai, Prompter, Setup }

        static readonly string[] TabNames = { "Musik", "Pustaka", "Asisten", "Prompter", "Atur" };
        const float TabH = 26;

        Page page = Page.Music;
        long closedAt, openGrace;

        static readonly Font fTab = new Font("Segoe UI Semibold", 11.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        static readonly Font fRow = new Font("Segoe UI", 12.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        static readonly Font fTiny = new Font("Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Pixel);
        static readonly StringFormat sfWrap = MakeWrap();

        static StringFormat MakeWrap()
        {
            var f = new StringFormat(StringFormat.GenericTypographic);
            f.Trimming = StringTrimming.Word;
            return f;
        }

        float PageH
        {
            get
            {
                switch (page)
                {
                    case Page.Library: return 324;
                    case Page.Ai: return 332;
                    case Page.Prompter: return 214;
                    case Page.Setup: return 374;
                    default: return ExpH;
                }
            }
        }

        // ---- hit targets: rebuilt on every frame while the panel is fully open ------------------------------------

        sealed class Hit { public string Id; public RectangleF R; public Action<PointF> Do; }
        List<Hit> hits = new List<Hit>(), building;
        string hot;
        string confirmId;
        long confirmUntil;

        void AddHitAt(string id, RectangleF r, Action<PointF> act)
        {
            if (building != null) building.Add(new Hit { Id = id, R = r, Do = act });
        }

        void AddHit(string id, RectangleF r, Action act) { AddHitAt(id, r, p => act()); }

        Hit HitAt(PointF p)
        {
            for (int i = hits.Count - 1; i >= 0; i--) if (hits[i].R.Contains(p)) return hits[i];
            return null;
        }

        // A destructive button asks once: the first click turns it into "Yakin?" for three seconds.
        bool Confirmed(string id)
        {
            long now = Environment.TickCount;
            if (confirmId == id && now < confirmUntil) { confirmId = null; return true; }
            confirmId = id;
            confirmUntil = now + 3000;
            var t = new System.Windows.Forms.Timer { Interval = 3100 };
            t.Tick += delegate { t.Stop(); t.Dispose(); Render(); };
            t.Start();
            return false;
        }

        bool Asking(string id) { return confirmId == id && Environment.TickCount < confirmUntil; }

        // Opened from a hotkey or a click, the cursor may be elsewhere: stay open a moment before the leave check.
        void OpenPage(Page p, bool focus)
        {
            if (userHidden) { userHidden = false; ApplyVisible(); }
            openGrace = Environment.TickCount + 1500;
            page = p;
            closedAt = Environment.TickCount;
            OnPageShown();
            hover = true;
            leave.Stop();
            holdUntil = 0;
            Wake();
            if (focus) pendingFocus = true;
            Retarget();
            Render();
        }

        void OnPageShown()
        {
            if (page == Page.Library) ReloadSongs();
            if (page == Page.Prompter) LoadScriptInfo();
        }

        // ---- shared widgets ---------------------------------------------------------------------------------

        void DrawTabs(Graphics g, float ex, float a, bool live)
        {
            float[] w = new float[TabNames.Length];
            float total = 0;
            for (int i = 0; i < TabNames.Length; i++)
            {
                // only the open tab carries its name next to the icon, the others are icons alone
                w[i] = (int)page == i ? g.MeasureString(TabNames[i], fTab, 200, sfNear).Width + 42 : 34;
                total += w[i] + (i > 0 ? 4 : 0);
            }
            float x = ex + 34 + (ExpSize.Width - 34 - 64 - total) / 2, y = 4;
            for (int i = 0; i < TabNames.Length; i++)
            {
                var r = new RectangleF(x, y, w[i], 23);
                bool on = (int)page == i;
                string id = "tab" + i;
                int bg = on ? 0 : hot == id ? 22 : 0;
                if (on) using (var b = new SolidBrush(Color.FromArgb((int)(60 * Clamp01(a)), AccA))) g.FillPath(b, RoundRect(r, 11.5f));
                else if (bg > 0) using (var b = new SolidBrush(Wh(a, bg))) g.FillPath(b, RoundRect(r, 11.5f));
                Color c = Wh(a, on ? 250 : hot == id ? 225 : 185);
                DrawTabIcon(g, i, on ? r.X + 18 : r.X + r.Width / 2, r.Y + r.Height / 2, c);
                if (on)
                    using (var b = new SolidBrush(c))
                        g.DrawString(TabNames[i], fTab, b, new RectangleF(r.X + 31, r.Y + 4.5f, r.Width - 35, 16), sfNear);
                Page p = (Page)i;
                if (live) AddHit(id, r, delegate { if (page != p) { page = p; OnPageShown(); Retarget(); } });
                x += w[i] + 4;
            }
        }

        // Tab icons drawn in code, about 14 px across: note, library list, sparkle, script, gear.
        static void DrawTabIcon(Graphics g, int i, float cx, float cy, Color c)
        {
            using (var b = new SolidBrush(c))
            using (var pen = new Pen(c, 1.6f))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                switch (i)
                {
                    case 0:
                        g.FillEllipse(b, cx - 6, cy + 1.5f, 6.5f, 5f);
                        g.DrawLine(pen, cx + 0.2f, cy + 3.5f, cx + 0.2f, cy - 6.5f);
                        g.DrawBezier(pen, cx + 0.2f, cy - 6.5f, cx + 3.5f, cy - 5.5f, cx + 5.5f, cy - 4f, cx + 5f, cy - 1f);
                        break;
                    case 1:
                        g.DrawLine(pen, cx - 7, cy - 4.5f, cx + 2, cy - 4.5f);
                        g.DrawLine(pen, cx - 7, cy, cx + 2, cy);
                        g.DrawLine(pen, cx - 7, cy + 4.5f, cx - 1, cy + 4.5f);
                        g.FillEllipse(b, cx + 2, cy + 1.5f, 5f, 4f);
                        g.DrawLine(pen, cx + 6.3f, cy + 3f, cx + 6.3f, cy - 5f);
                        break;
                    case 2:
                        g.FillPolygon(b, Star(cx - 1, cy + 1, 6.5f));
                        g.FillPolygon(b, Star(cx + 5, cy - 5, 3f));
                        break;
                    case 3:
                        g.DrawPath(pen, RoundRect(new RectangleF(cx - 7, cy - 6, 14, 12), 2.5f));
                        g.DrawLine(pen, cx - 3.5f, cy - 2, cx + 3.5f, cy - 2);
                        g.DrawLine(pen, cx - 3.5f, cy + 1.8f, cx + 1.5f, cy + 1.8f);
                        break;
                    default:
                        using (var tooth = new Pen(c, 2.6f))
                            for (int k = 0; k < 8; k++)
                            {
                                double t = k * Math.PI / 4;
                                float dx = (float)Math.Cos(t), dy = (float)Math.Sin(t);
                                g.DrawLine(tooth, cx + dx * 4.5f, cy + dy * 4.5f, cx + dx * 7f, cy + dy * 7f);
                            }
                        using (var ring = new Pen(c, 2.2f)) g.DrawEllipse(ring, cx - 4, cy - 4, 8, 8);
                        break;
                }
            }
        }

        static PointF[] Star(float cx, float cy, float r)
        {
            float k = r * 0.24f;
            return new[]
            {
                new PointF(cx, cy - r), new PointF(cx + k, cy - k), new PointF(cx + r, cy), new PointF(cx + k, cy + k),
                new PointF(cx, cy + r), new PointF(cx - k, cy + k), new PointF(cx - r, cy), new PointF(cx - k, cy - k)
            };
        }

        void Pill(Graphics g, string id, RectangleF r, string text, bool on, float a, Action act)
        {
            bool h = hot == id;
            bool ask = Asking(id);
            Color bg = ask ? Color.FromArgb((int)(220 * Clamp01(a)), 196, 52, 60) : on ? AccLight(a) : Wh(a, h ? 44 : 24);
            using (var b = new SolidBrush(bg)) g.FillPath(b, RoundRect(r, Math.Min(r.Height / 2, 11)));
            Color fg = on && !ask ? Color.FromArgb((int)(255 * Clamp01(a)), 14, 14, 18) : Wh(a, h || ask ? 250 : 210);
            using (var b = new SolidBrush(fg))
                g.DrawString(ask ? "Yakin?" : text, fTiny, b, new RectangleF(r.X + 4, r.Y + (r.Height - 14) / 2, r.Width - 8, 15), sfMid);
            if (act != null) AddHit(id, r, act);
        }

        float PillW(Graphics g, string text) { return g.MeasureString(text, fTiny, 300, sfNear).Width + 22; }

        // Lays pills left to right from x, returns the x after the last one.
        float Pills(Graphics g, float x, float y, float h, float a, params object[] items)
        {
            for (int i = 0; i + 3 < items.Length; i += 4)
            {
                string id = (string)items[i], text = (string)items[i + 1];
                bool on = (bool)items[i + 2];
                var act = (Action)items[i + 3];
                float w = PillW(g, text);
                Pill(g, id, new RectangleF(x, y, w, h), text, on, a, act);
                x += w + 6;
            }
            return x;
        }

        void Switch(Graphics g, string id, RectangleF row, string label, bool on, float a, Action act)
        {
            bool h = hot == id;
            if (h) using (var b = new SolidBrush(Wh(a, 14))) g.FillPath(b, RoundRect(row, 8));
            using (var b = new SolidBrush(Wh(a, 215)))
                g.DrawString(label, fComp, b, new RectangleF(row.X + 8, row.Y + (row.Height - 15) / 2, row.Width - 54, 16), sfNear);
            var track = new RectangleF(row.Right - 40, row.Y + (row.Height - 16) / 2, 30, 16);
            using (var b = new SolidBrush(on ? Color.FromArgb((int)(255 * Clamp01(a)), 74, 200, 120) : Wh(a, 50)))
                g.FillPath(b, RoundRect(track, 8));
            using (var b = new SolidBrush(Wh(a, 250)))
                g.FillEllipse(b, on ? track.Right - 14 : track.X + 2, track.Y + 2, 12, 12);
            AddHit(id, row, act);
        }

        static void Txt(Graphics g, string s, Font f, float x, float y, float w, Color c)
        {
            using (var b = new SolidBrush(c)) g.DrawString(s ?? "", f, b, new RectangleF(x, y, w, f.Height + 4), sfNear);
        }

        // ---- the text box over a drawn field ----------------------------------------------------------------

        IslandInput input;
        string fieldId, inputFor;
        RectangleF fieldRect;
        bool pendingFocus;
        readonly Dictionary<string, string> drafts = new Dictionary<string, string>();

        static string Cue(string id)
        {
            if (id == "url") return "Tautan YouTube, atau seret berkas musik ke sini";
            if (id == "list") return "Nama daftar putar baru";
            if (id == "model") return "Nama model, mis. openai/gpt-oss-20b, lalu Enter";
            if (id == "vmodel") return "Model Gemini Live, atau suara Deepgram (aura-2-...)";
            return "Tanya apa saja, lalu Enter";
        }

        string Draft(string id) { string s; return drafts.TryGetValue(id, out s) ? s : ""; }

        bool Typing { get { return input != null && input.Visible && input.ContainsFocus; } }

        void Field(Graphics g, string id, RectangleF r, float a)
        {
            fieldId = id;
            fieldRect = r;
            using (var b = new SolidBrush(Color.FromArgb((int)(255 * Clamp01(a)), IslandInput.Fill)))
                g.FillPath(b, RoundRect(r, 9));
            using (var pen = new Pen(Wh(a, 36), 1f)) g.DrawPath(pen, RoundRect(r, 9));
            bool covered = input != null && input.Visible && inputFor == id;
            if (!covered)
            {
                string d = Draft(id);
                Txt(g, d.Length > 0 ? d : Cue(id), fRow, r.X + 10, r.Y + (r.Height - 17) / 2, r.Width - 20, Wh(a, d.Length > 0 ? 235 : 165));
            }
            AddHit("field", r, delegate { pendingFocus = true; SyncInput(); });
        }

        void SaveDraft()
        {
            if (input != null && inputFor != null) drafts[inputFor] = input.Box.Text;
        }

        // Shows the real text box only once the panel has stopped moving, so it never lags behind the animation.
        void SyncInput()
        {
            bool settled = Math.Abs(sw.X - sw.T) < 1.5f && sh.X >= fieldRect.Bottom + 2;
            bool want = Visible && cur == Mode.Expanded && fieldId != null && settled;
            if (!want)
            {
                if (input != null && input.Visible && (cur != Mode.Expanded || fieldId == null || !Visible))
                {
                    SaveDraft();
                    input.Hide();
                }
                return;
            }
            if (input == null)
            {
                input = new IslandInput();
                input.Owner = this;
                input.Submit += delegate { OnSubmit(); };
                input.Cancel += delegate { OnCancelInput(); };
                input.Deactivate += delegate
                {
                    if (!InsideIsland(Cursor.Position)) leave.Start();
                };
            }
            if (inputFor != fieldId)
            {
                SaveDraft();
                inputFor = fieldId;
                input.Box.Text = Draft(fieldId);
                input.SetCue(Cue(fieldId));
            }
            var scr = new Rectangle(Left + (int)Math.Round((fieldRect.X + 10) * S), Top + (int)Math.Round((fieldRect.Y + 4) * S),
                (int)Math.Round((fieldRect.Width - 20) * S), (int)Math.Round((fieldRect.Height - 8) * S));
            input.Place(scr, S);
            if (!input.Visible) { input.Show(); input.SetCue(Cue(fieldId)); }
            if (pendingFocus) { pendingFocus = false; input.FocusBox(); }
        }

        bool InsideIsland(Point p)
        {
            float x0 = Left + ((MaxW - sw.X) / 2f) * S;
            return p.X >= x0 && p.X <= x0 + sw.X * S && p.Y >= Top && p.Y <= Top + sh.X * S;
        }

        void OnSubmit()
        {
            string text = input.Box.Text.Trim();
            if (inputFor == "url") StartDownload(text);
            else if (inputFor == "list") CreateList(text);
            else if (inputFor == "ai") Send(text);
            else if (inputFor == "model") AddModel(text);
            else if (inputFor == "vmodel") AddVoice(text);
        }

        void OnCancelInput()
        {
            if (inputFor == "list") { input.Box.Text = ""; drafts["list"] = ""; newList = false; Render(); return; }
            if (inputFor == "model" || inputFor == "vmodel") { input.Box.Text = ""; drafts[inputFor] = ""; addModel = false; Render(); return; }
            if (input.Box.Text.Length > 0) { input.Box.Text = ""; return; }
            Collapse();
        }

        void Collapse()
        {
            hover = false;
            leave.Stop();
            if (input != null && input.Visible) { SaveDraft(); input.Hide(); }
            HoldThenMini();
            Retarget();
        }

        void ClearField(string id)
        {
            drafts[id] = "";
            if (input != null && inputFor == id) input.Box.Text = "";
        }

        // ---- page: Pustaka ------------------------------------------------------------------------------------

        List<Song> songs = new List<Song>();
        string listName;      // null = all songs
        int listTop;
        bool newList, dlBusy, toolBusy;
        int dlPct = -1;
        string ytStatus = "";
        Song addTo;
        const int Rows = 6;
        const float RowH = 26;

        void ReloadSongs()
        {
            if (listName != null && !Library.PlaylistNames().Contains(listName)) listName = null;
            songs = listName == null ? Library.All() : Library.Playlist(listName);
            listTop = Math.Max(0, Math.Min(listTop, songs.Count - Rows));
        }

        void DrawLibrary(Graphics g, float ex, float a)
        {
            float W = ExpSize.Width, x = ex + 18, y = 36, inner = W - 36;
            if (!YtDlp.Installed)
            {
                Txt(g, "yt-dlp belum terpasang", fComp, x, y, inner - 100, Wh(a, 230));
                Txt(g, "Untuk tautan YouTube, sekitar 25 MB", fTiny, x, y + 17, inner - 190, Wh(a, 210));
                fieldId = null;
                Pill(g, "tool", new RectangleF(ex + W - 18 - 90, y, 90, 30), toolBusy ? "Memasang..." : "Pasang", true, a,
                    toolBusy ? (Action)null : delegate { InstallTool(); });
                Pill(g, "pick", new RectangleF(ex + W - 18 - 90 - 6 - 84, y, 84, 30), "Berkas...", false, a,
                    impBusy ? (Action)null : delegate { PickFiles(); });
            }
            else
            {
                var urlBox = new RectangleF(x, y, inner - 96 - 90, 30);
                if (newList)
                {
                    // the new list is named in the chips row below; this row waits
                    using (var b = new SolidBrush(Color.FromArgb((int)(110 * Clamp01(a)), IslandInput.Fill))) g.FillPath(b, RoundRect(urlBox, 9));
                    string held = Draft("url");
                    Txt(g, held.Length > 0 ? held : Cue("url"), fRow, urlBox.X + 10, urlBox.Y + (urlBox.Height - 17) / 2, urlBox.Width - 20, Wh(a, 80));
                    Pill(g, "pick", new RectangleF(ex + W - 18 - 88 - 6 - 84, y, 84, 30), "Berkas...", false, a * 0.45f, null);
                    Pill(g, "go", new RectangleF(ex + W - 18 - 88, y, 88, 30), "Unduh", true, a * 0.45f, null);
                }
                else
                {
                    Field(g, "url", urlBox, a);
                    Pill(g, "pick", new RectangleF(ex + W - 18 - 88 - 6 - 84, y, 84, 30), "Berkas...", false, a,
                        impBusy ? (Action)null : delegate { PickFiles(); });
                    Pill(g, "go", new RectangleF(ex + W - 18 - 88, y, 88, 30), dlBusy ? "Batal" : "Unduh", true, a, delegate
                    {
                        if (dlBusy) YtDlp.Cancel();
                        else StartDownload(CurrentText("url"));
                    });
                }
            }

            if ((dlBusy || impBusy) && dlPct >= 0)
            {
                using (var b = new SolidBrush(Wh(a, 40))) g.FillPath(b, RoundRect(new RectangleF(x, 70, inner, 3), 1.5f));
                using (var b = new SolidBrush(Wh(a, 230))) g.FillPath(b, RoundRect(new RectangleF(x, 70, Math.Max(3, inner * dlPct / 100f), 3), 1.5f));
            }
            Txt(g, ytStatus, fTiny, x, 76, inner, Wh(a, 210));

            // playlists, or the playlists to add a song to
            float cy = 96, ch = 22;
            var save = g.Save();
            g.IntersectClip(newList && addTo == null ? new RectangleF(x - 1, cy - 4, inner + 2, 30) : new RectangleF(x, cy - 1, inner, ch + 2));
            if (addTo != null)
            {
                Txt(g, "Tambahkan ke:", fTiny, x, cy + 4, 90, Wh(a, 210));
                float px = x + 84;
                var names = Library.PlaylistNames();
                if (names.Count == 0) Txt(g, "belum ada daftar putar, buat dulu dengan + Daftar", fTiny, px, cy + 4, inner - 84, Wh(a, 210));
                foreach (string n in names)
                {
                    string name = n;
                    float w = PillW(g, name);
                    Pill(g, "add:" + name, new RectangleF(px, cy, w, ch), name, false, a, delegate
                    {
                        Library.AddToPlaylist(name, addTo.Id);
                        ytStatus = "\"" + addTo.Title + "\" ditambahkan ke " + name;
                        addTo = null;
                    });
                    px += w + 6;
                }
                Pill(g, "addcancel", new RectangleF(px, cy, PillW(g, "Batal"), ch), "Batal", false, a, delegate { addTo = null; });
            }
            else if (newList)
            {
                Field(g, "list", new RectangleF(x, cy - 3, inner - 142, 28), a);
                Pill(g, "listok", new RectangleF(x + inner - 134, cy - 3, 64, 28), "Buat", true, a, delegate { CreateList(CurrentText("list")); });
                Pill(g, "listcancel", new RectangleF(x + inner - 64, cy - 3, 64, 28), "Batal", false, a, delegate
                {
                    newList = false;
                    ClearField("list");
                });
            }
            else
            {
                float px = x;
                px = Pills(g, px, cy, ch, a, "list:", "Semua lagu", listName == null, (Action)delegate { listName = null; listTop = 0; ReloadSongs(); });
                foreach (string n in Library.PlaylistNames())
                {
                    string name = n;
                    bool on = listName == name;
                    px = Pills(g, px, cy, ch, a, "list:" + name, name, on, (Action)delegate { listName = name; listTop = 0; ReloadSongs(); });
                    if (on)
                    {
                        Pill(g, "dellist", new RectangleF(px - 2, cy, Asking("dellist") ? 52 : 24, ch), "\u2715", false, a, delegate
                        {
                            if (!Confirmed("dellist")) return;
                            Library.DeletePlaylist(name);
                            listName = null;
                            ReloadSongs();
                        });
                        px += (Asking("dellist") ? 52 : 24) + 4;
                    }
                }
                Pills(g, px, cy, ch, a, "newlist", "+ Daftar", false, (Action)delegate { newList = !newList; pendingFocus = newList; });
            }
            g.Restore(save);

            // songs
            float ly = 126;
            if (songs.Count == 0)
            {
                string msg = listName == null ? "Belum ada lagu. Seret berkas musik ke sini, pilih Berkas..., atau tempel tautan YouTube." : "Daftar kosong. Isi lewat Semua lagu: arahkan kursor, tekan + daftar.";
                using (var b = new SolidBrush(Wh(a, 210)))
                    g.DrawString(msg, fComp, b, new RectangleF(ex, ly + 60, W, 18), sfMid);
            }
            Song playing = LocalPlayer.I.Current;
            for (int i = 0; i < Rows && listTop + i < songs.Count; i++)
            {
                int idx = listTop + i;
                Song s = songs[idx];
                var row = new RectangleF(x - 6, ly + i * RowH, inner + 12, RowH - 2);
                string id = "song:" + s.Id;
                bool h = hot != null && (hot == id || hot.EndsWith(":" + s.Id));
                bool isCur = playing != null && playing.Id == s.Id;
                if (h || isCur) using (var b = new SolidBrush(Wh(a, isCur ? 26 : 14))) g.FillPath(b, RoundRect(row, 8));
                AddHit(id, row, delegate { addTo = null; LocalPlayer.I.PlayList(songs, idx); });
                float tw = h ? inner - 168 : inner - 50;
                if (isCur)
                    using (var b = new SolidBrush(Wh(a, 230)))
                        g.FillPolygon(b, new[] { new PointF(x, row.Y + 8), new PointF(x, row.Y + 17), new PointF(x + 7, row.Y + 12.5f) });
                float tx = x + 14;
                Txt(g, s.Title, fComp, tx, row.Y + 5, tw - 14, Wh(a, 240));
                if (!h)
                {
                    using (var b = new SolidBrush(Wh(a, 210)))
                        g.DrawString(Fmt(TimeSpan.FromSeconds(s.Seconds)), fTiny, b, new RectangleF(x + inner - 44, row.Y + 6, 44, 14), sfFar);
                    continue;
                }
                Song song = s;
                float bx = x + inner - 150;
                Pill(g, "plus:" + s.Id, new RectangleF(bx, row.Y + 3, 46, 18), "+ daftar", false, a, delegate { addTo = song; });
                Pill(g, "lyr:" + s.Id, new RectangleF(bx + 50, row.Y + 3, 44, 18), "teks", false, a, delegate { FindLyrics(song); });
                string did = "del:" + s.Id;
                Pill(g, did, new RectangleF(bx + 98, row.Y + 3, 52, 18), listName == null ? "hapus" : "keluarkan", false, a, delegate { RemoveSong(song, did); });
            }
            if (songs.Count > Rows)
            {
                float trackH = Rows * RowH - 6, th = Math.Max(16, trackH * Rows / songs.Count);
                float ty = ly + (trackH - th) * listTop / Math.Max(1, songs.Count - Rows);
                using (var b = new SolidBrush(Wh(a, 70))) g.FillPath(b, RoundRect(new RectangleF(ex + W - 9, ty, 3, th), 1.5f));
            }

            // play modes and tools
            float by = 290, bh = 22;
            var lp = LocalPlayer.I;
            float bx2 = Pills(g, x, by, bh, a,
                "shuffle", "Acak: " + (lp.Shuffle ? "nyala" : "mati"), lp.Shuffle, (Action)delegate { lp.SetShuffle(!lp.Shuffle); },
                "repeat", "Ulang: " + (lp.Repeat == 0 ? "mati" : lp.Repeat == 1 ? "semua" : "satu"), lp.Repeat > 0, (Action)delegate { lp.CycleRepeat(); });
            float rw = PillW(g, "Kunci API") + 6 + (YtDlp.Installed ? PillW(g, "Perbarui yt-dlp") + 6 : 0);
            float rx = Math.Max(bx2, x + inner - rw + 6);
            rx = Pills(g, rx, by, bh, a, "keys", "Kunci API", false, (Action)delegate { KeysWindow.ShowWindow("groq"); });
            if (YtDlp.Installed)
                Pills(g, rx, by, bh, a, "update", toolBusy ? "Memperbarui..." : "Perbarui yt-dlp", false, toolBusy ? null : (Action)delegate { InstallTool(); });
        }

        string CurrentText(string id)
        {
            if (input != null && inputFor == id) return input.Box.Text.Trim();
            return Draft(id).Trim();
        }

        void Post(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke((Action)delegate { if (!IsDisposed) { a(); Render(); } });
        }

        void InstallTool()
        {
            toolBusy = true;
            if (!YtDlp.Installed)
                YtDlp.Install(m => Post(delegate { ytStatus = m; }), (ok, m) => Post(delegate { toolBusy = false; ytStatus = m; }));
            else
            {
                ytStatus = "Memperbarui yt-dlp...";
                YtDlp.SelfUpdate((ok, m) => Post(delegate { toolBusy = false; ytStatus = m; }));
            }
            Render();
        }

        bool impBusy;

        void PickFiles()
        {
            if (impBusy) return;
            string[] picked = null;
            using (var d = new OpenFileDialog { Filter = Importer.Filter, Multiselect = true, Title = "Tambah musik" })
                if (d.ShowDialog() == DialogResult.OK) picked = d.FileNames;
            if (picked != null) ImportPaths(picked);
        }

        // From the file picker or a drop on the island.
        void ImportPaths(IEnumerable<string> paths)
        {
            if (impBusy) return;
            var files = Importer.Expand(paths);
            if (files.Count == 0) { ytStatus = "Tidak ada berkas musik yang didukung (mp3, m4a, aac, wav, flac, wma, ogg, opus)."; Render(); return; }
            impBusy = true;
            dlPct = 0;
            ytStatus = "Menambahkan " + files.Count + " berkas...";
            Importer.Run(files,
                (n, total) => Post(delegate { dlPct = total == 0 ? 0 : n * 100 / total; ytStatus = "Menambahkan " + Math.Min(n + 1, total) + "/" + total + "..."; }),
                (added, skipped) => Post(delegate
                {
                    impBusy = false;
                    dlPct = -1;
                    foreach (Song s in added)
                    {
                        Library.Add(s);
                        if (listName != null) Library.AddToPlaylist(listName, s.Id);
                    }
                    ytStatus = added.Count == 0 ? "Gagal menambahkan berkas."
                        : added.Count + " lagu ditambahkan" + (skipped > 0 ? ", " + skipped + " dilewati." : ".");
                    ReloadSongs();
                    if (page != Page.Library || cur != Mode.Expanded)
                        ShowToast(new Notify.Message { Title = "Musik ditambahkan", Body = ytStatus });
                }));
            Render();
        }

        void StartDownload(string text)
        {
            if (dlBusy) return;
            string clean;
            if (!YtDlp.TryParseUrl(text, out clean)) { ytStatus = "Tempel tautan YouTube yang valid."; Render(); return; }
            if (YtDlp.IsPlaylistUrl(clean)) { StartPlaylist(clean); return; }
            dlBusy = true;
            dlPct = 0;
            ytStatus = "Mengunduh...";
            YtDlp.Download(clean,
                p => Post(delegate { dlPct = p; ytStatus = "Mengunduh... " + p + "%"; }),
                (song, err) => Post(delegate
                {
                    dlBusy = false;
                    dlPct = -1;
                    if (song != null)
                    {
                        Library.Add(song);
                        ytStatus = "Selesai: " + song.Title;
                        ClearField("url");
                        ReloadSongs();
                        if (page != Page.Library || cur != Mode.Expanded)
                            ShowToast(new Notify.Message { Title = "Lagu siap diputar", Body = song.Title });
                    }
                    else ytStatus = "Gagal: " + err;
                }));
            Render();
        }

        void StartPlaylist(string url)
        {
            dlBusy = true;
            dlPct = 0;
            ytStatus = "Membaca playlist...";
            string plName = null;
            int total = 0, count = 0;
            bool cut = false;
            YtDlp.DownloadPlaylist(url,
                (name, n, truncated) => Post(delegate { plName = Library.EnsurePlaylist(name); total = n; cut = truncated; ytStatus = "Playlist \"" + plName + "\": " + n + " lagu"; }),
                (i, p) => Post(delegate { if (total > 0) { dlPct = (i * 100 + p) / total; ytStatus = "Mengunduh " + Math.Min(i + 1, total) + "/" + total + " (" + p + "%)"; } }),
                (id, song) => Post(delegate
                {
                    if (song != null) Library.Add(song);
                    if (plName != null) Library.AddToPlaylist(plName, id);
                    count++;
                    ReloadSongs();
                }),
                (failed, err) => Post(delegate
                {
                    dlBusy = false;
                    dlPct = -1;
                    if (err != null && count == 0) ytStatus = "Gagal: " + err;
                    else
                    {
                        ytStatus = count + " lagu masuk ke \"" + plName + "\"" + (failed > 0 ? ", " + failed + " gagal" : "") +
                            (cut ? ". Hanya " + YtDlp.PlaylistMax + " pertama." : err != null ? ". " + err : ".");
                        ClearField("url");
                        if (plName != null) listName = plName;
                        listTop = 0;
                        ReloadSongs();
                        if (page != Page.Library || cur != Mode.Expanded)
                            ShowToast(new Notify.Message { Title = "Playlist siap", Body = ytStatus });
                    }
                    Render();
                }));
            Render();
        }

        void CreateList(string name)
        {
            if (!Library.CreatePlaylist(name)) { ytStatus = "Nama daftar kosong atau sudah ada."; Render(); return; }
            listName = null;
            listTop = 0;
            newList = false;
            ClearField("list");
            ytStatus = "Daftar \"" + name.Trim() + "\" dibuat. Arahkan kursor ke lagu, lalu tekan + daftar.";
            ReloadSongs();
            Render();
        }

        void RemoveSong(Song s, string id)
        {
            if (listName != null)
            {
                Library.RemoveFromPlaylist(listName, s.Id);
                ReloadSongs();
                return;
            }
            if (!Confirmed(id)) return;
            if (LocalPlayer.I.Current != null && LocalPlayer.I.Current.Id == s.Id) LocalPlayer.I.Stop();
            Library.Remove(s.Id);
            ReloadSongs();
        }

        // Looks everywhere again (LRCLIB, captions, and a transcription when a key is stored).
        void FindLyrics(Song s)
        {
            ytStatus = "Mencari teks untuk " + s.Title + "...";
            SongLyrics.Resolve(s, true, (l, src, fresh) => Post(delegate
            {
                ytStatus = l != null ? "Teks ditemukan: " + src : "Teks tidak ditemukan." + (Transcribe.Available ? "" : " Tambahkan kunci Groq atau Deepgram untuk membuat transkrip.");
                SongLyrics.Raise(s.Id);
            }));
        }

        // ---- page: assistant ------------------------------------------------------------------------------------
        // The conversation lives in memory only and is cleared with "Baru".

        static readonly List<AiMessage> history = new List<AiMessage>();
        bool aiBusy;
        string aiStatus = "";
        float chatScroll;     // pixels above the bottom
        float chatHeight;

        void DrawAi(Graphics g, float ex, float a)
        {
            float W = ExpSize.Width, x = ex + 18, inner = W - 36;
            string prov = Settings.Get("ai.provider", "groq");
            float px = x;
            foreach (var p in Ai.Providers)
            {
                string id = p.Id;
                px = Pills(g, px, 36, 22, a, "prov:" + id, p.Name, prov == id, (Action)delegate { Settings.Set("ai.provider", id); aiStatus = ""; });
            }
            float rw = PillW(g, "Kunci") + 6 + PillW(g, "Baru");
            float rx = x + inner - rw;
            var prv = Ai.Find(prov);
            string ml = ShortModel(Ai.Model(prv)) + "  \u25BE";
            Pill(g, "model", new RectangleF(px, 36, Math.Min(PillW(g, ml), rx - px - 8), 22), ml, modelPick, a, delegate
            {
                modelPick = !modelPick;
                modelTop = 0;
                if (!modelPick) addModel = false;
            });
            rx = Pills(g, rx, 36, 22, a, "aikeys", "Kunci", false, (Action)delegate { KeysWindow.ShowWindow(Settings.Get("ai.provider", "groq")); });
            Pills(g, rx, 36, 22, a, "aifresh", "Baru", false, (Action)delegate { history.Clear(); chatScroll = 0; aiStatus = ""; });

            // conversation, newest at the bottom
            var area = new RectangleF(x, 66, inner, 198);
            using (var b = new SolidBrush(Wh(a, 10))) g.FillPath(b, RoundRect(new RectangleF(area.X - 6, area.Y - 4, area.Width + 12, area.Height + 8), 10));
            if (modelPick) DrawModels(g, area, prv, a);
            else if (history.Count == 0 && !aiBusy && voice == null)
            {
                var p = Ai.Find(prov);
                string m = Secrets.Has(prov) ? "Model: " + Ai.Model(p) : "Belum ada kunci " + p.Name + ". Klik Kunci untuk menambahkan.";
                using (var b = new SolidBrush(Wh(a, 210)))
                {
                    g.DrawString("Tanya apa saja. Jawabannya tampil di sini.", fComp, b, new RectangleF(ex, area.Y + 76, W, 18), sfMid);
                    g.DrawString(m, fTiny, b, new RectangleF(ex, area.Y + 96, W, 16), sfMid);
                }
            }
            else
            {
                var save = g.Save();
                g.IntersectClip(area);
                float tw = inner - 8;
                var blocks = new List<KeyValuePair<AiMessage, float>>();
                float total = 0;
                var shown = new List<AiMessage>(history);
                string live = voice != null ? (voice.Live ?? "").Trim() : "";
                if (live.Length > 0 && (history.Count == 0 || history[history.Count - 1].Text != live)) shown.Add(new AiMessage(voice.LiveRole, live));
                foreach (var m in shown)
                {
                    float hgt = g.MeasureString(m.Text, fComp, new SizeF(tw, 4000), sfWrap).Height + 18;
                    blocks.Add(new KeyValuePair<AiMessage, float>(m, hgt));
                    total += hgt + 8;
                }
                if (aiBusy) total += 22;
                chatHeight = total;
                chatScroll = Math.Max(0, Math.Min(chatScroll, total - area.Height));
                float y = area.Bottom - total + chatScroll;
                foreach (var kv in blocks)
                {
                    if (y + kv.Value > area.Y && y < area.Bottom)
                    {
                        bool user = kv.Key.Role == "user";
                        Txt(g, user ? "Kamu" : "Asisten", fSmall, area.X, y, 100, Wh(a, user ? 120 : 150));
                        using (var b = new SolidBrush(Wh(a, user ? 200 : 245)))
                            g.DrawString(kv.Key.Text, fComp, b, new RectangleF(area.X, y + 15, tw, kv.Value), sfWrap);
                    }
                    y += kv.Value + 8;
                }
                if (aiBusy) Txt(g, "Asisten sedang berpikir...", fComp, area.X, y + 2, tw, Wh(a, 210));
                g.Restore(save);
                if (total > area.Height)
                {
                    float th = Math.Max(16, area.Height * area.Height / total);
                    float ty = area.Y + (area.Height - th) * (1 - chatScroll / (total - area.Height));
                    using (var b = new SolidBrush(Wh(a, 70))) g.FillPath(b, RoundRect(new RectangleF(ex + W - 9, ty, 3, th), 1.5f));
                }
            }

            DrawMic(g, new RectangleF(x, 272, 30, 30), a);
            float fx = x + 36, fw = inner - 80 - 36;
            if (voice != null)
            {
                DrawVoiceBar(g, new RectangleF(fx, 272, fw, 30), a);
                Pill(g, "send", new RectangleF(x + inner - 72, 272, 72, 30), "Selesai", true, a, delegate { StopVoice(); });
            }
            else if (addModel)
            {
                string fid = voiceTab ? "vmodel" : "model";
                Field(g, fid, new RectangleF(fx, 272, fw, 30), a);
                Pill(g, "send", new RectangleF(x + inner - 72, 272, 72, 30), modelBusy ? "..." : "Tambah", true, a,
                    modelBusy ? (Action)null : delegate { if (voiceTab) AddVoice(CurrentText("vmodel")); else AddModel(CurrentText("model")); });
            }
            else
            {
                Field(g, "ai", new RectangleF(fx, 272, fw, 30), a);
                Pill(g, "send", new RectangleF(x + inner - 72, 272, 72, 30), aiBusy ? "..." : "Kirim", true, a,
                    aiBusy ? (Action)null : delegate { Send(CurrentText("ai")); });
            }

            Txt(g, aiStatus, fTiny, x, 310, inner - 110, Wh(a, 210));
            var last = history.LastOrDefault(m => m.Role == "assistant");
            if (last != null)
                Pill(g, "copy", new RectangleF(x + inner - 100, 307, 100, 20), "Salin jawaban", false, a, delegate
                {
                    try { Clipboard.SetText(last.Text); aiStatus = "Jawaban disalin."; } catch { }
                });
        }

        // ---- model picker: the suggested models, the user's own, and a row to add one -----------------------------

        bool modelPick, addModel, modelBusy;
        int modelTop;
        const float ModelRowH = 24;

        static string ShortModel(string m)
        {
            int i = m.LastIndexOf('/');
            return i >= 0 && i < m.Length - 1 ? m.Substring(i + 1) : m;
        }

        int ModelRows { get { return (int)((198 - 28) / ModelRowH); } }

        void DrawModels(Graphics g, RectangleF area, AiProvider p, float a)
        {
            Pills(g, area.X, area.Y, 22, a,
                "mtab:text", "Teks (" + p.Name + ")", !voiceTab, (Action)delegate { voiceTab = false; modelTop = 0; addModel = false; },
                "mtab:voice", "Suara", voiceTab, (Action)delegate { voiceTab = true; modelTop = 0; addModel = false; });
            var list = new RectangleF(area.X, area.Y + 28, area.Width, area.Height - 28);
            if (voiceTab) DrawVoiceModels(g, list, a);
            else DrawTextModels(g, list, p, a);
        }

        void DrawTextModels(Graphics g, RectangleF area, AiProvider p, float a)
        {
            var models = Ai.Models(p);
            var added = Ai.Added(p);
            string current = Ai.Model(p);
            int total = models.Count + 1;
            modelTop = Math.Max(0, Math.Min(modelTop, total - ModelRows));
            for (int i = 0; i < ModelRows && modelTop + i < total; i++)
            {
                int idx = modelTop + i;
                var row = new RectangleF(area.X - 4, area.Y + i * ModelRowH, area.Width + 8, ModelRowH - 2);
                if (idx == models.Count)
                {
                    bool h = hot == "addmodel";
                    if (h || addModel) using (var b = new SolidBrush(Wh(a, 16))) g.FillPath(b, RoundRect(row, 7));
                    Txt(g, "+ Tambah model", fComp, row.X + 10, row.Y + 4, row.Width - 20, Wh(a, h ? 245 : 180));
                    AddHit("addmodel", row, delegate { addModel = true; pendingFocus = true; });
                    continue;
                }
                string m = models[idx];
                string id = "m:" + m;
                bool hov = hot != null && (hot == id || hot == "mdel:" + m);
                bool on = m == current;
                if (hov || on) using (var b = new SolidBrush(Wh(a, on ? 26 : 14))) g.FillPath(b, RoundRect(row, 7));
                AddHit(id, row, delegate
                {
                    Ai.SetModel(p, m);
                    modelPick = false;
                    addModel = false;
                    aiStatus = "Model " + p.Name + ": " + m;
                });
                if (on)
                    using (var pen = new Pen(Wh(a, 240), 1.6f))
                        g.DrawLines(pen, new[] { new PointF(row.X + 9, row.Y + 11), new PointF(row.X + 12, row.Y + 14.5f), new PointF(row.X + 18, row.Y + 7.5f) });
                Txt(g, m, fComp, row.X + 24, row.Y + 4, row.Width - 100, Wh(a, on ? 250 : 215));
                if (added.Contains(m) && hov)
                {
                    string did = "mdel:" + m;
                    Pill(g, did, new RectangleF(row.Right - 58, row.Y + 3, 52, 16), "hapus", false, a, delegate
                    {
                        if (!Confirmed(did)) return;
                        Ai.RemoveModel(p, m);
                        aiStatus = "Model " + m + " dihapus dari daftar.";
                    });
                }
                else
                {
                    string tag = m == p.DefaultModel ? "bawaan" : p.Suggested.Contains(m) ? "saran" : added.Contains(m) ? "tambahanmu" : "";
                    using (var b = new SolidBrush(Wh(a, 210)))
                        g.DrawString(tag, fSmall, b, new RectangleF(row.Right - 86, row.Y + 6, 78, 14), sfFar);
                }
            }
        }

        bool voiceTab;

        void DrawVoiceModels(Graphics g, RectangleF area, float a)
        {
            var all = Voices.All();
            int total = all.Count + 1;
            modelTop = Math.Max(0, Math.Min(modelTop, total - ModelRows));
            for (int i = 0; i < ModelRows && modelTop + i < total; i++)
            {
                int idx = modelTop + i;
                var row = new RectangleF(area.X - 4, area.Y + i * ModelRowH, area.Width + 8, ModelRowH - 2);
                if (idx == all.Count)
                {
                    bool h = hot == "addvoice";
                    if (h || addModel) using (var b = new SolidBrush(Wh(a, 16))) g.FillPath(b, RoundRect(row, 7));
                    Txt(g, "+ Tambah model Live atau suara Deepgram", fComp, row.X + 10, row.Y + 4, row.Width - 20, Wh(a, h ? 245 : 180));
                    AddHit("addvoice", row, delegate { addModel = true; pendingFocus = true; });
                    continue;
                }
                string prov = all[idx].Key, m = all[idx].Value;
                string id = "v:" + prov + ":" + m;
                bool hov = hot != null && (hot == id || hot == "vdel:" + m);
                bool on = prov == "gvoice" ? Voices.GeminiVoice == m : Voices.Provider == prov && Voices.Current == m;
                if (hov || on) using (var b = new SolidBrush(Wh(a, on ? 26 : 14))) g.FillPath(b, RoundRect(row, 7));
                AddHit(id, row, delegate
                {
                    Voices.Select(prov, m);
                    modelPick = false;
                    addModel = false;
                    aiStatus = prov == "gvoice" ? "Suara bicara Gemini: " + m + " (berlaku di sesi berikutnya)" : "Suara: " + (prov == "gemini" ? "Gemini Live, " : "Deepgram, ") + m;
                });
                if (on)
                    using (var pen = new Pen(Wh(a, 240), 1.6f))
                        g.DrawLines(pen, new[] { new PointF(row.X + 9, row.Y + 11), new PointF(row.X + 12, row.Y + 14.5f), new PointF(row.X + 18, row.Y + 7.5f) });
                string desc = Voices.Describe(m);
                Txt(g, desc.Length > 0 ? desc : m, fComp, row.X + 24, row.Y + 4, row.Width - 130, Wh(a, on ? 250 : 215));
                if (Voices.Added(prov).Contains(m) && hov)
                {
                    string did = "vdel:" + m;
                    Pill(g, did, new RectangleF(row.Right - 58, row.Y + 3, 52, 16), "hapus", false, a, delegate
                    {
                        if (!Confirmed(did)) return;
                        Voices.Remove(prov, m);
                    });
                }
                else
                    using (var b = new SolidBrush(Wh(a, 210)))
                        g.DrawString(prov == "gemini" ? "Gemini Live" : prov == "gvoice" ? "Suara Gemini" : "Deepgram", fSmall, b, new RectangleF(row.Right - 106, row.Y + 6, 98, 14), sfFar);
            }
        }

        // Gemini names are checked for Live support, Deepgram names against its list of voices.
        void AddVoice(string name)
        {
            name = name.Trim();
            if (name.Length == 0 || modelBusy) return;
            string prov = Voices.IsDeepgram(name) ? "deepgram" : "gemini";
            if (!Secrets.Has(prov)) { aiStatus = "Belum ada kunci " + (prov == "gemini" ? "Gemini" : "Deepgram") + " untuk memeriksa nama itu."; Render(); return; }
            modelBusy = true;
            aiStatus = "Memeriksa " + name + "...";
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<string> available = null;
                string error = null;
                try { available = Voices.Available(prov); }
                catch (Exception ex) { error = ex.Message; }
                Post(delegate
                {
                    modelBusy = false;
                    if (available != null && !available.Contains(name))
                    {
                        var near = available.Where(m => m.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0).Take(3).ToList();
                        aiStatus = (prov == "gemini" ? "Itu bukan model Gemini Live." : "Suara itu tidak ada di Deepgram.") +
                            (near.Count > 0 ? " Mungkin: " + string.Join(", ", near.ToArray()) : "");
                        return;
                    }
                    Voices.Add(prov, name);
                    Voices.Select(prov, name);
                    addModel = false;
                    ClearField("vmodel");
                    aiStatus = available != null ? "Suara " + name + " ditambahkan dan dipakai." : "Tidak bisa memeriksa (" + error + "), tetap ditambahkan.";
                });
            });
            Render();
        }

        // ---- spoken conversation --------------------------------------------------------------------------------

        VoiceSession voice;

        void ToggleVoice() { if (voice != null) StopVoice(); else StartVoice(); }

        void StartVoice()
        {
            if (voice != null) return;
            string prov = Voices.Provider;
            if (!Secrets.Has(prov))
            {
                aiStatus = "Belum ada kunci " + (prov == "gemini" ? "Gemini" : "Deepgram") + ". Tambahkan lewat Kunci.";
                Render();
                return;
            }
            var v = Voices.Create();
            voice = v;
            aiStatus = "";
            chatScroll = 0;
            v.Changed += delegate
            {
                Post(delegate
                {
                    if (voice != v) return;
                    if (v.State == VoiceSession.St.Ended) VoiceEnded(v);
                    else UpdateMood();
                });
            };
            v.Turn += (role, text) => Post(delegate { history.Add(new AiMessage(role, text)); chatScroll = 0; });
            v.Start();
            Ping("click");
            Retarget();
            UpdateMood();
        }

        void StopVoice() { if (voice != null) voice.Stop(); }

        void VoiceEnded(VoiceSession v)
        {
            voice = null;
            aiStatus = v.Error != null ? "Suara berhenti: " + v.Error : v.Quiet ? "Percakapan suara selesai (diam 15 detik)." : "Percakapan suara selesai.";
            if (cur != Mode.Expanded && v.Error != null) ShowToast(new Notify.Message { Title = "Suara berhenti", Body = v.Error });
            Retarget();
            UpdateMood();
            Memory.Trim();
        }

        static void DrawMicGlyph(Graphics g, float cx, float cy, Color c)
        {
            using (var b = new SolidBrush(c)) g.FillPath(b, RoundRect(new RectangleF(cx - 3, cy - 7.5f, 6, 10), 3));
            using (var pen = new Pen(c, 1.6f))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                g.DrawArc(pen, cx - 6, cy - 6.5f, 12, 10, 0, 180);
                g.DrawLine(pen, cx, cy + 3.5f, cx, cy + 6.5f);
                g.DrawLine(pen, cx - 3, cy + 6.5f, cx + 3, cy + 6.5f);
            }
        }

        void DrawMic(Graphics g, RectangleF r, float a)
        {
            bool on = voice != null, h = hot == "mic";
            Color bg = on ? Color.FromArgb((int)(230 * Clamp01(a)), 196, 52, 60) : Wh(a, h ? 44 : 24);
            using (var b = new SolidBrush(bg)) g.FillEllipse(b, r);
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            if (on) using (var b = new SolidBrush(Wh(a, 250))) g.FillPath(b, RoundRect(new RectangleF(cx - 5, cy - 5, 10, 10), 2));
            else DrawMicGlyph(g, cx, cy, Wh(a, h ? 250 : 220));
            AddHit("mic", r, delegate { ToggleVoice(); });
        }

        static Color StateColor(VoiceSession.St s, float a)
        {
            int al = (int)(255 * Clamp01(a));
            switch (s)
            {
                case VoiceSession.St.Listening: return Color.FromArgb(al, 74, 200, 120);
                case VoiceSession.St.Thinking: return Color.FromArgb(al, 240, 180, 60);
                case VoiceSession.St.Speaking: return Color.FromArgb(al, 90, 160, 255);
                default: return Color.FromArgb(al, 150, 150, 160);
            }
        }

        static string StateText(VoiceSession.St s)
        {
            switch (s)
            {
                case VoiceSession.St.Listening: return "Mendengarkan";
                case VoiceSession.St.Thinking: return "Berpikir";
                case VoiceSession.St.Speaking: return "Bicara";
                case VoiceSession.St.Ended: return "Selesai";
                default: return "Menyambung";
            }
        }

        void DrawVoiceBar(Graphics g, RectangleF r, float a)
        {
            var v = voice;
            using (var b = new SolidBrush(Color.FromArgb((int)(255 * Clamp01(a)), IslandInput.Fill))) g.FillPath(b, RoundRect(r, 9));
            using (var b = new SolidBrush(StateColor(v.State, a))) g.FillEllipse(b, r.X + 10, r.Y + 11, 8, 8);
            Txt(g, StateText(v.State) + "  \u00B7  " + v.Name, fRow, r.X + 24, r.Y + 6, r.Width - 80, Wh(a, 235));
            // level meter: the microphone while listening, a steady pulse while the assistant talks
            float lv = v.State == VoiceSession.St.Speaking ? 0.5f + 0.3f * (float)Math.Sin(Environment.TickCount / 120.0) : v.Level;
            for (int i = 0; i < 5; i++)
            {
                float k = Clamp01(lv * 1.6f - i * 0.18f);
                float bh = 4 + 14 * k;
                using (var b = new SolidBrush(Wh(a, k > 0.05f ? 220 : 70)))
                    g.FillPath(b, RoundRect(new RectangleF(r.Right - 46 + i * 7, r.Y + r.Height / 2 - bh / 2, 4, bh), 2));
            }
        }

        // Checks the name against the provider's model list first, so a typo does not end in a 404 later.
        void AddModel(string name)
        {
            name = name.Trim();
            if (name.Length == 0 || modelBusy) return;
            var p = Ai.Find(Settings.Get("ai.provider", "groq"));
            modelBusy = true;
            aiStatus = "Memeriksa " + name + " di " + p.Name + "...";
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<string> available = null;
                string error = null;
                try { available = Ai.ListModels(p); }
                catch (Exception ex) { error = ex.Message; }
                Post(delegate
                {
                    modelBusy = false;
                    if (available != null && !available.Contains(name))
                    {
                        var near = available.Where(m => m.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.IndexOf(ShortModel(m), StringComparison.OrdinalIgnoreCase) >= 0).Take(3).ToList();
                        aiStatus = "Model itu tidak ada di " + p.Name + "." + (near.Count > 0 ? " Mungkin: " + string.Join(", ", near.ToArray()) : "");
                        return;
                    }
                    Ai.AddModel(p, name);
                    Ai.SetModel(p, name);
                    addModel = false;
                    ClearField("model");
                    aiStatus = available != null ? "Model " + name + " ditambahkan dan dipakai." : "Tidak bisa memeriksa (" + error + "), tetap ditambahkan.";
                });
            });
            Render();
        }

        // Keeps the request small: the latest messages only, and it always starts with the user's turn.
        static List<AiMessage> Recent()
        {
            var list = history.Skip(Math.Max(0, history.Count - 20)).ToList();
            while (list.Count > 0 && list[0].Role != "user") list.RemoveAt(0);
            int chars = 0, keep = 0;
            for (int i = list.Count - 1; i >= 0; i--) { chars += list[i].Text.Length; if (chars > 24000) break; keep++; }
            list = list.Skip(list.Count - Math.Max(1, keep)).ToList();
            while (list.Count > 0 && list[0].Role != "user") list.RemoveAt(0);
            return list;
        }

        void Send(string text)
        {
            if (aiBusy || text.Length == 0) return;
            aiBusy = true;
            history.Add(new AiMessage("user", text));
            ClearField("ai");
            aiStatus = "";
            chatScroll = 0;
            thinking = true;
            UpdateMood();
            string provider = Settings.Get("ai.provider", "groq");
            bool fb = Settings.Get("ai.fallback", "1") == "1";
            var snapshot = Recent();
            ThreadPool.QueueUserWorkItem(delegate
            {
                AiResult result = null;
                string error = null;
                try { result = Ai.Ask(snapshot, Ai.System, provider, fb); }
                catch (Exception ex) { error = ex.Message; }
                Post(delegate { AiDone(result, error, text); });
            });
            Render();
        }

        void AiDone(AiResult r, string error, string asked)
        {
            aiBusy = false;
            thinking = false;
            UpdateMood();
            chatScroll = 0;
            if (r != null)
            {
                history.Add(new AiMessage("assistant", r.Text));
                aiStatus = "Dijawab oleh " + r.Provider + (r.KeyLabel.Length > 0 ? " (" + r.KeyLabel + ")" : "") + ".";
                if (page != Page.Ai || cur != Mode.Expanded)
                    ShowToast(new Notify.Message { Title = "Asisten menjawab", Body = r.Text.Replace('\n', ' ') });
            }
            else
            {
                // take the question back, so the next try starts from a clean turn
                if (history.Count > 0 && history[history.Count - 1].Role == "user") history.RemoveAt(history.Count - 1);
                drafts["ai"] = asked;
                if (input != null && inputFor == "ai") input.Box.Text = asked;
                aiStatus = "Gagal: " + error;
            }
        }

        // ---- page: teleprompter -------------------------------------------------------------------------------

        string script = "";

        void LoadScriptInfo()
        {
            try { script = File.Exists(Teleprompter.LastFile) ? File.ReadAllText(Teleprompter.LastFile) : ""; } catch { script = ""; }
        }

        static int Num(string key, int def, int lo, int hi)
        {
            int v;
            if (!int.TryParse(Settings.Get(key, def.ToString()), out v)) v = def;
            return Math.Max(lo, Math.Min(hi, v));
        }

        void Bump(string key, int def, int lo, int hi, int by)
        {
            Settings.Set(key, Math.Max(lo, Math.Min(hi, Num(key, def, lo, hi) + by)).ToString());
            Teleprompter.Reconfigure();
        }

        void DrawPrompter(Graphics g, float ex, float a)
        {
            float W = ExpSize.Width, x = ex + 18, inner = W - 36;
            int wpm = Num("prompter.wpm", 140, 60, 400), px = Num("prompter.size", 19, 14, 48);
            string first = "";
            foreach (string l in script.Split('\n')) { if (l.Trim().Length > 0) { first = l.Trim(); break; } }
            Txt(g, "Naskah terakhir", fSmall, x, 38, inner, Wh(a, 210));
            if (first.Length == 0)
                Txt(g, "Belum ada naskah. Buka editor untuk menulis atau mengimpor.", fSub, x, 54, inner, Wh(a, 200));
            else
            {
                Txt(g, first, fTitle, x, 53, inner, Wh(a, 245));
                int words = Teleprompter.CountWords(script);
                int secs = (int)Math.Round(words / (double)wpm * 60);
                Txt(g, words + " kata  \u00B7  perkiraan " + (secs / 60) + ":" + (secs % 60).ToString("00") + " pada " + wpm + " wpm", fTiny, x, 76, inner, Wh(a, 210));
            }

            float y = 104;
            Txt(g, "Kecepatan", fComp, x, y + 4, 80, Wh(a, 190));
            Pill(g, "wpm-", new RectangleF(x + 76, y, 26, 24), "\u2212", false, a, delegate { Bump("prompter.wpm", 140, 60, 400, -10); });
            using (var b = new SolidBrush(Wh(a, 240))) g.DrawString(wpm + " wpm", fComp, b, new RectangleF(x + 104, y + 4, 64, 16), sfMid);
            Pill(g, "wpm+", new RectangleF(x + 170, y, 26, 24), "+", false, a, delegate { Bump("prompter.wpm", 140, 60, 400, 10); });

            Txt(g, "Huruf", fComp, x + 222, y + 4, 50, Wh(a, 190));
            Pill(g, "sz-", new RectangleF(x + 266, y, 26, 24), "\u2212", false, a, delegate { Bump("prompter.size", 19, 14, 48, -1); });
            using (var b = new SolidBrush(Wh(a, 240))) g.DrawString(px + " px", fComp, b, new RectangleF(x + 294, y + 4, 48, 16), sfMid);
            Pill(g, "sz+", new RectangleF(x + 344, y, 26, 24), "+", false, a, delegate { Bump("prompter.size", 19, 14, 48, 1); });

            bool mirror = Settings.Get("prompter.mirror", "0") == "1";
            Switch(g, "mirror", new RectangleF(x - 8, 136, inner + 16, 26), "Cermin (untuk kaca teleprompter)", mirror, a, delegate
            {
                Settings.Set("prompter.mirror", mirror ? "0" : "1");
                Teleprompter.Reconfigure();
            });

            float bx = Pills(g, x, 170, 26, a, "pstart", "Mulai", true, (Action)delegate
            {
                if (first.Length == 0) { Teleprompter.ShowEditor(); return; }
                Collapse();
                Teleprompter.Start(script);
            });
            Pills(g, bx, 170, 26, a, "peditor", "Editor naskah", false, (Action)delegate { Teleprompter.ShowEditor(); });
            using (var b = new SolidBrush(Wh(a, 210)))
                g.DrawString("Ctrl+Alt+Spasi jeda  \u00B7  Ctrl+Alt+X hentikan  \u00B7  Ctrl+Alt+P editor", fSmall, b, new RectangleF(x, 199, inner, 14), sfFar);
        }

        // ---- page: settings ----------------------------------------------------------------------------------

        void DrawSetup(Graphics g, float ex, float a)
        {
            float W = ExpSize.Width, x = ex + 18, inner = W - 36;
            Txt(g, "Material", fComp, x, 40, 120, Wh(a, 190));
            float mw = PillW(g, "Liquid Glass") + 6 + PillW(g, "Hitam");
            Pills(g, x + inner - mw, 36, 22, a,
                "mat:liquid", "Liquid Glass", material == "liquid", (Action)delegate { SetMaterial("liquid"); },
                "mat:solid", "Hitam", material != "liquid", (Action)delegate { SetMaterial("solid"); });

            Txt(g, "Mode island", fComp, x, 70, 120, Wh(a, 190));
            string mm = Settings.Get("mini", "auto");
            float nw = PillW(g, "Normal") + 6 + PillW(g, "Mini") + 6 + PillW(g, "Otomatis");
            Pills(g, x + inner - nw, 66, 22, a,
                "mini:normal", "Normal", mm == "normal", (Action)delegate { SetMiniSetting("normal"); hover = true; },
                "mini:mini", "Mini", mm == "mini", (Action)delegate { SetMiniSetting("mini"); },
                "mini:auto", "Otomatis", mm == "auto", (Action)delegate { SetMiniSetting("auto"); hover = true; });

            Txt(g, "Saat musik main", fComp, x, 100, 140, Wh(a, 190));
            string mc = Settings.Get("music.compact", "text");
            float tw = PillW(g, "Judul / lirik") + 6 + PillW(g, "Mata saja");
            Pills(g, x + inner - tw, 96, 22, a,
                "mc:text", "Judul / lirik", mc == "text", (Action)delegate { Settings.Set("music.compact", "text"); Retarget(); },
                "mc:eyes", "Mata saja", mc == "eyes", (Action)delegate { Settings.Set("music.compact", "eyes"); Retarget(); });

            Txt(g, "Warna aksen", fComp, x, 130, 140, Wh(a, 190));
            float sx = x + inner - AccentIds.Length * 26 + 6;
            for (int i = 0; i < AccentIds.Length; i++)
            {
                int k = i;
                var sr = new RectangleF(sx + i * 26, 128, 20, 20);
                using (var b = new LinearGradientBrush(sr, Color.FromArgb((int)(255 * Clamp01(a)), AccentA[i]), Color.FromArgb((int)(255 * Clamp01(a)), AccentB[i]), 45f))
                    g.FillEllipse(b, sr);
                if (AccentIndex == i)
                    using (var pen = new Pen(Wh(a, 245), 1.8f)) g.DrawEllipse(pen, sr.X - 3, sr.Y - 3, sr.Width + 6, sr.Height + 6);
                else if (hot == "acc:" + AccentIds[i])
                    using (var pen = new Pen(Wh(a, 110), 1.4f)) g.DrawEllipse(pen, sr.X - 3, sr.Y - 3, sr.Width + 6, sr.Height + 6);
                AddHit("acc:" + AccentIds[i], new RectangleF(sr.X - 3, sr.Y - 3, sr.Width + 6, sr.Height + 6), delegate { Settings.Set("accent", AccentIds[k]); });
            }

            float cw = (inner - 10) / 2, c2 = x + cw + 10, ry = 158, rh = 28;
            Func<string, string, bool> on = (k, d) => Settings.Get(k, d) == "1";
            Action<string, string> flip = (k, d) => Settings.Set(k, on(k, d) ? "0" : "1");
            Switch(g, "s:sound", new RectangleF(x - 8, ry, cw + 8, rh), "Efek suara", on("sound", "1"), a, delegate { flip("sound", "1"); });
            Switch(g, "s:lyr", new RectangleF(x - 8, ry + rh, cw + 8, rh), "Cari lirik online", on("lyrics.online", "1"), a, delegate { flip("lyrics.online", "1"); });
            Switch(g, "s:notif", new RectangleF(x - 8, ry + 2 * rh, cw + 8, rh), "Notifikasi saat Mini", on("mini.notif", "1"), a, delegate { flip("mini.notif", "1"); });
            Switch(g, "s:auto", new RectangleF(c2, ry, cw + 8, rh), "Mulai bersama Windows", IsAutostart(), a, delegate { SetAutostart(!IsAutostart()); });
            Switch(g, "s:start", new RectangleF(c2, ry + rh, cw + 8, rh), "Ada di menu Start", HasStartShortcut(), a, delegate { SetStartShortcut(!HasStartShortcut()); });
            Switch(g, "s:fb", new RectangleF(c2, ry + 2 * rh, cw + 8, rh), "Asisten pindah penyedia", on("ai.fallback", "1"), a, delegate { flip("ai.fallback", "1"); });

            Switch(g, "s:barge", new RectangleF(x - 8, ry + 3 * rh, cw + 8, rh), "Sela AI saat bicara", on("voice.bargein", "0"), a, delegate { flip("voice.bargein", "0"); });
            using (var b = new SolidBrush(Wh(a, 210)))
                g.DrawString("khusus pemakaian headset", fSmall, b, new RectangleF(c2 + 8, ry + 3 * rh + 8, cw, 14), sfNear);

            float by = 286;
            float bx = Pills(g, x, by, 24, a,
                "keys2", "Kunci API", false, (Action)delegate { KeysWindow.ShowWindow(Settings.Get("ai.provider", "groq")); },
                "folder", "Folder data", false, (Action)delegate { try { Directory.CreateDirectory(Settings.Dir); Process.Start(Settings.Dir); } catch { } },
                "hide", "Sembunyikan", false, (Action)delegate { Collapse(); ToggleHidden(); });
            float qw = PillW(g, "Keluar");
            Pill(g, "quit", new RectangleF(Math.Max(bx, x + inner - qw), by, qw, 24), "Keluar", false, a, delegate
            {
                if (Confirmed("quit")) Application.Exit();
            });
            // version and update
            string vtxt = Updater.Status.Length > 0 ? Updater.Status : "Notchira v" + Updater.Current;
            Txt(g, vtxt.StartsWith("Notchira") ? vtxt : "v" + Updater.Current + "  ·  " + vtxt, fComp, x, 322, inner - 130, Wh(a, 200));
            string ulabel = Updater.Busy ? "Tunggu..." : Updater.Available ? "Perbarui ke v" + Updater.Latest : "Cek pembaruan";
            float uw = PillW(g, ulabel);
            Pill(g, "upd", new RectangleF(x + inner - uw, 318, uw, 24), ulabel, Updater.Available, a, Updater.Busy ? (Action)null : delegate
            {
                if (Updater.Available) Updater.Apply(() => Post(delegate { }), () => BeginInvoke((Action)delegate { Application.Exit(); }));
                else Updater.Check(true, () => Post(delegate { }));
                Render();
            });
            using (var b = new SolidBrush(Wh(a, 210)))
                g.DrawString("Ctrl+Alt+H sembunyikan  \u00B7  Ctrl+Alt+K mini  \u00B7  Ctrl+Alt+A asisten  \u00B7  Ctrl+Alt+V suara", fSmall, b, new RectangleF(x, 350, inner, 14), sfMid);
        }

        // ---- wheel ----------------------------------------------------------------------------------------------

        void ScrollPage(int delta)
        {
            if (page == Page.Library)
            {
                int to = Math.Max(0, Math.Min(listTop - Math.Sign(delta), songs.Count - Rows));
                if (to != listTop) { listTop = to; Render(); }
            }
            else if (page == Page.Ai && modelPick)
            {
                int count = voiceTab ? Voices.All().Count : Ai.Models(Ai.Find(Settings.Get("ai.provider", "groq"))).Count;
                int to = Math.Max(0, Math.Min(modelTop - Math.Sign(delta), count + 1 - ModelRows));
                if (to != modelTop) { modelTop = to; Render(); }
            }
            else if (page == Page.Ai)
            {
                chatScroll = Math.Max(0, chatScroll + delta / 120f * 40);
                Render();
            }
        }
    }
}
