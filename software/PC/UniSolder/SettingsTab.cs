using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace UniSolder
{
    /// <summary>
    /// Station settings: the same options as the menu on the station and as the settings page of the Android app (firmware protocol 3,
    /// commands 0x40/0x41, the name 0x42/0x43). A change is applied in the station at once.
    /// Not here: INPUT, CALIBRATE, INST.INFO, VERSION, PINCODE, ROTATION, BUTTONS, MENU DOWN.
    /// All commands run on the UI thread (each waits at most about a second).
    /// </summary>
    class SettingsTab : Page
    {
        private enum Kind { Preset, Temp, Number, Choice }

        /// <summary>One setting. Lo..Hi are in menu units (degrees for temperatures).</summary>
        private class Spec
        {
            public int Id;
            public string Name;
            public Kind Kind;
            public int Lo, Hi;
            public string[] Choices;
            public string ZeroLabel;
            public string Unit = "";
            public string Note = "";
        }

        private class Row
        {
            public Spec Spec;
            public Label Value;
            public YellowTrackBar Bar;
            public ThemedButton Minus, Plus;
            public readonly List<ThemedButton> Segments = new List<ThemedButton>();
            public Timer SendTimer;
            public int Current;
            public bool Updating;

            public int ToValue(int p)
            {
                if (Spec.Kind == Kind.Preset) return p == 0 ? 0 : Spec.Lo + p - 1;
                return Spec.Lo + p;
            }

            public int ToProgress(int v)
            {
                if (Spec.Kind == Kind.Preset) return v == 0 ? 0 : Math.Max(1, v - Spec.Lo + 1);
                return Math.Max(0, v - Spec.Lo);
            }

            public int MaxProgress { get { return Spec.Kind == Kind.Preset ? Spec.Hi - Spec.Lo + 1 : Spec.Hi - Spec.Lo; } }
        }

        private readonly UniSolderComm comm;
        private readonly Label status = new Label();
        private readonly List<Row> rows = new List<Row>();
        private readonly TextBox nameBox = new TextBox();
        private readonly ThemedButton nameSave = new ThemedButton();
        private readonly List<Control> nameControls = new List<Control>();
        private int[] values = new int[29];

        /// <summary>The user wants to leave the page (Back button).</summary>
        public event Action Back;

        private static readonly Spec[] Specs =
        {
            new Spec { Id = 0, Name = "T1", Kind = Kind.Preset, Lo = 100, Hi = 450 },
            new Spec { Id = 1, Name = "T2", Kind = Kind.Preset, Lo = 100, Hi = 450 },
            new Spec { Id = 2, Name = "T3", Kind = Kind.Preset, Lo = 100, Hi = 450 },
            new Spec { Id = 22, Name = "T4", Kind = Kind.Preset, Lo = 100, Hi = 450 },
            new Spec { Id = 3, Name = "Holder temperature", Kind = Kind.Temp, Lo = 100, Hi = 450, Note = "Temperature while the iron rests in the stand." },
            new Spec { Id = 4, Name = "Sleep temperature", Kind = Kind.Temp, Lo = 100, Hi = 450, Note = "Temperature after the sleep time has passed." },
            new Spec { Id = 5, Name = "Sleep time", Kind = Kind.Number, Lo = 0, Hi = 255, ZeroLabel = "OFF", Note = "Seconds when the station senses the iron in the stand, otherwise minutes." },
            new Spec { Id = 6, Name = "Stand-by time", Kind = Kind.Number, Lo = 0, Hi = 255, ZeroLabel = "OFF", Unit = " min", Note = "Heater off after this time." },
            new Spec { Id = 11, Name = "Wake up", Kind = Kind.Choice, Lo = 0, Hi = 3, Choices = new[] { "OFF", "KEY", "HOLD", "BOTH" }, Note = "What wakes the station from sleep: a key press, lifting the iron, both." },
            new Spec { Id = 8, Name = "Degrees", Kind = Kind.Choice, Lo = 0, Hi = 1, Choices = new[] { "Celsius", "Fahrenheit" } },
            new Spec { Id = 9, Name = "Brightness", Kind = Kind.Number, Lo = 1, Hi = 15 },
            new Spec { Id = 28, Name = "Sound", Kind = Kind.Choice, Lo = 0, Hi = 1, Choices = new[] { "OFF", "ON" } },
            new Spec { Id = 18, Name = "Temperature step", Kind = Kind.Number, Lo = 1, Hi = 50, Unit = "°", Note = "Step of the knob when setting the temperature." },
            new Spec { Id = 19, Name = "Display step", Kind = Kind.Number, Lo = 1, Hi = 5, Unit = "°", Note = "Resolution of the shown temperature." },
            new Spec { Id = 7, Name = "Holder sensor", Kind = Kind.Choice, Lo = 0, Hi = 2, Choices = new[] { "OFF", "ON", "AUTO" } },
            new Spec { Id = 13, Name = "Sensor filter", Kind = Kind.Number, Lo = 0, Hi = 255, ZeroLabel = "OFF" },
            new Spec { Id = 27, Name = "Sound pitch", Kind = Kind.Number, Lo = 1, Hi = 9 }
        };

        private static Spec Find(int id)
        {
            foreach (var s in Specs) if (s.Id == id) return s;
            throw new ArgumentException("setting " + id);
        }

        public SettingsTab(UniSolderComm comm)
        {
            this.comm = comm;
            var col = Col;

            var back = new ThemedButton();
            Theme.Style(back, "‹ Back", 110);
            back.Margin = new Padding(0, 14, 0, 0);
            back.Click += (s, e) => Back?.Invoke();
            col.Controls.Add(back);
            col.Controls.Add(Theme.Heading("Station settings"));

            status.AutoSize = true;
            status.Font = Theme.MakeFont(10f);
            status.ForeColor = Theme.Muted;
            status.BackColor = Theme.Bg;
            status.Margin = new Padding(0, 0, 0, 4);
            col.Controls.Add(status);

            AddSection("Temperatures", 0, 1, 2, 22, 3, 4);
            AddSection("Sleep and stand-by", 5, 6, 11, 7);
            AddSection("Display and sound", 8, 9, 28, 27);
            AddSection("Knob and sensor", 18, 19, 13);

            // power-up name
            var nameHead = Theme.Heading("Name");
            col.Controls.Add(nameHead);
            nameControls.Add(nameHead);
            var nameHint = Theme.Hint("Shown on the station at power-up. Up to 10 characters: letters, digits, space, - and .");
            col.Controls.Add(nameHint);
            nameControls.Add(nameHint);
            nameBox.MaxLength = 10;
            nameBox.BackColor = Theme.Field;
            nameBox.ForeColor = Theme.Text;
            nameBox.BorderStyle = BorderStyle.FixedSingle;
            nameBox.Font = Theme.MakeFont(12f);
            nameBox.Width = 220;
            nameBox.Margin = new Padding(0, 0, 12, 0);
            nameBox.KeyPress += (s, e) =>
            {
                char c = e.KeyChar;
                bool ok = char.IsControl(c) || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == ' ' || c == '-' || c == '.';
                if (!ok) e.Handled = true;
            };
            Theme.Style(nameSave, "Save", 110, 40);
            nameSave.Margin = new Padding(0);
            nameSave.Click += (s, e) => SaveName();
            var nameRow = new FlowLayoutPanel { AutoSize = true, BackColor = Theme.Bg, WrapContents = false, Margin = new Padding(0, 0, 0, 8) };
            nameRow.Controls.Add(nameBox);
            nameRow.Controls.Add(nameSave);
            col.Controls.Add(nameRow);
            nameControls.Add(nameRow);

            col.Controls.Add(Theme.Hint("INPUT, calibration, instrument info and the pincode can only be changed on the station itself."));

            foreach (var c in nameControls) c.Visible = false;
            EnableAll(false);
            SetStatus("Not connected.");
        }

        private void AddSection(string title, params int[] ids)
        {
            Col.Controls.Add(Theme.Heading(title));
            foreach (int id in ids) AddRow(Find(id));
        }

        private void AddRow(Spec spec)
        {
            var r = new Row { Spec = spec };
            rows.Add(r);

            // header: name on the left, value on the right
            var head = new TableLayoutPanel { Tag = "fill", ColumnCount = 2, RowCount = 1, Height = 32, Width = 400, BackColor = Theme.Bg, Margin = new Padding(0, 10, 0, 0) };
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            head.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var name = new Label { Text = spec.Name, AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = Theme.MakeFont(11f), ForeColor = Theme.Text, BackColor = Theme.Bg, Margin = new Padding(0) };
            r.Value = new Label { AutoSize = true, Anchor = AnchorStyles.Right, TextAlign = ContentAlignment.MiddleRight, Font = Theme.MakeFont(11f, FontStyle.Bold), ForeColor = Theme.Text, BackColor = Theme.Bg, Margin = new Padding(0) };
            head.Controls.Add(name, 0, 0);
            if (spec.Kind != Kind.Choice) head.Controls.Add(r.Value, 1, 0);
            Col.Controls.Add(head);

            if (spec.Kind == Kind.Choice)
            {
                int n = spec.Choices.Length;
                var seg = new TableLayoutPanel { Tag = "fill", ColumnCount = n, RowCount = 1, Height = 48, Width = 400, BackColor = Theme.Bg, Margin = new Padding(0, 0, 0, 2) };
                seg.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                for (int i = 0; i < n; i++)
                {
                    seg.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / n));
                    int value = spec.Lo + i;
                    var b = new ThemedButton { AutoSize = false, Dock = DockStyle.Fill, Margin = new Padding(i == 0 ? 0 : 6, 0, 0, 0), Text = spec.Choices[i] };
                    b.Click += (s, e) => Commit(r, value);
                    r.Segments.Add(b);
                    seg.Controls.Add(b, i, 0);
                }
                Col.Controls.Add(seg);
            }
            else
            {
                var line = new TableLayoutPanel { Tag = "fill", ColumnCount = 3, RowCount = 1, Height = 52, Width = 400, BackColor = Theme.Bg, Margin = new Padding(0, 0, 0, 2) };
                line.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
                line.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                line.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
                line.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                r.Minus = new ThemedButton { AutoSize = false, Dock = DockStyle.Fill, Margin = new Padding(0), Text = "−" };
                r.Plus = new ThemedButton { AutoSize = false, Dock = DockStyle.Fill, Margin = new Padding(0), Text = "+" };
                r.Bar = new YellowTrackBar
                {
                    AutoSize = false,
                    Dock = DockStyle.Fill,
                    BackColor = Theme.Bg,
                    Minimum = 0,
                    Maximum = r.MaxProgress,
                    TickFrequency = Math.Max(1, r.MaxProgress / 10),
                    LargeChange = Math.Max(1, r.MaxProgress / 10),
                    SmallChange = 1,
                    Margin = new Padding(8, 0, 8, 0)
                };
                r.SendTimer = new Timer { Interval = 400 };
                r.SendTimer.Tick += (s, e) =>
                {
                    r.SendTimer.Stop();
                    Commit(r, r.Current);
                };
                r.Bar.ValueChanged += (s, e) =>
                {
                    if (r.Updating) return;
                    r.Current = r.ToValue(r.Bar.Value);
                    r.Value.Text = Display(r, r.Current);
                    r.SendTimer.Stop();
                    r.SendTimer.Start();        //sent when the bar stops moving
                };
                r.Minus.Click += (s, e) => Nudge(r, -1);
                r.Plus.Click += (s, e) => Nudge(r, 1);
                line.Controls.Add(r.Minus, 0, 0);
                line.Controls.Add(r.Bar, 1, 0);
                line.Controls.Add(r.Plus, 2, 0);
                Col.Controls.Add(line);
            }
            if (spec.Note.Length > 0) Col.Controls.Add(Theme.Hint(spec.Note));
        }

        private bool Fahrenheit { get { return values[8] == 1; } }

        private string Temp(int c)
        {
            return Fahrenheit
                ? string.Format(CultureInfo.InvariantCulture, "{0}°F", (int)Math.Round(c * 1.8 + 32.0))
                : c.ToString(CultureInfo.InvariantCulture) + "°C";
        }

        private string Display(Row r, int v)
        {
            var s = r.Spec;
            if (s.Kind == Kind.Choice) return v >= 0 && v < s.Choices.Length ? s.Choices[v] : v.ToString();
            if (s.Kind == Kind.Preset && v == 0) return "OFF";
            if (s.ZeroLabel != null && v == 0) return s.ZeroLabel;
            if (s.Kind == Kind.Preset || s.Kind == Kind.Temp) return Temp(v);
            return v.ToString(CultureInfo.InvariantCulture) + s.Unit;
        }

        private void ShowValue(Row r, int v)
        {
            r.Current = v;
            r.Value.Text = Display(r, v);
            if (r.Bar != null)
            {
                r.Updating = true;
                r.Bar.Value = Math.Max(r.Bar.Minimum, Math.Min(r.Bar.Maximum, r.ToProgress(v)));
                r.Updating = false;
            }
            for (int i = 0; i < r.Segments.Count; i++)
            {
                bool on = r.Spec.Lo + i == v;
                if (r.Segments[i].Strong != on)
                {
                    r.Segments[i].Strong = on;
                    r.Segments[i].Invalidate();
                }
            }
        }

        /// <summary>One step with the - and + buttons; sent when the presses stop.</summary>
        private void Nudge(Row r, int dir)
        {
            int p = Math.Max(0, Math.Min(r.MaxProgress, r.ToProgress(r.Current) + dir));
            ShowValue(r, r.ToValue(p));
            r.SendTimer.Stop();
            r.SendTimer.Start();
        }

        private void Commit(Row r, int value)
        {
            if (!comm.Transport.Connected) return;
            ShowValue(r, value);
            var res = comm.SetParam(r.Spec.Id, value);
            if (res == null)
            {
                SetStatus("No answer from the station.");
                Reload();
            }
            else if (res[0] == 0)
            {
                values[r.Spec.Id] = res[1];
                ShowValue(r, res[1]);
                SetStatus("");
                if (r.Spec.Id == 8) foreach (var o in rows) ShowValue(o, values[o.Spec.Id]);      //C/F: the shown temperatures change
            }
            else if (res[0] == 3)
            {
                MessageBox.Show(this, "The station is in its menu. Leave the menu first.", "Station settings", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Reload();
            }
            else
            {
                MessageBox.Show(this, "The station did not accept that value.", "Station settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Reload();
            }
        }

        private void SaveName()
        {
            int st = comm.SetName(nameBox.Text.TrimEnd());
            string msg;
            if (st == 0) msg = "Name saved.";
            else if (st == 3) msg = "The station is in its menu. Leave the menu first.";
            else if (st < 0) msg = "No answer from the station.";
            else msg = "The station did not accept that name.";
            MessageBox.Show(this, msg, "Station settings", MessageBoxButtons.OK, st == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        private void SetStatus(string t)
        {
            status.Text = t;
            status.Visible = t.Length > 0;
        }

        private void EnableAll(bool on)
        {
            foreach (var r in rows)
            {
                if (r.Bar != null) { r.Bar.Enabled = on; r.Minus.Enabled = on; r.Plus.Enabled = on; }
                foreach (var b in r.Segments) b.Enabled = on;
            }
            nameBox.Enabled = on;
            nameSave.Enabled = on;
        }

        /// <summary>Reads all settings from the station (called when the page is shown).</summary>
        public void Reload()
        {
            if (!comm.Transport.Connected)
            {
                EnableAll(false);
                SetStatus("Not connected.");
                return;
            }
            SetStatus("Reading the settings ...");
            var v = comm.GetParams();
            string name = v != null ? comm.GetName() : null;
            foreach (var c in nameControls) c.Visible = name != null;
            if (name != null) nameBox.Text = name;
            if (v == null)
            {
                SetStatus("No answer from the station. This needs station firmware with protocol 3 or later.");
                EnableAll(false);
                return;
            }
            values = v;
            foreach (var r in rows) ShowValue(r, values[r.Spec.Id]);
            EnableAll(true);
            SetStatus("Changes are applied in the station at once.");
        }

        public void OnDisconnected()
        {
            EnableAll(false);
            SetStatus("Not connected.");
        }
    }
}
