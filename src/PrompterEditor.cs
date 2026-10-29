using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace Notchira
{
    // Where the script is pasted, saved and tuned before and during a reading.
    sealed class PrompterEditor : Form
    {
        readonly ComboBox saved = new ComboBox();
        readonly TextBox box = new TextBox();
        readonly NumericUpDown wpm = new NumericUpDown();
        readonly NumericUpDown size = new NumericUpDown();
        readonly TrackBar bg = new TrackBar();
        readonly CheckBox mirror = new CheckBox();
        readonly Label info = new Label();
        readonly SynchronizationContext ui = SynchronizationContext.Current;
        string beforeAi;
        const string TidyPrompt =
            "Kamu editor naskah untuk video. Rapikan naskah dari pengguna supaya enak dibacakan dengan lantang: kalimat pendek dan jelas, " +
            "buang pengulangan, pertahankan makna, bahasa, dan nada aslinya, jangan menambah fakta baru. Sisipkan penanda [jeda] " +
            "di tempat napas yang alami (paling banyak satu per paragraf). Balas hanya dengan naskahnya, tanpa komentar atau tanda kutip.";
        bool loading;

        public PrompterEditor()
        {
            Text = "Teleprompter - Notchira";
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(640, 520);
            MinimumSize = new Size(560, 460);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;

            var lblSaved = new Label { Text = "Naskah tersimpan", Location = new Point(12, 14), AutoSize = true };
            saved.DropDownStyle = ComboBoxStyle.DropDownList;
            saved.SetBounds(120, 10, 280, 24);
            saved.SelectedIndexChanged += delegate { LoadSelected(); };
            var btnImport = Btn("Impor...", 410, 9, 70);
            btnImport.Click += delegate { Import(); };
            var btnSave = Btn("Simpan", 486, 9, 66);
            btnSave.Click += delegate { Save(); };
            var btnDelete = Btn("Hapus", 558, 9, 70);
            btnDelete.Click += delegate { Delete(); };

            box.Multiline = true;
            box.AcceptsReturn = true;
            box.ScrollBars = ScrollBars.Vertical;
            box.Font = new Font("Segoe UI", 11f);
            box.SetBounds(12, 44, 616, 330);
            box.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            box.TextChanged += delegate { UpdateInfo(); };

            info.SetBounds(12, 380, 616, 18);
            info.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            info.ForeColor = SystemColors.GrayText;

            var l1 = Lbl("Kecepatan (kata per menit)", 12, 410);
            Setup(wpm, 60, 400, 140, 190, 406, 60);
            var l2 = Lbl("Ukuran huruf", 270, 410);
            Setup(size, 14, 48, 19, 350, 406, 55);
            var l3 = Lbl("Pekat latar", 420, 410);
            bg.SetBounds(495, 402, 133, 30);
            bg.Minimum = 15; bg.Maximum = 100; bg.TickFrequency = 17; bg.Value = 55;
            bg.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            mirror.Text = "Cermin (teleprompter kaca)";
            mirror.SetBounds(12, 440, 250, 22);
            mirror.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;

            var hint = new Label
            {
                Text = "Tulis [jeda] di naskah untuk berhenti otomatis di titik itu. Pintasan saat berjalan: Ctrl+Alt+Spasi jeda, R ulang, ← → mundur/maju 5 dtk, + - kecepatan, P editor, X hentikan.",
                Location = new Point(12, 468),
                Size = new Size(500, 40),
                ForeColor = SystemColors.GrayText,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            var tidy = Btn("Rapikan dengan AI", 270, 438, 130);
            var undo = Btn("Urungkan AI", 406, 438, 90);
            undo.Enabled = false;
            tidy.Anchor = undo.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            tidy.Click += delegate { Tidy(tidy, undo); };
            undo.Click += delegate
            {
                if (beforeAi == null) return;
                box.Text = beforeAi;
                beforeAi = null;
                undo.Enabled = false;
            };
            var start = Btn("Mulai (Ctrl+Enter)", 520, 472, 108);
            start.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            start.Click += delegate { StartReading(); };

            Controls.AddRange(new Control[] { lblSaved, saved, btnImport, btnSave, btnDelete, box, info, l1, wpm, l2, size, l3, bg, mirror, tidy, undo, hint, start });

            LoadSettings();
            RefreshList();
            wpm.ValueChanged += delegate { Push(); };
            size.ValueChanged += delegate { Push(); };
            bg.ValueChanged += delegate { Push(); };
            mirror.CheckedChanged += delegate { Push(); };
            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                PersistLast();
                if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
            };
            VisibleChanged += delegate { if (Visible) LoadSettings(); };
        }

        static Button Btn(string text, int x, int y, int w)
        {
            var b = new Button { Text = text };
            b.SetBounds(x, y, w, 26);
            return b;
        }

        static Label Lbl(string text, int x, int y) { return new Label { Text = text, Location = new Point(x, y), AutoSize = true }; }

        static void Setup(NumericUpDown n, int min, int max, int val, int x, int y, int w)
        {
            n.Minimum = min; n.Maximum = max; n.Value = val;
            n.SetBounds(x, y, w, 24);
            n.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        }

        void LoadSettings()
        {
            loading = true;
            wpm.Value = Math.Max(wpm.Minimum, Math.Min(wpm.Maximum, Num("prompter.wpm", 140)));
            size.Value = Math.Max(size.Minimum, Math.Min(size.Maximum, Num("prompter.size", 19)));
            bg.Value = Math.Max(bg.Minimum, Math.Min(bg.Maximum, Num("prompter.bg", 55)));
            mirror.Checked = Settings.Get("prompter.mirror", "0") == "1";
            if (box.Text.Length == 0)
            {
                try { if (File.Exists(Teleprompter.LastFile)) box.Text = File.ReadAllText(Teleprompter.LastFile); } catch { }
            }
            loading = false;
            UpdateInfo();
        }

        static int Num(string key, int def)
        {
            int v;
            return int.TryParse(Settings.Get(key, def.ToString()), out v) ? v : def;
        }

        void Push()
        {
            if (loading) return;
            Settings.Set("prompter.wpm", wpm.Value.ToString());
            Settings.Set("prompter.size", size.Value.ToString());
            Settings.Set("prompter.bg", bg.Value.ToString());
            Settings.Set("prompter.mirror", mirror.Checked ? "1" : "0");
            Teleprompter.Reconfigure();
            UpdateInfo();
        }

        void UpdateInfo()
        {
            int words = Teleprompter.CountWords(box.Text);
            double min = words / (double)Math.Max(1, (int)wpm.Value);
            int secs = (int)Math.Round(min * 60);
            info.Text = words + " kata  ·  perkiraan " + (secs / 60) + ":" + (secs % 60).ToString("00") + " pada " + (int)wpm.Value + " wpm";
        }

        void PersistLast()
        {
            try
            {
                Directory.CreateDirectory(Settings.Dir);
                File.WriteAllText(Teleprompter.LastFile, box.Text);
            }
            catch { }
        }

        // Sends the script to the assistant for a spoken-style rewrite; the old text can be restored once.
        void Tidy(Button b, Button undo)
        {
            string text = box.Text;
            if (text.Trim().Length == 0) return;
            b.Enabled = false;
            info.Text = "Meminta AI merapikan naskah...";
            string provider = Settings.Get("ai.provider", "groq");
            ThreadPool.QueueUserWorkItem(delegate
            {
                AiResult r = null;
                string error = null;
                try { r = Ai.Once(TidyPrompt, text); }
                catch (Exception ex) { error = ex.Message; }
                ui.Post(_ =>
                {
                    if (IsDisposed) return;
                    b.Enabled = true;
                    if (r != null)
                    {
                        beforeAi = box.Text;
                        box.Text = r.Text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
                        undo.Enabled = true;
                        info.Text = "Dirapikan oleh " + r.Provider + ". " + info.Text;
                    }
                    else info.Text = "Gagal: " + error;
                }, null);
            });
        }

        void StartReading()
        {
            PersistLast();
            Teleprompter.Start(box.Text);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.Enter)) { StartReading(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ---- saved scripts ----------------------------------------------------------------------------------

        void RefreshList()
        {
            string keep = saved.SelectedItem as string;
            saved.Items.Clear();
            try
            {
                if (Directory.Exists(Teleprompter.ScriptDir))
                    foreach (string f in Directory.GetFiles(Teleprompter.ScriptDir, "*.txt").OrderBy(x => x))
                        saved.Items.Add(Path.GetFileNameWithoutExtension(f));
            }
            catch { }
            if (keep != null && saved.Items.Contains(keep)) saved.SelectedItem = keep;
        }

        string PathFor(string name) { return Path.Combine(Teleprompter.ScriptDir, name + ".txt"); }

        void LoadSelected()
        {
            string n = saved.SelectedItem as string;
            if (n == null) return;
            try { box.Text = File.ReadAllText(PathFor(n)); } catch { }
        }

        static string SafeName(string s)
        {
            s = s.Trim();
            int nl = s.IndexOfAny(new[] { '\r', '\n' });
            if (nl >= 0) s = s.Substring(0, nl);
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            s = s.Trim().TrimEnd('.');
            if (s.Length > 40) s = s.Substring(0, 40).Trim();
            return s.Length == 0 ? "Naskah" : s;
        }

        // Saves under the first line's name, or overwrites the script that is currently selected.
        void Save()
        {
            try
            {
                Directory.CreateDirectory(Teleprompter.ScriptDir);
                string name = saved.SelectedItem as string ?? SafeName(box.Text);
                File.WriteAllText(PathFor(name), box.Text);
                RefreshList();
                saved.SelectedItem = name;
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Notchira"); }
        }

        void Delete()
        {
            string n = saved.SelectedItem as string;
            if (n == null) return;
            if (MessageBox.Show("Hapus naskah \"" + n + "\"?", "Notchira", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            try { File.Delete(PathFor(n)); } catch { }
            saved.SelectedIndex = -1;
            RefreshList();
        }

        void Import()
        {
            using (var d = new OpenFileDialog { Filter = "Teks (*.txt;*.md)|*.txt;*.md|Semua berkas|*.*" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    string s = File.ReadAllText(d.FileName);
                    if (d.FileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) s = Teleprompter.StripMarkdown(s);
                    box.Text = s.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
                    saved.SelectedIndex = -1;
                }
                catch (Exception ex) { MessageBox.Show(ex.Message, "Notchira"); }
            }
        }
    }
}
