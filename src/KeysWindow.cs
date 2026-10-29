using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Notchira
{
    // Key ring manager: several API keys per provider, which one is active, and whether to switch on its own.
    // Key values are never shown, only the label and the last four characters.
    sealed class KeysWindow : Form
    {
        static KeysWindow instance;

        sealed class Item { public string Id, Name; public override string ToString() { return Name; } }

        readonly ComboBox providers = new ComboBox();
        readonly ListView view = new ListView();
        readonly CheckBox auto = new CheckBox();
        readonly TextBox model = new TextBox();
        readonly TextBox baseUrl = new TextBox();
        readonly Label modelLbl = new Label();
        readonly Label baseLbl = new Label();
        readonly Label note = new Label();
        readonly CheckBox transcribe = new CheckBox();
        List<KeyInfo> shown = new List<KeyInfo>();
        bool loading;

        public static void ShowWindow(string provider)
        {
            if (instance == null || instance.IsDisposed) instance = new KeysWindow();
            instance.Select(provider);
            instance.Show();
            instance.Activate();
        }

        KeysWindow()
        {
            Text = "Kunci API - Notchira";
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(600, 420);
            MinimumSize = new Size(560, 400);
            StartPosition = FormStartPosition.CenterScreen;

            var lbl = new Label { Text = "Penyedia", Location = new Point(12, 16), AutoSize = true };
            providers.DropDownStyle = ComboBoxStyle.DropDownList;
            providers.SetBounds(80, 12, 280, 24);
            foreach (var p in Ai.Providers) providers.Items.Add(new Item { Id = p.Id, Name = p.Name });
            providers.Items.Add(new Item { Id = "deepgram", Name = "Deepgram (transkripsi lagu)" });
            providers.SelectedIndexChanged += delegate { Reload(); };

            view.View = View.Details;
            view.FullRowSelect = true;
            view.MultiSelect = false;
            view.HideSelection = false;
            view.Columns.Add("Nama", 220);
            view.Columns.Add("Akhiran", 80);
            view.Columns.Add("Status", 250);
            view.SetBounds(12, 46, 576, 180);
            view.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            var add = Btn("Tambah...", 12, 234, 80);
            add.Click += delegate { AddKey(); };
            var remove = Btn("Hapus", 98, 234, 70);
            remove.Click += delegate { RemoveKey(); };
            var activate = Btn("Jadikan aktif", 174, 234, 100);
            activate.Click += delegate { Act(Secrets.Select); };
            var reset = Btn("Pulihkan batas", 280, 234, 100);
            reset.Click += delegate { Act(Secrets.ResetLimit); };

            auto.Text = "Ganti otomatis ke kunci berikutnya bila kena batas atau ditolak";
            auto.SetBounds(12, 268, 560, 22);
            auto.CheckedChanged += delegate
            {
                if (!loading) Secrets.SetAuto(Current, auto.Checked);
            };

            modelLbl.Text = "Model";
            modelLbl.SetBounds(12, 302, 60, 18);
            model.SetBounds(80, 298, 300, 24);
            model.Leave += delegate { SaveSetting("ai.model.", model, p => p.DefaultModel); };
            baseLbl.Text = "Alamat API";
            baseLbl.SetBounds(12, 332, 66, 18);
            baseUrl.SetBounds(80, 328, 400, 24);
            baseUrl.Leave += delegate { SaveSetting("ai.base.", baseUrl, p => p.DefaultBase); };

            transcribe.Text = "Buat teks lagu otomatis lewat layanan ini (audio lagu dikirim ke sana)";
            transcribe.SetBounds(12, 300, 570, 22);
            transcribe.Checked = Settings.Get("transcribe.auto", "1") == "1";
            transcribe.CheckedChanged += delegate { if (!loading) Settings.Set("transcribe.auto", transcribe.Checked ? "1" : "0"); };

            note.Text = "Kunci disimpan terenkripsi di akun Windows ini dan tidak pernah ditampilkan utuh.";
            note.SetBounds(12, 384, 576, 32);
            note.ForeColor = SystemColors.GrayText;
            note.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            Controls.AddRange(new Control[] { lbl, providers, view, add, remove, activate, reset, auto, modelLbl, model, baseLbl, baseUrl, transcribe, note });
            providers.SelectedIndex = 0;
            FormClosed += delegate { Memory.Trim(); };
        }

        static Button Btn(string text, int x, int y, int w)
        {
            var b = new Button { Text = text };
            b.SetBounds(x, y, w, 26);
            return b;
        }

        string Current { get { var i = providers.SelectedItem as Item; return i == null ? "groq" : i.Id; } }

        void Select(string provider)
        {
            for (int i = 0; i < providers.Items.Count; i++)
                if (((Item)providers.Items[i]).Id == provider) { providers.SelectedIndex = i; return; }
        }

        void Reload()
        {
            loading = true;
            shown = Secrets.List(Current);
            view.Items.Clear();
            foreach (KeyInfo k in shown)
            {
                string status = k.Active ? "Aktif" : "";
                if (k.Limited) status += (status.Length > 0 ? ", " : "") + "istirahat sampai " + k.LimitedUntil.ToString("HH:mm");
                view.Items.Add(new ListViewItem(new[] { k.Label, k.Last4.Length > 0 ? "..." + k.Last4 : "", status }));
            }
            auto.Checked = Secrets.GetAuto(Current);
            var p = Ai.Providers.FirstOrDefault(x => x.Id == Current);
            bool ai = p != null;
            modelLbl.Visible = model.Visible = baseLbl.Visible = baseUrl.Visible = ai;
            transcribe.Visible = Current == "groq" || Current == "deepgram";
            transcribe.Top = ai ? 358 : 300;
            if (ai)
            {
                string m = Settings.Get("ai.model." + p.Id, ""), b = Settings.Get("ai.base." + p.Id, "");
                model.Text = m.Length > 0 ? m : p.DefaultModel;
                baseUrl.Text = b.Length > 0 ? b : p.DefaultBase;
            }
            loading = false;
        }

        // Stores an override only when it differs from the default, so defaults can change with new versions.
        void SaveSetting(string prefix, TextBox box, Func<AiProvider, string> def)
        {
            var p = Ai.Providers.FirstOrDefault(x => x.Id == Current);
            if (p == null || loading) return;
            string v = box.Text.Trim();
            Settings.Set(prefix + p.Id, v == def(p) ? "" : v);
        }

        KeyInfo Selected() { return view.SelectedIndices.Count == 0 ? null : shown[view.SelectedIndices[0]]; }

        void Act(Action<string, string> op)
        {
            KeyInfo k = Selected();
            if (k == null) return;
            op(Current, k.Id);
            Reload();
        }

        void RemoveKey()
        {
            KeyInfo k = Selected();
            if (k == null) return;
            if (MessageBox.Show("Hapus kunci \"" + k.Label + "\"?", "Notchira", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            Secrets.Remove(Current, k.Id);
            Reload();
        }

        void AddKey()
        {
            using (var f = new Form())
            {
                f.Text = "Tambah kunci";
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = f.MaximizeBox = false;
                f.ClientSize = new Size(380, 130);
                f.Font = Font;
                var l1 = new Label { Text = "Nama (opsional)", Location = new Point(12, 14), AutoSize = true };
                var name = new TextBox { Location = new Point(110, 10), Width = 258 };
                var l2 = new Label { Text = "Kunci API", Location = new Point(12, 46), AutoSize = true };
                var value = new TextBox { Location = new Point(110, 42), Width = 258, UseSystemPasswordChar = true };
                var ok = new Button { Text = "Tambah", DialogResult = DialogResult.OK, Location = new Point(212, 88), Size = new Size(75, 26) };
                var cancel = new Button { Text = "Batal", DialogResult = DialogResult.Cancel, Location = new Point(293, 88), Size = new Size(75, 26) };
                f.Controls.AddRange(new Control[] { l1, name, l2, value, ok, cancel });
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                if (f.ShowDialog(this) != DialogResult.OK) return;
                try { Secrets.Add(Current, name.Text, value.Text); }
                catch (ArgumentException ex) { MessageBox.Show(ex.Message, "Notchira"); }
                Reload();
            }
        }
    }
}
