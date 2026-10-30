using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Notchira
{
    // A real text box floated over a field the island draws, so typing, IME and the clipboard work as usual.
    // The island stays a no-activate layered window; only this small box takes focus when clicked.
    sealed class IslandInput : Form
    {
        public static readonly Color Fill = Color.FromArgb(54, 56, 68);

        public readonly TextBox Box = new TextBox();
        public event Action Submit, Cancel;
        float fontPx;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wp, string lp);

        public IslandInput()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Fill;
            Box.BorderStyle = BorderStyle.None;
            Box.BackColor = Fill;
            Box.ForeColor = Color.FromArgb(242, 242, 246);
            Box.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; if (Submit != null) Submit(); }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; if (Cancel != null) Cancel(); }
            };
            Controls.Add(Box);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
        }

        // Grey hint text shown while the box is empty, also while it has focus.
        public void SetCue(string text)
        {
            SendMessage(Box.Handle, 0x1501, (IntPtr)1, text);
        }

        // Places the box over a screen rectangle (physical pixels), text centered vertically.
        public void Place(Rectangle r, float scale)
        {
            float px = 13f * scale;
            if (Math.Abs(px - fontPx) > 0.1f)
            {
                fontPx = px;
                var old = Box.Font;
                Box.Font = new Font("Segoe UI", px, FontStyle.Regular, GraphicsUnit.Pixel);
                if (old != null && old != Control.DefaultFont) old.Dispose();
            }
            if (Bounds != r) Bounds = r;
            int h = Box.PreferredHeight;
            Box.SetBounds(0, Math.Max(0, (r.Height - h) / 2), r.Width, h);
        }

        public void FocusBox()
        {
            Activate();
            Box.Focus();
            Box.SelectionStart = Box.TextLength;
        }
    }
}
