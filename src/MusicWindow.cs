using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace Notchira
{
    // Library window: paste a YouTube link, download the audio, manage playlists, play.
    // It is created when opened and disposed when closed, so it holds no memory while hidden.
    sealed class MusicWindow : Form
    {
        const string AllSongs = "Semua lagu";

        static MusicWindow instance;

        readonly TextBox url = new TextBox();
        readonly Button btnDownload = new Button();
        readonly Button btnCancel = new Button();
        readonly ProgressBar bar = new ProgressBar();
        readonly Label status = new Label();
        readonly Label tool = new Label();
        readonly Button btnTool = new Button();
        readonly ComboBox lists = new ComboBox();
        readonly ListView view = new ListView();
        readonly Button btnShuffle = new Button();
        readonly Button btnRepeat = new Button();
        readonly SynchronizationContext ui;
        List<Song> shown = new List<Song>();
        bool busy;

        public static void ShowWindow()
        {
            if (instance == null || instance.IsDisposed) instance = new MusicWindow();
            instance.Show();
            instance.Activate();
        }

        // Dev hook: NOTCHIRA_DEMO=musicwin
        public static void Demo(string dir)
        {
            ShowWindow();
            var t = new System.Windows.Forms.Timer { Interval = 1200 };
            t.Tick += delegate
            {
                t.Stop();
                using (var bmp = new Bitmap(instance.Width, instance.Height))
                {
                    instance.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                    bmp.Save(System.IO.Path.Combine(dir, "musicwin.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                Application.Exit();
            };
            t.Start();
        }

        MusicWindow()
        {
            ui = SynchronizationContext.Current;
            Text = "Musik YouTube - Notchira";
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(700, 500);
            MinimumSize = new Size(600, 420);
            StartPosition = FormStartPosition.CenterScreen;

            var lblUrl = new Label { Text = "Tautan YouTube", Location = new Point(12, 15), AutoSize = true };
            url.SetBounds(104, 11, 400, 24);
            url.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            btnDownload.Text = "Unduh audio";
            btnDownload.SetBounds(512, 10, 100, 26);
            btnDownload.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnDownload.Click += delegate { StartDownload(); };
            btnCancel.Text = "Batal";
            btnCancel.SetBounds(618, 10, 70, 26);
            btnCancel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnCancel.Enabled = false;
            btnCancel.Click += delegate { YtDlp.Cancel(); };

            bar.SetBounds(12, 44, 676, 6);
            bar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            status.SetBounds(12, 54, 676, 18);
            status.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            status.ForeColor = SystemColors.GrayText;

            tool.SetBounds(12, 76, 520, 18);
            btnTool.SetBounds(538, 72, 150, 24);
            btnTool.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnTool.Click += delegate { ToolAction(); };

            lists.DropDownStyle = ComboBoxStyle.DropDownList;
            lists.SetBounds(12, 106, 220, 24);
            lists.SelectedIndexChanged += delegate { Reload(); };
            var btnNew = Btn("Daftar baru", 240, 105, 90);
            btnNew.Click += delegate { NewList(); };
            var btnDelList = Btn("Hapus daftar", 336, 105, 90);
            btnDelList.Click += delegate { DeleteList(); };

            view.View = View.Details;
            view.FullRowSelect = true;
            view.MultiSelect = false;
            view.HideSelection = false;
            view.Columns.Add("Judul", 380);
            view.Columns.Add("Artis", 190);
            view.Columns.Add("Durasi", 70);
            view.SetBounds(12, 138, 676, 306);
            view.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            view.DoubleClick += delegate { PlaySelected(); };

            var btnPlay = Btn("Putar", 12, 458, 70);
            btnPlay.Click += delegate { PlaySelected(); };
            var btnAdd = Btn("Tambah ke daftar", 88, 458, 120);
            btnAdd.Click += delegate { AddMenu(btnAdd); };
            var btnRemove = Btn("Hapus", 214, 458, 70);
            btnRemove.Click += delegate { RemoveSelected(); };
            var btnLyrics = Btn("Cari teks lagu", 290, 458, 100);
            btnLyrics.Click += delegate { FindLyrics(btnLyrics); };
            var btnKeys = Btn("Kunci API...", 396, 458, 80);
            btnKeys.Click += delegate { KeysDialog(); };
            btnLyrics.Anchor = btnKeys.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            btnShuffle.SetBounds(480, 458, 100, 26);
            btnShuffle.Click += delegate { LocalPlayer.I.SetShuffle(!LocalPlayer.I.Shuffle); UpdateModes(); };
            btnRepeat.SetBounds(586, 458, 102, 26);
            btnRepeat.Click += delegate { LocalPlayer.I.CycleRepeat(); UpdateModes(); };
            btnPlay.Anchor = btnAdd.Anchor = btnRemove.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            btnShuffle.Anchor = btnRepeat.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;

            Controls.AddRange(new Control[] { lblUrl, url, btnDownload, btnCancel, bar, status, tool, btnTool, lists, btnNew,
                btnDelList, view, btnPlay, btnAdd, btnRemove, btnLyrics, btnKeys, btnShuffle, btnRepeat });

            RefreshLists();
            UpdateTool();
            UpdateModes();
            Reload();
            FormClosed += delegate { Memory.Trim(); };
        }

        static Button Btn(string text, int x, int y, int w)
        {
            var b = new Button { Text = text };
            b.SetBounds(x, y, w, 26);
            return b;
        }

        static string Fmt(int secs) { return (secs / 60) + ":" + (secs % 60).ToString("00"); }

        void UpdateModes()
        {
            btnShuffle.Text = "Acak: " + (LocalPlayer.I.Shuffle ? "nyala" : "mati");
            btnRepeat.Text = "Ulang: " + (LocalPlayer.I.Repeat == 0 ? "mati" : LocalPlayer.I.Repeat == 1 ? "semua" : "satu");
        }

        // ---- yt-dlp -----------------------------------------------------------------------------------------

        void UpdateTool()
        {
            if (YtDlp.Installed)
            {
                tool.Text = "yt-dlp terpasang.";
                btnTool.Text = "Perbarui yt-dlp";
            }
            else
            {
                tool.Text = "yt-dlp belum terpasang. Dibutuhkan untuk mengunduh (sekitar 25 MB dari GitHub, diperiksa SHA-256).";
                btnTool.Text = "Pasang yt-dlp";
            }
            btnDownload.Enabled = YtDlp.Installed && !busy;
        }

        void ToolAction()
        {
            btnTool.Enabled = false;
            if (!YtDlp.Installed)
                YtDlp.Install(m => ui.Post(_ => status.Text = m, null), (ok, m) => ui.Post(_ =>
                {
                    status.Text = m;
                    btnTool.Enabled = true;
                    UpdateTool();
                }, null));
            else
            {
                status.Text = "Memperbarui yt-dlp...";
                YtDlp.SelfUpdate((ok, m) => ui.Post(_ =>
                {
                    status.Text = m;
                    btnTool.Enabled = true;
                    UpdateTool();
                }, null));
            }
        }

        void StartDownload()
        {
            string clean;
            if (!YtDlp.TryParseUrl(url.Text, out clean)) { status.Text = "Tempel tautan YouTube yang valid."; return; }
            busy = true;
            bar.Value = 0;
            btnDownload.Enabled = false;
            btnCancel.Enabled = true;
            status.Text = "Mengunduh...";
            YtDlp.Download(clean,
                p => ui.Post(_ => { if (!IsDisposed) { bar.Value = p; status.Text = "Mengunduh... " + p + "%"; } }, null),
                (song, err) => ui.Post(_ =>
                {
                    busy = false;
                    if (IsDisposed) return;
                    btnCancel.Enabled = false;
                    bar.Value = song != null ? 100 : 0;
                    if (song != null)
                    {
                        Library.Add(song);
                        status.Text = "Selesai: " + song.Title;
                        url.Text = "";
                        Reload();
                    }
                    else status.Text = "Gagal: " + err;
                    UpdateTool();
                }, null));
        }

        // ---- list -------------------------------------------------------------------------------------------

        void RefreshLists()
        {
            string keep = lists.SelectedItem as string;
            lists.Items.Clear();
            lists.Items.Add(AllSongs);
            foreach (string n in Library.PlaylistNames()) lists.Items.Add(n);
            lists.SelectedItem = keep != null && lists.Items.Contains(keep) ? keep : AllSongs;
        }

        string CurrentList { get { var s = lists.SelectedItem as string; return s == null || s == AllSongs ? null : s; } }

        void Reload()
        {
            string l = CurrentList;
            shown = l == null ? Library.All() : Library.Playlist(l);
            view.BeginUpdate();
            view.Items.Clear();
            foreach (Song s in shown)
                view.Items.Add(new ListViewItem(new[] { s.Title, s.Artist, Fmt(s.Seconds) }));
            view.EndUpdate();
        }

        Song Selected()
        {
            return view.SelectedIndices.Count == 0 ? null : shown[view.SelectedIndices[0]];
        }

        void PlaySelected()
        {
            Song s = Selected();
            if (s == null && shown.Count > 0) s = shown[0];
            if (s == null) return;
            LocalPlayer.I.PlayList(shown, shown.IndexOf(s));
        }

        void RemoveSelected()
        {
            Song s = Selected();
            if (s == null) return;
            string l = CurrentList;
            if (l != null)
            {
                Library.RemoveFromPlaylist(l, s.Id);
                Reload();
                return;
            }
            if (MessageBox.Show("Hapus \"" + s.Title + "\" dari perpustakaan beserta filenya?", "Notchira",
                MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            if (LocalPlayer.I.Current != null && LocalPlayer.I.Current.Id == s.Id) LocalPlayer.I.Stop();
            Library.Remove(s.Id);
            Reload();
        }

        // Looks everywhere again (LRCLIB, captions, and a transcription when a key is stored) and tells the island.
        void FindLyrics(Button b)
        {
            Song s = Selected();
            if (s == null) { status.Text = "Pilih lagu dulu."; return; }
            b.Enabled = false;
            status.Text = "Mencari teks untuk " + s.Title + "...";
            SongLyrics.Resolve(s, true, (l, src, fresh) => ui.Post(_ =>
            {
                if (IsDisposed) return;
                b.Enabled = true;
                status.Text = l != null ? "Teks ditemukan: " + src : "Teks tidak ditemukan." + (Transcribe.Available ? "" : " Tambahkan kunci Groq atau Deepgram untuk membuat transkrip.");
                SongLyrics.Raise(s.Id);
            }, null));
        }

        void KeysDialog()
        {
            using (var f = new Form())
            {
                f.Text = "Kunci API transkripsi";
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = f.MaximizeBox = false;
                f.ClientSize = new Size(470, 330);
                f.Font = Font;

                var info = new Label
                {
                    Text = "Dipakai bila lagu tidak punya teks di LRCLIB atau caption YouTube. Audio lagu dikirim ke layanan yang kuncinya diisi. Kunci disimpan terenkripsi di akun Windows ini.",
                    Location = new Point(12, 10), Size = new Size(446, 44)
                };
                var g1 = new Label { Text = "Groq: tersimpan " + Secrets.Get("groq").Count + " kunci. Ganti (satu per baris, kosong = tidak diubah)", Location = new Point(12, 60), AutoSize = true };
                var t1 = new TextBox { Location = new Point(12, 80), Size = new Size(446, 56), Multiline = true, ScrollBars = ScrollBars.Vertical };
                var c1 = new CheckBox { Text = "Hapus semua kunci Groq", Location = new Point(12, 140), AutoSize = true };
                var g2 = new Label { Text = "Deepgram: tersimpan " + Secrets.Get("deepgram").Count + " kunci. Ganti (satu per baris, kosong = tidak diubah)", Location = new Point(12, 168), AutoSize = true };
                var t2 = new TextBox { Location = new Point(12, 188), Size = new Size(446, 56), Multiline = true, ScrollBars = ScrollBars.Vertical };
                var c2 = new CheckBox { Text = "Hapus semua kunci Deepgram", Location = new Point(12, 248), AutoSize = true };
                var auto = new CheckBox
                {
                    Text = "Buat teks otomatis saat lagu diputar bila tidak ada sumber lain",
                    Location = new Point(12, 274), AutoSize = true, Checked = Settings.Get("transcribe.auto", "1") == "1"
                };
                var ok = new Button { Text = "Simpan", DialogResult = DialogResult.OK, Location = new Point(302, 296), Size = new Size(75, 26) };
                var cancel = new Button { Text = "Batal", DialogResult = DialogResult.Cancel, Location = new Point(383, 296), Size = new Size(75, 26) };
                f.Controls.AddRange(new Control[] { info, g1, t1, c1, g2, t2, c2, auto, ok, cancel });
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                if (f.ShowDialog(this) != DialogResult.OK) return;

                Action<string, TextBox, CheckBox> apply = (provider, box, del) =>
                {
                    var lines = box.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (del.Checked) Secrets.Set(provider, new string[0]);
                    else if (lines.Length > 0) Secrets.Set(provider, lines);
                };
                apply("groq", t1, c1);
                apply("deepgram", t2, c2);
                Settings.Set("transcribe.auto", auto.Checked ? "1" : "0");
                status.Text = "Kunci disimpan.";
            }
        }

        void NewList()
        {
            string name = Prompt("Nama daftar putar baru");
            if (string.IsNullOrWhiteSpace(name)) return;
            if (!Library.CreatePlaylist(name)) { status.Text = "Nama daftar kosong atau sudah ada."; return; }
            RefreshLists();
            lists.SelectedItem = name.Trim();
        }

        void DeleteList()
        {
            string l = CurrentList;
            if (l == null) return;
            if (MessageBox.Show("Hapus daftar \"" + l + "\"? Lagunya tetap ada di perpustakaan.", "Notchira",
                MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            Library.DeletePlaylist(l);
            RefreshLists();
        }

        void AddMenu(Control anchor)
        {
            Song s = Selected();
            if (s == null) return;
            var m = new ContextMenuStrip();
            foreach (string n in Library.PlaylistNames())
            {
                string name = n;
                m.Items.Add(name, null, delegate { Library.AddToPlaylist(name, s.Id); status.Text = "Ditambahkan ke " + name; });
            }
            if (m.Items.Count == 0) m.Items.Add("(belum ada daftar putar)").Enabled = false;
            m.Show(anchor, new Point(0, anchor.Height));
        }

        static string Prompt(string title)
        {
            using (var f = new Form())
            {
                f.Text = title;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = f.MaximizeBox = false;
                f.ClientSize = new Size(320, 80);
                var tb = new TextBox { Location = new Point(12, 12), Width = 296 };
                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(152, 44) };
                var cancel = new Button { Text = "Batal", DialogResult = DialogResult.Cancel, Location = new Point(233, 44) };
                f.Controls.AddRange(new Control[] { tb, ok, cancel });
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                return f.ShowDialog() == DialogResult.OK ? tb.Text : null;
            }
        }
    }
}
