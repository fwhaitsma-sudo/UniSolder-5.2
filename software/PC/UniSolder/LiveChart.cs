using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace UniSolder
{
    /// <summary>
    /// Scrolling chart of the live data like the one of the Android app (ChartView.kt): the newest sample at the right edge,
    /// vertical axis 0..500 degrees C, the power line on the same axis with 100% at the top, and the heater resistance on its own
    /// axis in ohm on the right, fitted automatically so that its trace stays in the band between a quarter and half of the height.
    /// Readings that are not a number leave a gap in the trace. Under the plot a legend: a click switches a trace on or off.
    /// </summary>
    class LiveChart : Control
    {
        public const int Capacity = 500;
        private const float MinOhm = 0.05f;
        /// <summary>A heater has a few ohm: readings of this and more (open heater, 1000 ohm and more) are not valid.</summary>
        public const float MaxOhm = 50f;
        private const int FitSamples = 100;
        private static readonly float[] NiceSteps = { 0.1f, 0.2f, 0.5f, 1f, 2f, 5f };

        public class Series
        {
            public readonly string Name;
            public readonly Color Color;
            public bool Visible;
            public readonly bool RightAxis;
            public readonly float[] Data = new float[Capacity];

            public Series(string name, Color color, bool visible, bool rightAxis = false)
            {
                Name = name;
                Color = color;
                Visible = visible;
                RightAxis = rightAxis;
            }
        }

        public readonly List<Series> Items = new List<Series>
        {
            new Series("Set", Color.FromArgb(255, 82, 82), true),
            new Series("Temp", Color.FromArgb(105, 240, 174), true),
            new Series("Power", Color.FromArgb(255, 202, 40), true),
            // the temperature the PID loop reacts to (smoothed temperature plus the derivative term); off until switched on
            new Series("PID input", Color.FromArgb(64, 196, 255), false),
            // diagnostics (off by default): temperature after the averaging filter, change of the raw ADC reading around 250
            new Series("Filtered", Color.FromArgb(224, 224, 224), false),
            new Series("ADC Δ", Color.FromArgb(255, 128, 171), false),
            // heater resistance in ohm, on the right axis
            new Series("Ω", Color.FromArgb(179, 136, 255), true, true),
            new Series("Wave sh.", Color.FromArgb(255, 171, 64), false),
            new Series("Flag", Color.FromArgb(128, 222, 234), false)
        };

        private int count;
        private int head;
        private float axLo, axHi = 10f;
        private bool axInit;
        private string notice;
        private readonly List<KeyValuePair<Rectangle, Series>> chips = new List<KeyValuePair<Rectangle, Series>>();

        /// <summary>Text in the middle of the chart, for example while no tip is in the iron; null = none.</summary>
        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public string Notice
        {
            get { return notice; }
            set
            {
                if (notice == value) return;
                notice = value;
                Invalidate();
            }
        }

        public LiveChart()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            MinimumSize = new Size(200, 160);
        }

        private Series Ohm { get { return Items.Find(s => s.RightAxis); } }

        /// <summary>Adds one sample: values in the order of <see cref="Items"/>.</summary>
        /// <summary>
        /// Adds one sample: values in the order of <see cref="Items"/>. Samples are not drawn on arrival: over Bluetooth they arrive in bunches,
        /// and drawing each at its arrival time makes the trace stutter. They wait in a short queue and are released at the average rate.
        /// </summary>
        public void Add(float[] values)
        {
            double now = clock.Elapsed.TotalSeconds;
            lock (queue)
            {
                queue.Enqueue((float[])values.Clone());
                while (queue.Count > 30) queue.Dequeue();
                arrivals.Enqueue(now);
                while (arrivals.Count > 30 || (arrivals.Count > 2 && now - arrivals.Peek() > 3.0)) arrivals.Dequeue();
                if (arrivals.Count >= 8)
                {
                    double span = now - arrivals.Peek();
                    if (span > 0.2) arrivalRate = (arrivals.Count - 1) / span;
                }
                lastArrival = now;
            }
            // no Invalidate here: the render loop below redraws once per screen refresh
        }

        /// <summary>Measured samples per second (average over the last seconds); 0 until known.</summary>
        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public double SampleRate { get { return arrivalRate; } }

        private readonly Queue<float[]> queue = new Queue<float[]>();
        private readonly Queue<double> arrivals = new Queue<double>();
        private double arrivalRate, lastArrival, lastRelease;

        /// <summary>Moves queued samples into the chart at the steady pace (UI thread).</summary>
        private void Pump()
        {
            double now = clock.Elapsed.TotalSeconds;
            double step = arrivalRate > 1.0 ? Math.Max(0.02, Math.Min(0.5, 1.0 / arrivalRate)) : 0.1;
            lock (queue)
            {
                while (queue.Count > 0)
                {
                    double s = queue.Count > 4 ? step * 0.5 : step;       // catch up when many wait
                    if (lastAdd > 0 && now - lastRelease < s) break;
                    var values = queue.Dequeue();
                    Commit(values);
                    interval = step;
                    lastAdd = now;
                    lastRelease = (lastRelease > 0 && now - lastRelease < 2 * s) ? lastRelease + s : now;
                }
            }
        }

        private void Commit(float[] values)
        {
            for (int i = 0; i < Items.Count; i++) Items[i].Data[head] = values[i];
            head = (head + 1) % Capacity;
            if (count < Capacity) count++;
        }

        public void ClearQueue()
        {
            lock (queue) { queue.Clear(); arrivals.Clear(); }
        }

        // ---- smooth scrolling: samples arrive about 10 times a second, the screen refreshes 60 times or more.
        // A render thread waits for the screen refresh (DwmFlush) and asks for one redraw per refresh while samples are coming; the trace slides
        // by a fraction of a sample step between two samples (one sample of delay) instead of jumping a full step per sample.
        private static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        private volatile bool running;
        private volatile bool drawPending;
        private double lastAdd, interval = 0.1;

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmFlush();

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            running = true;
            var t = new System.Threading.Thread(RenderLoop) { IsBackground = true, Name = "LiveChart refresh" };
            t.Start();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            running = false;
            base.OnHandleDestroyed(e);
        }

        private void RenderLoop()
        {
            bool sync = true;
            while (running)
            {
                try
                {
                    if (!sync || DwmFlush() != 0)
                    {
                        sync = false;
                        System.Threading.Thread.Sleep(16);       // no desktop composition: about 60 per second
                    }
                }
                catch (Exception) { sync = false; System.Threading.Thread.Sleep(16); }
                if (!running || drawPending) continue;
                double idle = clock.Elapsed.TotalSeconds - lastArrival;
                bool waiting;
                lock (queue) waiting = queue.Count > 0;
                if (!waiting && (lastAdd <= 0 || idle > interval * 1.5 + 0.1) && clock.Elapsed.TotalSeconds - lastAdd > interval * 1.5 + 0.1) continue;      // no samples coming (paused, disconnected): nothing moves
                drawPending = true;
                try { BeginInvoke(new Action(() => { Pump(); Invalidate(); })); }
                catch (Exception) { drawPending = false; }
            }
        }

        public void Clear()
        {
            ClearQueue();
            count = 0;
            head = 0;
            Invalidate();
        }

        /// <summary>
        /// Fits the ohm axis to the recent samples: the data sits in the band from 25% to 50% of the height. The fit uses the 10th to
        /// 90th percentile of the last 100 samples, so a few wild readings do not stretch the axis.
        /// </summary>
        private void UpdateOhmAxis(int oldest)
        {
            var d = Ohm.Data;
            int from = Math.Max(0, count - FitSamples);
            var v = new float[count - from];
            int n = 0;
            for (int i = from; i < count; i++)
            {
                float x = d[(oldest + i) % Capacity];
                if (!float.IsNaN(x) && x > MinOhm && x < MaxOhm) v[n++] = x;
            }
            if (n < 3) return;
            Array.Sort(v, 0, n);
            float mn = v[(n - 1) / 10];
            float mx = v[(n - 1) * 9 / 10];
            float span = Math.Max(mx - mn, 0.4f) * 4f;      // the data band is a quarter of the height
            float lo = (mn + mx) / 2f - 0.375f * span;
            if (!axInit)
            {
                axLo = lo;
                axHi = lo + span;
                axInit = true;
            }
            else
            {
                axLo += (lo - axLo) * 0.1f;                 // glide, so the axis does not jump
                axHi += (lo + span - axHi) * 0.1f;
            }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            foreach (var c in chips)
            {
                if (c.Key.Contains(e.Location))
                {
                    c.Value.Visible = !c.Value.Visible;
                    Invalidate();
                    return;
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool over = false;
            foreach (var c in chips) if (c.Key.Contains(e.Location)) over = true;
            Cursor = over ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            drawPending = false;
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            float k = Math.Max(1f, DeviceDpi / 96f);
            using (var small = Theme.MakeFont(9f))
            using (var chipFont = Theme.MakeFont(10f))
            using (var big = Theme.MakeFont(16f, FontStyle.Bold))
            {
                // legend first (its height decides the plot height)
                chips.Clear();
                float chipH = 26f * k;
                float x = 4f * k, y = 0;
                var layout = new List<KeyValuePair<RectangleF, Series>>();
                int rows = 1;
                foreach (var s in Items)
                {
                    string text = "● " + s.Name;
                    float w = g.MeasureString(text, chipFont).Width + 10f * k;
                    if (x + w > Width - 4f * k && x > 4f * k) { x = 4f * k; rows++; }
                    layout.Add(new KeyValuePair<RectangleF, Series>(new RectangleF(x, rows - 1, w, chipH), s));
                    x += w;
                }
                float legendH = rows * chipH + 4f * k;
                float legendTop = Height - legendH;
                foreach (var l in layout)
                {
                    var r = new Rectangle((int)l.Key.X, (int)(legendTop + l.Key.Y * chipH), (int)l.Key.Width, (int)chipH);
                    chips.Add(new KeyValuePair<Rectangle, Series>(r, l.Value));
                    var col = l.Value.Visible ? l.Value.Color : Color.FromArgb(77, l.Value.Color);
                    using (var br = new SolidBrush(col))
                        g.DrawString("● " + l.Value.Name, chipFont, br, r.X + 4f * k, r.Y + (chipH - chipFont.GetHeight(g)) / 2f);
                }

                var ohm = Ohm;
                bool showOhm = ohm.Visible && count >= 2;
                int oldest = (head - count + Capacity) % Capacity;
                if (showOhm) UpdateOhmAxis(oldest);
                float left = 36f * k;
                float right = Width - (ohm.Visible ? 44f : 6f) * k;
                float top = 6f * k;
                float bottom = legendTop - 6f * k;
                float w2 = right - left, h = bottom - top;
                if (w2 <= 10f || h <= 10f) return;

                using (var grid = new Pen(Color.FromArgb(70, 255, 255, 255), 1f))
                using (var text = new SolidBrush(Color.FromArgb(170, 255, 255, 255)))
                {
                    for (int v = 0; v <= 500; v += 100)
                    {
                        float yy = bottom - v / 500f * h;
                        g.DrawLine(grid, left, yy, right, yy);
                        g.DrawString(v.ToString(), small, text, 2f * k, yy - small.GetHeight(g) / 2f);
                    }
                }

                if (showOhm && axInit && axHi > axLo)
                {
                    // axis labels in ohm, in the colour of the trace, at nice round steps
                    float rawStep = (axHi - axLo) / 5f;
                    float step = 10f;
                    foreach (var st in NiceSteps) if (st >= rawStep) { step = st; break; }
                    using (var br = new SolidBrush(ohm.Color))
                    {
                        float t = (float)Math.Ceiling(axLo / step) * step;
                        while (t <= axHi)
                        {
                            float yy = bottom - (t - axLo) / (axHi - axLo) * h;
                            g.DrawString(t.ToString(step < 1f ? "0.0" : "0", System.Globalization.CultureInfo.InvariantCulture), small, br, right + 3f * k, yy - small.GetHeight(g) / 2f);
                            t += step;
                        }
                        g.DrawString("Ω", small, br, right + 3f * k, top - 2f * k);
                    }
                }

                g.SetClip(new RectangleF(left, top, w2, h));
                // slide: just after a sample arrives the trace is one step to the left, one step later it has reached its place
                double since = clock.Elapsed.TotalSeconds - lastAdd;
                float frac = lastAdd <= 0 ? 1f : (float)Math.Max(0.0, Math.Min(1.0, since / interval));
                float slide = -(1f - frac) * w2 / (Capacity - 1f);
                var run = new List<PointF>();
                foreach (var s in Items)
                {
                    if (!s.Visible || count < 2) continue;
                    run.Clear();
                    using (var pen = new Pen(s.Color, 2f * k) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        for (int i = 0; i < count; i++)
                        {
                            float raw = s.Data[(oldest + i) % Capacity];
                            float px = left + slide + (Capacity - count + i) / (Capacity - 1f) * w2;
                            float py;
                            bool ok = !float.IsNaN(raw);          // no valid reading (tip removed): gap in the trace
                            if (ok && s.RightAxis)
                            {
                                if (raw <= MinOhm || raw >= MaxOhm || !axInit || axHi <= axLo) ok = false;
                                py = bottom - (raw - axLo) / (axHi - axLo) * h;
                                if (ok && (py < top - h * 0.1f || py > bottom + h * 0.1f)) ok = false;     // wild reading: no line to nowhere
                            }
                            else
                            {
                                py = bottom - Math.Max(0f, Math.Min(520f, ok ? raw : 0f)) / 500f * h;
                            }
                            if (ok) run.Add(new PointF(px, py));
                            else Flush(g, pen, run);
                        }
                        Flush(g, pen, run);
                    }
                }
                g.ResetClip();

                var note = notice;
                if (note != null)
                {
                    using (var br = new SolidBrush(Color.FromArgb(230, 255, 255, 255)))
                    {
                        var size = g.MeasureString(note, big);
                        g.DrawString(note, big, br, left + (w2 - size.Width) / 2f, top + (h - size.Height) / 2f);
                    }
                }
            }
        }

        private static void Flush(Graphics g, Pen pen, List<PointF> run)
        {
            if (run.Count >= 2) g.DrawLines(pen, run.ToArray());
            else if (run.Count == 1) g.DrawLine(pen, run[0], new PointF(run[0].X + 0.5f, run[0].Y));
            run.Clear();
        }
    }

    /// <summary>Flat bar for the heater power: grey track, yellow fill (0..100).</summary>
    class HeaterBar : Control
    {
        private int value;

        public HeaterBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            Height = 14;
        }

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int Value
        {
            get { return value; }
            set
            {
                int v = Math.Max(0, Math.Min(100, value));
                if (v == this.value) return;
                this.value = v;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            int h = Math.Max(4, (int)(6 * DeviceDpi / 96f));
            int y = (Height - h) / 2;
            using (var track = new SolidBrush(Color.FromArgb(96, 96, 96)))
                g.FillRectangle(track, 0, y, Width, h);
            using (var fill = new SolidBrush(Theme.Yellow))
                g.FillRectangle(fill, 0, y, Width * value / 100, h);
        }
    }
}
