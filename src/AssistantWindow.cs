using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Notchira
{
    // Ask-anything window (Ctrl+Alt+A). The conversation lives in memory only and is cleared with "Percakapan baru";
    // the window itself is created on demand and disposed on close.
    sealed class AssistantWindow : Form
    {
        static AssistantWindow instance;
        static readonly List<AiMessage> history = new List<AiMessage>();

        sealed class Item { public string Id, Name; public override string ToString() { return Name; } }

        readonly ComboBox providers = new ComboBox();
        readonly CheckBox fallback = new CheckBox();
        readonly TextBox output = new TextBox();
        readonly TextBox input = new TextBox();
        readonly Button send = new Button();
        readonly Label status = new Label();
        readonly SynchronizationContext ui;
        bool busy;
        string pending;

        public static void ShowWindow()
        {
            if (instance == null || instance.IsDisposed) instance = new AssistantWindow();
            instance.Show();
            instance.Activate();
            instance.input.Focus();
        }

        AssistantWindow()
        {
            ui = SynchronizationContext.Current;
            Text = "Asisten AI - Notchira";
            Font = new Font("Segoe UI", 9.5f);
            ClientSize = new Size(560, 560);
            MinimumSize = new Size(440, 420);
            StartPosition = FormStartPosition.CenterScreen;

            var lbl = new Label { Text = "Penyedia", Location = new Point(12, 15), AutoSize = true };
            providers.DropDownStyle = ComboBoxStyle.DropDownList;
            providers.SetBounds(72, 11, 190, 24);
            foreach (var p in Ai.Providers) providers.Items.Add(new Item { Id = p.Id, Name = p.Name });
            string cur = Settings.Get("ai.provider", "groq");
            providers.SelectedIndex = Math.Max(0, Array.FindIndex(Ai.Providers, p => p.Id == cur));
            providers.SelectedIndexChanged += delegate { Settings.Set("ai.provider", ((Item)providers.SelectedItem).Id); ShowModel(); };

            var keys = new Button { Text = "Kunci...", Location = new Point(270, 10), Size = new Size(70, 26) };
            keys.Click += delegate { KeysWindow.ShowWindow(((Item)providers.SelectedItem).Id); };
            var fresh = new Button { Text = "Percakapan baru", Location = new Point(346, 10), Size = new Size(130, 26) };
            fresh.Click += delegate { history.Clear(); Render(); status.Text = ""; };
            fallback.Text = "Pindah ke penyedia lain bila semua kunci kena batas";
            fallback.SetBounds(12, 42, 440, 22);
            fallback.Checked = Settings.Get("ai.fallback", "1") == "1";
            fallback.CheckedChanged += delegate { Settings.Set("ai.fallback", fallback.Checked ? "1" : "0"); };

            output.Multiline = true;
            output.ReadOnly = true;
            output.ScrollBars = ScrollBars.Vertical;
            output.BackColor = SystemColors.Window;
            output.SetBounds(12, 70, 536, 360);
            output.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;

            input.Multiline = true;
            input.AcceptsReturn = true;
            input.ScrollBars = ScrollBars.Vertical;
            input.SetBounds(12, 438, 456, 70);
            input.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            input.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; Send(); }
            };
            send.Text = "Kirim";
            send.SetBounds(474, 438, 74, 30);
            send.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            send.Click += delegate { Send(); };

            status.SetBounds(12, 516, 536, 34);
            status.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            status.ForeColor = SystemColors.GrayText;

            Controls.AddRange(new Control[] { lbl, providers, keys, fresh, fallback, output, input, send, status });
            Action<string, string, string> onSwitch = (prov, from, to) => ui.Post(_ =>
            {
                if (!IsDisposed) status.Text = "Kunci " + prov + " \"" + from + "\" kena batas" + (to != null ? ", pindah ke \"" + to + "\"." : ".");
            }, null);
            Secrets.Switched += onSwitch;
            FormClosed += delegate { Secrets.Switched -= onSwitch; Memory.Trim(); };
            Render();
            ShowModel();
        }

        void ShowModel()
        {
            var p = Ai.Find(((Item)providers.SelectedItem).Id);
            status.Text = "Model: " + Ai.Model(p) + (Secrets.Has(p.Id) || p.KeyOptional ? "" : "  (belum ada kunci: klik Kunci...)");
        }

        void Render()
        {
            var sb = new StringBuilder();
            foreach (var m in history)
                sb.Append(m.Role == "user" ? "Kamu:\r\n" : "Asisten:\r\n").Append(m.Text.Replace("\r\n", "\n").Replace("\n", "\r\n")).Append("\r\n\r\n");
            if (busy) sb.Append("Asisten sedang berpikir...");
            output.Text = sb.ToString();
            output.SelectionStart = output.TextLength;
            output.ScrollToCaret();
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

        void Send()
        {
            string text = input.Text.Trim();
            if (busy || text.Length == 0) return;
            busy = true;
            send.Enabled = false;
            pending = text;
            history.Add(new AiMessage("user", text));
            input.Text = "";
            status.Text = "";
            Render();
            if (Island.Current != null) Island.Current.SetThinking(true);

            string provider = ((Item)providers.SelectedItem).Id;
            bool fb = fallback.Checked;
            var snapshot = Recent();
            ThreadPool.QueueUserWorkItem(delegate
            {
                AiResult result = null;
                string error = null;
                try { result = Ai.Ask(snapshot, Ai.System, provider, fb); }
                catch (Exception ex) { error = ex.Message; }
                ui.Post(_ => Done(result, error), null);
            });
        }

        void Done(AiResult r, string error)
        {
            busy = false;
            if (Island.Current != null) Island.Current.SetThinking(false);
            if (IsDisposed) { if (r != null) history.Add(new AiMessage("assistant", r.Text)); return; }
            send.Enabled = true;
            if (r != null)
            {
                history.Add(new AiMessage("assistant", r.Text));
                status.Text = "Dijawab oleh " + r.Provider + (r.KeyLabel.Length > 0 ? " (" + r.KeyLabel + ")" : "") + ".";
            }
            else
            {
                // take the question back, so the next try starts from a clean turn
                if (history.Count > 0 && history[history.Count - 1].Role == "user") history.RemoveAt(history.Count - 1);
                input.Text = pending;
                status.Text = "Gagal: " + error;
            }
            Render();
            input.Focus();
        }
    }
}
