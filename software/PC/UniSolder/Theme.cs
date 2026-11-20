using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace UniSolder
{
    /// <summary>
    /// Colours, fonts and small helpers shared by all pages: the dark look with the yellow accent of the Android app.
    /// </summary>
    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(16, 16, 18);
        public static readonly Color Card = Color.FromArgb(28, 28, 32);
        public static readonly Color Field = Color.FromArgb(36, 36, 42);
        public static readonly Color Text = Color.White;
        public static readonly Color Muted = Color.FromArgb(176, 176, 184);
        public static readonly Color Button = Color.FromArgb(62, 62, 70);
        public static readonly Color ButtonHover = Color.FromArgb(82, 82, 92);
        public static readonly Color ButtonOff = Color.FromArgb(34, 34, 40);
        public static readonly Color TextOff = Color.FromArgb(112, 112, 120);
        public static readonly Color Yellow = Color.FromArgb(255, 193, 7);
        public static readonly Color YellowHover = Color.FromArgb(255, 213, 79);
        public static readonly Color YellowOff = Color.FromArgb(104, 88, 30);
        public static readonly Color Line = Color.FromArgb(80, 80, 90);

        public static Font MakeFont(float points, FontStyle style = FontStyle.Regular)
        {
            return new Font("Segoe UI", points, style, GraphicsUnit.Point);
        }

        /// <summary>Yellow section heading, like the headings of the Android pages.</summary>
        public static Label Heading(string text)
        {
            var l = new Label();
            l.AutoSize = true;
            l.Text = text;
            l.Font = MakeFont(14f, FontStyle.Bold);
            l.ForeColor = Yellow;
            l.BackColor = Bg;
            l.Margin = new Padding(0, 22, 0, 6);
            return l;
        }

        /// <summary>Normal text that wraps at the width of the page.</summary>
        public static Label Body(string text)
        {
            var l = new Label();
            l.AutoSize = true;
            l.Text = text;
            l.Font = MakeFont(10f);
            l.ForeColor = Text;
            l.BackColor = Bg;
            l.Margin = new Padding(0, 2, 0, 6);
            return l;
        }

        /// <summary>Small grey explanation under a field or button.</summary>
        public static Label Hint(string text)
        {
            var l = Body(text);
            l.Font = MakeFont(9f);
            l.ForeColor = Muted;
            return l;
        }

        public static void Style(ThemedButton b, string text, int width, int height = 44)
        {
            b.Text = text;
            b.AutoSize = false;
            b.Size = new Size(width, height);
            b.Margin = new Padding(0, 6, 10, 6);
        }

        /// <summary>A horizontal row of buttons that wraps when the window is narrow.</summary>
        public static FlowLayoutPanel Row(params Control[] items)
        {
            var r = new FlowLayoutPanel();
            r.FlowDirection = FlowDirection.LeftToRight;
            r.WrapContents = true;
            r.AutoSize = true;
            r.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            r.BackColor = Bg;
            r.Margin = new Padding(0);
            r.Controls.AddRange(items);
            return r;
        }

        // ---- dark window parts (title bar, scroll bars): best effort, ignored when the system does not have them ----

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

        public static void DarkTitle(Form f)
        {
            try
            {
                int on = 1;
                DwmSetWindowAttribute(f.Handle, 20, ref on, sizeof(int));
            }
            catch (Exception) { }
        }

        public static void DarkScroll(Control c)
        {
            EventHandler apply = delegate
            {
                try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); }
                catch (Exception) { }
            };
            if (c.IsHandleCreated) apply(c, EventArgs.Empty);
            else c.HandleCreated += apply;
        }

        public static void DoubleBuffer(Control c)
        {
            try
            {
                typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(c, true, null);
            }
            catch (Exception) { }
        }

        /// <summary>Dark list with a grey header and a yellow-brown selection.</summary>
        public static void StyleList(ListView lv)
        {
            lv.OwnerDraw = true;
            lv.BackColor = Field;
            lv.ForeColor = Text;
            lv.BorderStyle = BorderStyle.None;
            lv.Font = MakeFont(10f);
            DoubleBuffer(lv);
            DarkScroll(lv);
            const TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            lv.DrawColumnHeader += (s, e) =>
            {
                using (var b = new SolidBrush(Button)) e.Graphics.FillRectangle(b, e.Bounds);
                TextRenderer.DrawText(e.Graphics, e.Header.Text, MakeFontCached(lv), Rectangle.Inflate(e.Bounds, -8, 0), Text, flags);
            };
            lv.DrawItem += (s, e) => { };
            lv.DrawSubItem += (s, e) =>
            {
                bool sel = e.Item.Selected;
                using (var b = new SolidBrush(sel ? Color.FromArgb(96, 76, 0) : Field)) e.Graphics.FillRectangle(b, e.Bounds);
                TextRenderer.DrawText(e.Graphics, e.SubItem.Text, lv.Font, Rectangle.Inflate(e.Bounds, -8, 0), Text, flags);
            };
        }

        private static Font MakeFontCached(Control c)
        {
            return c.Font;
        }
    }

    /// <summary>Rounded button in the colours of the Android app: grey, yellow for the main action, pill shape for the page bar.</summary>
    class ThemedButton : Button
    {
        private bool hover, down;

        /// <summary>The main action: yellow with black text.</summary>
        public bool Strong { get; set; }
        /// <summary>A button of the page bar.</summary>
        public bool Nav { get; set; }
        /// <summary>The page bar button of the page that is shown.</summary>
        public bool Selected { get; set; }

        public ThemedButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            BackColor = Theme.Bg;
            Font = Theme.MakeFont(10f);
            Size = new Size(150, 44);
            Cursor = Cursors.Hand;
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            var t = TextRenderer.MeasureText(Text, Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPrefix);
            return new Size(t.Width + 36, Math.Max(44, t.Height + 20));
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs mevent) { down = true; Invalidate(); base.OnMouseDown(mevent); }
        protected override void OnMouseUp(MouseEventArgs mevent) { down = false; Invalidate(); base.OnMouseUp(mevent); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
        protected override void OnParentBackColorChanged(EventArgs e) { base.OnParentBackColorChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color fill, fore;
            FontStyle style = FontStyle.Regular;
            bool yellow = Strong && !Nav;
            if (!Enabled)
            {
                fill = yellow ? Theme.YellowOff : Theme.ButtonOff;
                fore = Theme.TextOff;
            }
            else if (Nav)
            {
                fill = Selected ? Color.FromArgb(72, 72, 82) : (hover ? Theme.Button : Theme.Card);
                fore = Selected ? Theme.Text : Theme.Muted;
                if (Selected) style = FontStyle.Bold;
            }
            else if (yellow)
            {
                fill = (hover || down) ? Theme.YellowHover : Theme.Yellow;
                fore = Color.Black;
            }
            else
            {
                fill = (hover || down) ? Theme.ButtonHover : Theme.Button;
                fore = Theme.Text;
            }

            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = RoundRect(r, Math.Min(Height / 2, Nav ? Height / 2 : 18)))
            using (var b = new SolidBrush(fill))
            {
                g.FillPath(b, path);
            }
            if (Focused && ShowFocusCues)
            {
                var fr = Rectangle.Inflate(r, -3, -3);
                using (var path = RoundRect(fr, Math.Max(2, Math.Min(Height / 2, 18) - 3)))
                using (var p = new Pen(Color.FromArgb(120, fore), 1f)) g.DrawPath(p, path);
            }
            using (var f = new Font(Font, style))
            {
                TextRenderer.DrawText(g, Text, f, ClientRectangle, fore,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }

        internal static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            int d = Math.Max(1, radius * 2);
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    /// <summary>Check box with a yellow box when ticked; the text keeps the colour that is set for it (the trace colours of the Live page).</summary>
    class ThemedCheck : CheckBox
    {
        public ThemedCheck()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AutoSize = true;
            Font = Theme.MakeFont(10f);
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Cursor = Cursors.Hand;
            Margin = new Padding(0, 4, 0, 4);
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            var t = TextRenderer.MeasureText(Text, Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPrefix);
            return new Size(t.Width + 34, Math.Max(28, t.Height + 8));
        }

        protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int box = 20;
            var r = new Rectangle(1, (Height - box) / 2, box - 1, box - 1);
            using (var path = ThemedButton.RoundRect(r, 5))
            {
                if (Checked)
                {
                    using (var b = new SolidBrush(Enabled ? Theme.Yellow : Theme.YellowOff)) g.FillPath(b, path);
                    using (var p = new Pen(Color.Black, 2.2f))
                    {
                        g.DrawLines(p, new[] { new Point(r.X + 4, r.Y + 10), new Point(r.X + 8, r.Y + 14), new Point(r.X + 15, r.Y + 5) });
                    }
                }
                else
                {
                    using (var b = new SolidBrush(Theme.Field)) g.FillPath(b, path);
                    using (var p = new Pen(Focused ? Theme.Yellow : Theme.Line, 1.5f)) g.DrawPath(p, path);
                }
            }
            var tr = new Rectangle(box + 10, 0, Math.Max(1, Width - box - 10), Height);
            TextRenderer.DrawText(g, Text, Font, tr, Enabled ? ForeColor : Theme.TextOff,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>
    /// A scrolling page with one column of sections (heading, text, buttons) in the width of the window, at most a comfortable
    /// line length. Texts wrap at that width and rows of buttons wrap when the window is narrow, so nothing can overlap or be cut off.
    /// Controls with Tag = "fill" are made as wide as the column.
    /// </summary>
    class Page : Panel
    {
        public readonly FlowLayoutPanel Col = new FlowLayoutPanel();
        /// <summary>Widest column in pixels at 96 dpi.</summary>
        public int MaxContentWidth = 780;

        public Page()
        {
            Dock = DockStyle.Fill;
            AutoScroll = true;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.MakeFont(10f);
            Padding = new Padding(28, 8, 28, 28);
            Col.FlowDirection = FlowDirection.TopDown;
            Col.WrapContents = false;
            Col.AutoSize = true;
            Col.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Col.BackColor = Theme.Bg;
            Col.Margin = new Padding(0);
            Col.Location = new Point(Padding.Left, Padding.Top);
            Controls.Add(Col);
            Col.ControlAdded += (s, e) => Fit();
            Theme.DarkScroll(this);
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            Fit();
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            Fit();
        }

        public void Fit()
        {
            int max = (int)(MaxContentWidth * DeviceDpi / 96f);
            int w = Math.Max(200, Math.Min(max, ClientSize.Width - Padding.Horizontal));
            Col.SuspendLayout();
            foreach (Control c in Col.Controls)
            {
                if (c.Tag as string == "fill") c.Width = w;
                else if (c is Label || c is FlowLayoutPanel) c.MaximumSize = new Size(w, 0);
            }
            Col.ResumeLayout(true);
        }
    }

    /// <summary>The page bar at the bottom: rounded buttons, the page that is shown is lighter and bold (as in the Android app).</summary>
    class PillNav : TableLayoutPanel
    {
        private readonly List<ThemedButton> items = new List<ThemedButton>();
        public event Action<int> PageSelected;

        public PillNav(string[] names)
        {
            Dock = DockStyle.Bottom;
            Height = 72;
            BackColor = Theme.Bg;
            Padding = new Padding(12, 8, 12, 14);
            ColumnCount = names.Length;
            RowCount = 1;
            RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            for (int i = 0; i < names.Length; i++)
            {
                ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / names.Length));
                int index = i;
                var b = new ThemedButton();
                b.Nav = true;
                b.Text = names[i];
                b.AutoSize = false;
                b.Dock = DockStyle.Fill;
                b.Margin = new Padding(4, 0, 4, 0);
                b.Font = Theme.MakeFont(11f);
                b.Click += (s, e) => { Select(index); PageSelected?.Invoke(index); };
                items.Add(b);
                Controls.Add(b, i, 0);
            }
        }

        public void Select(int index)
        {
            for (int i = 0; i < items.Count; i++)
            {
                items[i].Selected = i == index;
                items[i].Invalidate();
            }
        }
    }

    /// <summary>Status bar in the dark colours.</summary>
    class DarkRenderer : ToolStripProfessionalRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (var b = new SolidBrush(Theme.Card)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }

        protected override void OnRenderStatusStripSizingGrip(ToolStripRenderEventArgs e) { }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = Theme.Muted;
            base.OnRenderItemText(e);
        }
    }
}
