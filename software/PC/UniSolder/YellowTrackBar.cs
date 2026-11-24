using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace UniSolder
{
    /// <summary>
    /// A TrackBar with a yellow thumb and a yellow filled part of the track (same look as the Android app's sliders).
    /// Windows draws a normal TrackBar with visual styles, which ignore colours, so this one paints itself;
    /// mouse and keyboard handling stay the native ones. The native channel and thumb rectangles are used so that
    /// the drawing matches where the thumb really is.
    /// </summary>
    public class YellowTrackBar : TrackBar
    {
        public static readonly Color Yellow = Color.FromArgb(255, 193, 7);

        private const int TBM_GETTHUMBRECT = 0x0419;
        private const int TBM_GETCHANNELRECT = 0x041A;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref RECT lParam);

        public YellowTrackBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnValueChanged(EventArgs e)
        {
            base.OnValueChanged(e);
            Invalidate();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (e.Button != MouseButtons.None) Invalidate();
        }

        private Rectangle NativeRect(int msg)
        {
            var r = new RECT();
            if (IsHandleCreated) SendMessage(Handle, msg, IntPtr.Zero, ref r);
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            var thumb = NativeRect(TBM_GETTHUMBRECT);
            var chan = NativeRect(TBM_GETCHANNELRECT);
            bool horizontal = Orientation == Orientation.Horizontal;
            if (chan.Width <= 0 && chan.Height <= 0)
                chan = horizontal ? new Rectangle(12, Height / 2 - 2, Width - 24, 4) : new Rectangle(Width / 2 - 2, 12, 4, Height - 24);
            if (!horizontal)
                chan = new Rectangle(chan.Top, chan.Left, chan.Height, chan.Width);    //the channel rectangle comes back rotated for a vertical bar

            Color fill = Enabled ? Yellow : Color.FromArgb(104, 88, 30);
            Color track = Color.FromArgb(72, 72, 82);
            Color thumbColor = Enabled ? Yellow : Color.FromArgb(104, 88, 30);
            int span = Math.Max(1, Maximum - Minimum);
            double frac = (Value - Minimum) / (double)span;

            if (horizontal)
            {
                int cy = (thumb.Height > 0) ? thumb.Top + thumb.Height / 2 : Height / 2;
                var bar = new Rectangle(chan.Left, cy - 2, chan.Width, 4);
                using (var b = new SolidBrush(track)) g.FillRectangle(b, bar);
                int x = (thumb.Width > 0) ? thumb.Left + thumb.Width / 2 : chan.Left + (int)(chan.Width * frac);
                using (var b = new SolidBrush(fill)) g.FillRectangle(b, new Rectangle(chan.Left, cy - 2, Math.Max(0, x - chan.Left), 4));
                DrawTicks(g, chan.Left, chan.Right, true, cy + 12);
                var knob = new Rectangle(x - 8, cy - 8, 16, 16);
                using (var b = new SolidBrush(thumbColor)) g.FillEllipse(b, knob);
                if (Focused) using (var p = new Pen(Color.White, 1.5f)) g.DrawEllipse(p, knob);
            }
            else
            {
                int cx = (thumb.Width > 0) ? thumb.Left + thumb.Width / 2 : Width / 2;
                var bar = new Rectangle(cx - 2, chan.Top, 4, chan.Height);
                using (var b = new SolidBrush(track)) g.FillRectangle(b, bar);
                int y = (thumb.Height > 0) ? thumb.Top + thumb.Height / 2 : chan.Bottom - (int)(chan.Height * frac);
                using (var b = new SolidBrush(fill)) g.FillRectangle(b, new Rectangle(cx - 2, y, 4, Math.Max(0, chan.Bottom - y)));
                var knob = new Rectangle(cx - 8, y - 8, 16, 16);
                using (var b = new SolidBrush(thumbColor)) g.FillEllipse(b, knob);
                if (Focused) using (var p = new Pen(Color.White, 1.5f)) g.DrawEllipse(p, knob);
            }
        }

        private void DrawTicks(Graphics g, int from, int to, bool horizontal, int at)
        {
            if (TickStyle == TickStyle.None || TickFrequency <= 0) return;
            int span = Maximum - Minimum;
            if (span <= 0 || span / TickFrequency > 100) return;
            using (var p = new Pen(Color.FromArgb(110, 110, 120)))
            {
                for (int v = Minimum; v <= Maximum; v += TickFrequency)
                {
                    int x = from + (int)((to - from) * (double)(v - Minimum) / span);
                    if (horizontal) g.DrawLine(p, x, at, x, at + 4);
                }
            }
        }
    }
}
