using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Notchira
{
    // Panels that need real controls (the script editor, the key ring) hang from the top edge in the island's shape
    // and slide down out of it. The island steps aside while one is open, so the panel reads as the island grown big.
    static class Drop
    {
        public const int HeaderH = 34;
        public static readonly Color Back = Color.FromArgb(14, 14, 18);
        public static readonly Color Field = Color.FromArgb(34, 34, 42);
        public static readonly Color Text = Color.FromArgb(236, 236, 242);
        public static readonly Color Dim = Color.FromArgb(150, 150, 162);

        static readonly List<Form> open = new List<Form>();

        public static bool AnyOpen { get { return open.Count > 0; } }

        // Turns a normal form into a drop panel once: borderless, dark, a header with a close button,
        // and every existing control moved below the header.
        public static void Style(Form f, string title)
        {
            f.FormBorderStyle = FormBorderStyle.None;
            f.ShowInTaskbar = false;
            f.TopMost = true;
            f.StartPosition = FormStartPosition.Manual;
            f.KeyPreview = true;
            f.BackColor = Back;
            f.ForeColor = Text;

            var c = f.ClientSize;
            f.ClientSize = new Size(c.Width, c.Height + HeaderH);
            foreach (Control x in f.Controls)
            {
                bool top = (x.Anchor & AnchorStyles.Top) != 0, bottom = (x.Anchor & AnchorStyles.Bottom) != 0;
                if (top && bottom) x.SetBounds(x.Left, x.Top + HeaderH, x.Width, x.Height - HeaderH);
                else if (top) x.Top += HeaderH;
            }
            Theme(f);

            var head = new Label
            {
                Text = title,
                Font = new Font("Segoe UI Semibold", 10f),
                ForeColor = Text,
                Location = new Point(16, 9),
                AutoSize = true
            };
            var close = new Label
            {
                Text = "\u2715",
                Font = new Font("Segoe UI Symbol", 10f),
                ForeColor = Dim,
                TextAlign = ContentAlignment.MiddleCenter,
                Size = new Size(30, 26),
                Location = new Point(f.ClientSize.Width - 40, 4),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Cursor = Cursors.Hand
            };
            close.MouseEnter += delegate { close.ForeColor = Text; };
            close.MouseLeave += delegate { close.ForeColor = Dim; };
            close.Click += delegate { Hide(f); };
            f.Controls.Add(head);
            f.Controls.Add(close);

            f.KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) { e.Handled = true; Hide(f); } };
            f.Resize += delegate { Shape(f); };
            f.Paint += delegate(object s, PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var p = Island.NotchPath(0, f.ClientSize.Width - 1, f.ClientSize.Height - 1, 22))
                using (var pen = new Pen(Color.FromArgb(60, 255, 255, 255)))
                    e.Graphics.DrawPath(pen, p);
            };
            f.HandleCreated += delegate { Native.SetWindowDisplayAffinity(f.Handle, Native.WDA_EXCLUDEFROMCAPTURE); };
            f.VisibleChanged += delegate
            {
                if (f.Visible) { if (!open.Contains(f)) open.Add(f); }
                else open.Remove(f);
                if (Island.Current != null) Island.Current.RefreshVisible();
            };
            f.FormClosed += delegate { open.Remove(f); if (Island.Current != null) Island.Current.RefreshVisible(); };
            Shape(f);
        }

        static void Shape(Form f)
        {
            using (var p = Island.NotchPath(0, f.ClientSize.Width, f.ClientSize.Height, 22))
            {
                var old = f.Region;
                f.Region = new Region(p);
                if (old != null) old.Dispose();
            }
            f.Invalidate();
        }

        // Dark colours for the stock controls; flat buttons so they match the island.
        static void Theme(Control root)
        {
            foreach (Control c in root.Controls)
            {
                if (c is Button)
                {
                    var b = (Button)c;
                    b.FlatStyle = FlatStyle.Flat;
                    b.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 82);
                    b.FlatAppearance.MouseOverBackColor = Color.FromArgb(48, 48, 58);
                    b.FlatAppearance.MouseDownBackColor = Color.FromArgb(60, 60, 72);
                    b.BackColor = Field;
                    b.ForeColor = Text;
                    b.Cursor = Cursors.Hand;
                }
                else if (c is ListView)
                {
                    var lv = (ListView)c;
                    lv.BackColor = Field;
                    lv.ForeColor = Text;
                    lv.BorderStyle = BorderStyle.None;
                    // the stock header is always light, so it is drawn here; the last column fills the rest of the row
                    lv.OwnerDraw = true;
                    lv.DrawColumnHeader += delegate(object s, DrawListViewColumnHeaderEventArgs e)
                    {
                        using (var b = new SolidBrush(Back)) e.Graphics.FillRectangle(b, e.Bounds);
                        TextRenderer.DrawText(e.Graphics, e.Header.Text, lv.Font, Rectangle.Inflate(e.Bounds, -4, 0), Dim,
                            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                    };
                    lv.DrawItem += delegate(object s, DrawListViewItemEventArgs e) { e.DrawDefault = true; };
                    lv.DrawSubItem += delegate(object s, DrawListViewSubItemEventArgs e) { e.DrawDefault = true; };
                    if (lv.Columns.Count > 0) lv.Columns[lv.Columns.Count - 1].Width = -2;
                }
                else if (c is TextBox || c is NumericUpDown)
                {
                    c.BackColor = Field;
                    c.ForeColor = Text;
                    if (c is TextBox) ((TextBox)c).BorderStyle = BorderStyle.FixedSingle;
                    else ((NumericUpDown)c).BorderStyle = BorderStyle.FixedSingle;
                }
                else if (c is ComboBox)
                {
                    // a drop-down list ignores BackColor unless it draws its own items
                    var cb = (ComboBox)c;
                    cb.FlatStyle = FlatStyle.Flat;
                    cb.BackColor = Field;
                    cb.ForeColor = Text;
                    cb.DrawMode = DrawMode.OwnerDrawFixed;
                    cb.DrawItem += delegate(object s, DrawItemEventArgs e)
                    {
                        bool sel = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
                        using (var b = new SolidBrush(sel ? Color.FromArgb(62, 62, 76) : Field)) e.Graphics.FillRectangle(b, e.Bounds);
                        if (e.Index >= 0)
                            TextRenderer.DrawText(e.Graphics, cb.GetItemText(cb.Items[e.Index]), cb.Font, Rectangle.Inflate(e.Bounds, -2, 0), Text,
                                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                    };
                }
                else if (c is TrackBar) c.BackColor = Back;
                else
                {
                    c.BackColor = Back;
                    if (c.ForeColor == SystemColors.GrayText) c.ForeColor = Dim;
                    else c.ForeColor = Text;
                }
                if (c.HasChildren) Theme(c);
            }
        }

        // Centered at the top edge of the primary screen, sliding down from above it.
        public static void Show(Form f)
        {
            var b = Screen.PrimaryScreen.Bounds;
            int x = b.Left + (b.Width - f.Width) / 2;
            if (f.Visible) { f.Activate(); return; }
            f.Location = new Point(x, b.Top - f.Height);
            f.Show();
            f.Activate();
            Slide(f, b.Top - f.Height, b.Top, null);
        }

        public static void Hide(Form f)
        {
            if (!f.Visible) return;
            int top = Screen.PrimaryScreen.Bounds.Top;
            Slide(f, f.Top, top - f.Height, delegate { f.Close(); });
        }

        static void Slide(Form f, int from, int to, Action done)
        {
            var t = new Timer { Interval = 10 };
            var clock = System.Diagnostics.Stopwatch.StartNew();
            t.Tick += delegate
            {
                if (f.IsDisposed) { t.Stop(); t.Dispose(); return; }
                double k = Math.Min(1.0, clock.Elapsed.TotalMilliseconds / 200.0);
                double e = 1 - Math.Pow(1 - k, 3);
                f.Top = (int)Math.Round(from + (to - from) * e);
                if (k < 1) return;
                t.Stop();
                t.Dispose();
                if (done != null) done();
            };
            t.Start();
        }
    }
}
