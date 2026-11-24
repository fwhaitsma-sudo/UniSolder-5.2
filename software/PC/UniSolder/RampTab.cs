using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace UniSolder
{
    /// <summary>
    /// Ramp mode of the station (firmware protocol 7 to 10): the station runs idle, step profile and lockout by itself. This page sets the
    /// values and the profile steps, switches the mode on and off, starts or aborts a profile, shows the state and manages the 10 profiles
    /// stored in the station. Same function as the ramp page of the Android app. The computer is only an editor and a viewer.
    /// All commands run on the UI thread (each waits at most a few seconds); the state is polled twice a second while the page is shown.
    /// </summary>
    class RampTab : Page
    {
        private readonly UniSolderComm comm;
        private readonly Label statusText = new Label();
        private readonly Label noteText = new Label();
        private readonly ThemedButton modeButton = new ThemedButton();
        private readonly ThemedButton startButton = new ThemedButton();
        private readonly ThemedButton abortButton = new ThemedButton();
        private readonly ThemedButton saveButton = new ThemedButton();
        private readonly List<TextBox> fields = new List<TextBox>();
        private readonly List<int> fieldPar = new List<int>();          // index in the station's parameter list for each field
        private readonly List<Control> profileControls = new List<Control>();      // protocol 9
        private readonly List<Control> slotControls = new List<Control>();         // protocol 10
        private readonly List<TextBox> stepFields = new List<TextBox>();           // 6 x (temperature, rate, hold)
        private readonly TextBox stepCount = new TextBox();
        private readonly FlowLayoutPanel slotList = new FlowLayoutPanel();
        private readonly HashSet<int> tenths = new HashSet<int>();      // fields shown in units of 0.1 (start delay, maximum rate)
        private readonly Timer pollTimer = new Timer { Interval = 500 };
        private int[] pars = new int[UniSolderComm.RAMP_PARS9];
        private int protocol;
        private int activeSlot = -1;
        private bool inMode, parsLoaded;

        /// <summary>The user wants to leave the page (Back button).</summary>
        public event Action Back;

        public RampTab(UniSolderComm comm)
        {
            this.comm = comm;
            var col = Col;

            var back = new ThemedButton();
            Theme.Style(back, "‹ Back", 110);
            back.Margin = new Padding(0, 14, 0, 0);
            back.Click += (s, e) => Back?.Invoke();
            col.Controls.Add(back);
            col.Controls.Add(Theme.Heading("Ramp mode"));
            col.Controls.Add(Theme.Body("In ramp mode the station holds the idle temperature and runs the soldering profile by itself. " +
                "Lift the iron from its stand: the profile starts after the start delay (like JBC's auto-start). " +
                "A short press of the knob or Start below also starts it; a press while it runs aborts, and so does putting the iron back in its stand. " +
                "The knob, sleep and stand-by do not change the temperature in this mode. After a power cycle the station is a normal station again."));

            statusText.AutoSize = true;
            statusText.Font = Theme.MakeFont(13f, FontStyle.Bold);
            statusText.ForeColor = Theme.Yellow;
            statusText.BackColor = Theme.Bg;
            statusText.Margin = new Padding(0, 10, 0, 4);
            col.Controls.Add(statusText);
            noteText.AutoSize = true;
            noteText.Font = Theme.MakeFont(10f);
            noteText.ForeColor = Theme.Text;
            noteText.BackColor = Theme.Bg;
            col.Controls.Add(noteText);

            Theme.Style(modeButton, "Enter ramp mode", 220);
            modeButton.Strong = true;
            modeButton.Click += (s, e) => Control(inMode ? 0 : 1);
            Theme.Style(startButton, "Start ramp", 150);
            startButton.Click += (s, e) => Control(2);
            Theme.Style(abortButton, "Abort", 130);
            abortButton.Click += (s, e) => Control(3);
            col.Controls.Add(Theme.Row(modeButton, startButton, abortButton));

            col.Controls.Add(Theme.Heading("Ramp values"));
            AddField(null, "Idle temperature (°C)", 0, "100-340, default 165 (below the melting point of Sn62 solder, 179). Held until the profile starts, also while the iron is in its stand.", false);
            AddField(null, "Lockout after the profile (seconds)", 4, "0-60, default 5, counted from the end of the profile. No new profile can start in this time.", false);
            AddField(null, "Ready up to (°C)", 8, "Default 200. At least 10 above idle and at most 10 below the highest step. A profile can start when the tip is not hotter than this (the station shows WAIT until then), and then starts from the tip's temperature.", false);
            AddField(null, "Time-out when idle (minutes)", 5, "1-60. After this time without use the station leaves ramp mode.", false);

            // ---- profile (protocol 9) ----
            AddTo(profileControls, Theme.Heading("Start and profile"));
            AddField(profileControls, "Start (0 or 1)", 12, "1 = lift the iron from its stand, the profile starts after the start delay (default; uses the stand switch, the same one that shows HLD). 0 = knob or app only.", false);
            AddField(profileControls, "Start delay after lifting (seconds)", 13, "0-10, default 3, one decimal. Time to wipe the tip and put it on the pad.", true);
            AddField(profileControls, "Maximum rate (°C per second)", 14, "0.5-100, default 4, one decimal. No step may ramp faster than this.", true);
            AddTo(profileControls, Theme.Hint("Each step ramps from the previous step's temperature to its own at its rate, then holds it. After the last step the heater goes back to idle. " +
                "Default (tin-lead, Kyocera AVX TCJ D-case): step 1 190 °C at 2 °C/s, hold 20 s; step 2 250 °C at 2 °C/s, hold 5 s (about 68 s in all)."));
            AddTo(profileControls, Theme.Body("Number of steps (1-6)"));
            StyleBox(stepCount, 100);
            AddTo(profileControls, stepCount);
            var table = new TableLayoutPanel { Tag = "fill", ColumnCount = 4, RowCount = 7, Width = 400, BackColor = Theme.Bg, Margin = new Padding(0, 10, 0, 0) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 16));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
            for (int i = 0; i < 7; i++) table.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            table.Height = 7 * 40;
            string[] heads = { "Step", "°C", "°C/s", "Hold s" };
            for (int j = 0; j < 4; j++)
                table.Controls.Add(new Label { Text = heads[j], AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Muted, BackColor = Theme.Bg, Font = Theme.MakeFont(10f), Margin = new Padding(0) }, j, 0);
            for (int k = 0; k < UniSolderComm.RAMP_STEPS; k++)
            {
                table.Controls.Add(new Label { Text = (k + 1).ToString(), AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Text, BackColor = Theme.Bg, Font = Theme.MakeFont(10f), Margin = new Padding(0) }, 0, k + 1);
                for (int j = 0; j < 3; j++)
                {
                    var t = new TextBox();
                    StyleBox(t, 0);
                    t.Dock = DockStyle.Fill;
                    t.Margin = new Padding(0, 4, 8, 4);
                    stepFields.Add(t);
                    table.Controls.Add(t, j + 1, k + 1);
                }
            }
            AddTo(profileControls, table);

            // ---- stored profiles (protocol 10) ----
            AddTo(slotControls, Theme.Heading("Profiles stored in the station"));
            AddTo(slotControls, Theme.Hint("Up to 10 profiles, kept in the station. Load makes one the active profile (the steps above). " +
                "Save here stores the steps shown above (also when they are not saved in the station yet) under a name."));
            slotList.FlowDirection = FlowDirection.TopDown;
            slotList.WrapContents = false;
            slotList.AutoSize = true;
            slotList.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            slotList.BackColor = Theme.Bg;
            slotList.Margin = new Padding(0);
            AddTo(slotControls, slotList);

            Theme.Style(saveButton, "Save values in the station", 280);
            saveButton.Strong = true;
            saveButton.Margin = new Padding(0, 16, 0, 6);
            saveButton.Click += (s, e) => SaveValues();
            col.Controls.Add(saveButton);

            pollTimer.Tick += (s, e) => PollStatus();
            VisibleChanged += (s, e) =>
            {
                if (Visible) OnShown();
                else pollTimer.Stop();
            };
            RefreshButtons();
        }

        private void AddTo(List<Control> list, Control c)
        {
            Col.Controls.Add(c);
            list?.Add(c);
        }

        private static void StyleBox(TextBox t, int width)
        {
            t.BackColor = Theme.Field;
            t.ForeColor = Theme.Text;
            t.BorderStyle = BorderStyle.FixedSingle;
            t.Font = Theme.MakeFont(11f);
            if (width > 0) t.Width = width;
            t.Margin = new Padding(0, 0, 0, 6);
        }

        private void AddField(List<Control> list, string label, int index, string hint, bool decimalValue)
        {
            AddTo(list, Theme.Body(label));
            var t = new TextBox();
            StyleBox(t, 200);
            if (decimalValue) tenths.Add(index);
            fields.Add(t);
            fieldPar.Add(index);
            if (list != null) list.Add(t);
            Col.Controls.Add(t);
            AddTo(list, Theme.Hint(hint));
        }

        // ------------------------------------------------------------------ state

        private bool Supported { get { return comm.Transport.Connected && protocol >= 7; } }

        private void RefreshButtons()
        {
            bool ok = Supported;
            modeButton.Enabled = ok;
            modeButton.Text = inMode ? "Leave ramp mode" : "Enter ramp mode";
            startButton.Enabled = ok && inMode;
            abortButton.Enabled = ok && inMode;
            saveButton.Enabled = ok;
            foreach (var f in fields) f.Enabled = ok;
            bool p9 = comm.Transport.Connected && protocol >= 9;
            foreach (var c in profileControls) c.Visible = p9;
            stepCount.Enabled = p9;
            foreach (var f in stepFields) f.Enabled = p9;
            bool p10 = comm.Transport.Connected && protocol >= 10;
            foreach (var c in slotControls) c.Visible = p10;
            if (!comm.Transport.Connected) noteText.Text = "Not connected.";
            else if (protocol < 7) noteText.Text = "This station's firmware has no ramp mode (it needs the ramp mode firmware, protocol 7).";
            Fit();
        }

        /// <summary>The page is shown: read the protocol and the values, then poll the state while it stays visible.</summary>
        private void OnShown()
        {
            var info = comm.Transport.Connected ? comm.GetAppInfo() : null;
            protocol = info != null ? info.Protocol : 0;
            RefreshButtons();
            if (Supported) LoadPars();
            pollTimer.Start();
        }

        public void OnDisconnected()
        {
            inMode = false;
            parsLoaded = false;
            protocol = 0;
            statusText.Text = "";
            RefreshButtons();
        }

        private void PollStatus()
        {
            if (!Visible || !Supported) return;
            var s = comm.GetRampStatus();
            if (s != null) ShowStatus(s);
        }

        private static string Tenths(int v)
        {
            return (v / 10.0).ToString("0.0", CultureInfo.InvariantCulture);
        }

        private void LoadPars()
        {
            int n = protocol >= 9 ? UniSolderComm.RAMP_PARS9 : protocol >= 8 ? UniSolderComm.RAMP_PARS8 : UniSolderComm.RAMP_PARS;
            var p = comm.GetRampPars(n);
            var steps = protocol >= 9 ? comm.GetRampSteps(protocol >= 10) : null;
            var slots = protocol >= 10 ? ReadSlots() : null;
            var s = comm.GetRampStatus();
            if (p != null)
            {
                pars = p;
                parsLoaded = true;
                for (int i = 0; i < fields.Count; i++)
                {
                    int k = fieldPar[i];
                    if (k >= p.Length) continue;
                    fields[i].Text = tenths.Contains(k) ? Tenths(p[k]) : p[k].ToString(CultureInfo.InvariantCulture);
                }
            }
            if (steps != null)
            {
                ShowSteps(steps.Count, steps.Steps);
                activeSlot = steps.Slot;
            }
            if (slots != null) ShowSlots(slots);
            if (s != null) ShowStatus(s);
        }

        private bool ParseInt(string text, out int v)
        {
            return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
        }

        private bool ParseTenths(string text, out int v)
        {
            double d;
            bool ok = double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out d);
            v = ok ? (int)Math.Round(d * 10) : 0;
            return ok;
        }

        private void SaveValues()
        {
            if (!parsLoaded)
            {
                MessageBox.Show(this, "Values not loaded yet.", "Ramp mode", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var v = (int[])pars.Clone();
            for (int i = 0; i < fields.Count; i++)
            {
                int k = fieldPar[i];
                if (k >= v.Length || !fields[i].Visible) continue;
                int x;
                bool ok = tenths.Contains(k) ? ParseTenths(fields[i].Text, out x) : ParseInt(fields[i].Text, out x);
                if (!ok)
                {
                    MessageBox.Show(this, "Check the values.", "Ramp mode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                v[k] = x;
            }
            int count = 0;
            int[] steps = null;
            if (protocol >= 9)
            {
                var e = EditorSteps();
                if (e == null) return;
                count = e.Item1;
                steps = e.Item2;
            }
            // the station checks the values against the steps and the steps against the values (maximum rate, ready limit):
            // when one order is refused, the other one is tried
            int st = comm.SetRampPars(v);
            int st1 = st, st2 = -9;
            if (steps != null)
            {
                if (st == 0) { st = comm.SetRampSteps(count, steps); st2 = st; }
                else if (st == 2)
                {
                    st = comm.SetRampSteps(count, steps); st2 = st;
                    if (st == 0) st = comm.SetRampPars(v);
                }
            }
            var back = st == 0 ? comm.GetRampPars(v.Length) : null;
            var backSteps = st == 0 && steps != null ? comm.GetRampSteps(protocol >= 10) : null;
            SSComm.Log.Info("Ramp save: values " + string.Join(",", v) + " steps " + count + ":" + (steps != null ? string.Join(",", steps) : "-") +
                " | status values " + st1 + ", steps " + st2 + ", final " + st +
                " | read back values " + (back != null ? string.Join(",", back) : "-") + " steps " + (backSteps != null ? backSteps.Count + ":" + string.Join(",", backSteps.Steps) : "-"));
            if (back != null && !back.SequenceEqual(v)) SSComm.Log.Warn("Ramp save: the station reports other values than were sent");
            if (backSteps != null && steps != null && !backSteps.Steps.SequenceEqual(steps)) SSComm.Log.Warn("Ramp save: the station reports other steps than were sent");
            var slots = st == 0 && protocol >= 10 ? ReadSlots() : null;
            if (st == 0)
            {
                if (back != null) pars = back;
                if (backSteps != null)
                {
                    ShowSteps(backSteps.Count, backSteps.Steps);
                    activeSlot = backSteps.Slot;
                }
                if (slots != null) ShowSlots(slots);
                MessageBox.Show(this, "Saved in the station.", "Ramp mode", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else if (st == 2)
            {
                MessageBox.Show(this, steps != null
                    ? "A value is out of range: steps 100-350 °C, rate up to the maximum rate, hold up to 600 s; the highest step at least 10 above idle; ready limit between idle + 10 and the highest step - 10."
                    : "A value is out of range (peak at least 10 above idle; ready limit between idle + 10 and peak - 10).", "Ramp mode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else if (st == 3) MessageBox.Show(this, "A profile is running: wait until it is finished.", "Ramp mode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else if (st < 0) MessageBox.Show(this, "No answer from the station: nothing saved.", "Ramp mode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else MessageBox.Show(this, "The station did not accept the values (status " + st + ").", "Ramp mode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        /// <summary>The steps in the editor: (number of steps, 6 x 3 values), or null (with a message) when a field is not a number.</summary>
        private Tuple<int, int[]> EditorSteps()
        {
            int count;
            var a = new int[UniSolderComm.RAMP_STEPS * 3];
            bool ok = ParseInt(stepCount.Text, out count);
            for (int i = 0; ok && i < stepFields.Count; i++)
            {
                int x;
                ok = i % 3 == 0 ? ParseInt(stepFields[i].Text, out x) : ParseTenths(stepFields[i].Text, out x);
                a[i] = x;
            }
            if (!ok)
            {
                MessageBox.Show(this, "Check the step values.", "Ramp mode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
            return Tuple.Create(count, a);
        }

        private List<UniSolderComm.RampSlot> ReadSlots()
        {
            var l = new List<UniSolderComm.RampSlot>();
            for (int i = 0; i < UniSolderComm.RAMP_SLOTS; i++)
            {
                var s = comm.RampSlotRead(i);
                if (s == null) return null;
                l.Add(s);
            }
            return l;
        }

        private static string StepSummary(int count, int[] s)
        {
            var sb = new StringBuilder();
            for (int k = 0; k < count && k < UniSolderComm.RAMP_STEPS; k++)
            {
                if (k > 0) sb.Append(" → ");
                sb.Append(s[k * 3]).Append("°C");
                if (s[k * 3 + 2] > 0) sb.Append(" ").Append((s[k * 3 + 2] / 10.0).ToString("0", CultureInfo.InvariantCulture)).Append("s");
            }
            return sb.ToString();
        }

        private void ShowSlots(List<UniSolderComm.RampSlot> slots)
        {
            slotList.SuspendLayout();
            while (slotList.Controls.Count > 0)
            {
                var c = slotList.Controls[0];
                slotList.Controls.RemoveAt(0);
                c.Dispose();
            }
            foreach (var sl0 in slots)
            {
                var sl = sl0;
                string head = (sl.Slot + 1) + ". " + (sl.Count == 0 ? "(empty)" : (sl.Name.Length == 0 ? "(no name)" : sl.Name)) +
                    (sl.Slot == activeSlot && sl.Count > 0 ? "   — active" : "");
                var label = new Label
                {
                    AutoSize = true,
                    Text = sl.Count == 0 ? head : head + "\r\n" + StepSummary(sl.Count, sl.Steps),
                    Font = Theme.MakeFont(10f),
                    ForeColor = Theme.Text,
                    BackColor = Theme.Bg,
                    Margin = new Padding(0, 12, 0, 0),
                    MaximumSize = new Size(560, 0)
                };
                var load = new ThemedButton();
                Theme.Style(load, "Load", 90, 40);
                load.Enabled = sl.Count > 0;
                load.Click += (s, e) => LoadSlot(sl);
                var saveHere = new ThemedButton();
                Theme.Style(saveHere, "Save here", 110, 40);
                saveHere.Click += (s, e) => AskSaveSlot(sl);
                var del = new ThemedButton();
                Theme.Style(del, "Delete", 90, 40);
                del.Enabled = sl.Count > 0;
                del.Click += (s, e) => ConfirmDeleteSlot(sl);
                slotList.Controls.Add(label);
                slotList.Controls.Add(Theme.Row(load, saveHere, del));
            }
            slotList.ResumeLayout(true);
            Fit();
        }

        private void RefreshSlotsAndSteps(string msg)
        {
            var steps = comm.GetRampSteps(true);
            var slots = ReadSlots();
            if (steps != null)
            {
                ShowSteps(steps.Count, steps.Steps);
                activeSlot = steps.Slot;
            }
            if (slots != null) ShowSlots(slots);
            if (msg != null) MessageBox.Show(this, msg, "Ramp mode", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void LoadSlot(UniSolderComm.RampSlot sl)
        {
            int r = comm.RampSlotLoad(sl.Slot);
            switch (r)
            {
                case 0: RefreshSlotsAndSteps("Loaded: " + (sl.Name.Length == 0 ? "profile " + (sl.Slot + 1) : sl.Name) + "."); break;
                case 1: MessageBox.Show(this, "This slot is empty.", "Ramp mode", MessageBoxButtons.OK, MessageBoxIcon.Information); break;
                case 2: MessageBox.Show(this, "Not possible with the station's values: a step is faster than the maximum rate, or the highest step is not at least 10 above idle and the ready limit + 10.", "Ramp mode", MessageBoxButtons.OK, MessageBoxIcon.Warning); break;
                case 3: MessageBox.Show(this, "A profile is running: wait until it is finished.", "Ramp mode", MessageBoxButtons.OK, MessageBoxIcon.Warning); break;
                default: MessageBox.Show(this, "No answer from the station.", "Ramp mode", MessageBoxButtons.OK, MessageBoxIcon.Warning); break;
            }
        }

        private void AskSaveSlot(UniSolderComm.RampSlot sl)
        {
            var e = EditorSteps();
            if (e == null) return;
            string title = "Save the steps above in slot " + (sl.Slot + 1);
            using (var dlg = new PromptForm(title, "Name (up to 10 characters)", sl.Name, UniSolderComm.RAMP_NAME_LEN))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string name = dlg.Value.Trim();
                if (name.Any(ch => ch < 32 || ch > 126))
                {
                    MessageBox.Show(this, "Use letters, digits and plain punctuation only (no accents).", "Ramp mode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                int r = comm.RampSlotWrite(sl.Slot, name, e.Item1, e.Item2);
                var chk = r == 0 ? comm.RampSlotRead(sl.Slot) : null;
                bool same = chk != null && chk.Count == e.Item1 && chk.Name == name;
                for (int i = 0; same && i < e.Item1 * 3; i++) if (chk.Steps[i] != e.Item2[i]) same = false;
                SSComm.Log.Info("Ramp slot " + (sl.Slot + 1) + " save: sent '" + name + "' " + e.Item1 + ":" + string.Join(",", e.Item2) +
                    " | status " + r + " | read back " + (chk == null ? "-" : "'" + chk.Name + "' " + chk.Count + ":" + string.Join(",", chk.Steps)) + " | same " + same);
                if (r == 0 && !same)
                {
                    MessageBox.Show(this, "The station said OK but slot " + (sl.Slot + 1) + " holds other values: not stored.", "Ramp mode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    RefreshSlotsAndSteps(null);
                }
                else if (r == 0)
                {
                    // the steps just stored become the active profile, so the editor keeps showing them
                    int ld = comm.RampSlotLoad(sl.Slot);
                    SSComm.Log.Info("Ramp slot " + (sl.Slot + 1) + " made active: status " + ld);
                    var slots = ReadSlots();
                    var steps = ld == 0 ? comm.GetRampSteps(true) : null;
                    if (steps != null)
                    {
                        ShowSteps(steps.Count, steps.Steps);
                        activeSlot = steps.Slot;
                    }
                    if (slots != null) ShowSlots(slots);
                    string msg = ld == 0 ? "Saved in slot " + (sl.Slot + 1) + " and active."
                        : ld == 3 ? "Saved in slot " + (sl.Slot + 1) + ", but a profile is running: not active yet."
                        : "Saved in slot " + (sl.Slot + 1) + ", but not valid with the station's values (maximum rate, idle, ready limit): not active.";
                    MessageBox.Show(this, msg, "Ramp mode", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show(this, r == 2 ? "A step is out of range (1-6 steps, 100-350 °C, rate 0.1-100 °C/s, hold up to 600 s)." : "The station did not accept it.",
                        "Ramp mode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void ConfirmDeleteSlot(UniSolderComm.RampSlot sl)
        {
            string name = sl.Name.Length == 0 ? "profile " + (sl.Slot + 1) : sl.Name;
            if (MessageBox.Show(this, "The active profile (the steps above) does not change.", "Delete " + name + "?", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            int r = comm.RampSlotDelete(sl.Slot);
            if (r == 0) RefreshSlotsAndSteps(null);
            else MessageBox.Show(this, "The station did not delete it.", "Ramp mode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void ShowSteps(int count, int[] s)
        {
            stepCount.Text = count.ToString(CultureInfo.InvariantCulture);
            for (int i = 0; i < stepFields.Count; i++)
            {
                stepFields[i].Text = i % 3 == 0 ? s[i].ToString(CultureInfo.InvariantCulture) : Tenths(s[i]);
                stepFields[i].ForeColor = i / 3 < count ? Theme.Text : Theme.Muted;
            }
        }

        private void Control(int cmd)
        {
            var r = comm.RampControl(cmd);
            var s = comm.GetRampStatus();
            if (r == null) MessageBox.Show(this, "No answer from the station.", "Ramp mode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else if (r[0] == 1) MessageBox.Show(this, cmd == 1 ? "Not possible now (two-iron adapter, no instrument or station asleep)." : "Not possible now.", "Ramp mode", MessageBoxButtons.OK, MessageBoxIcon.Information);
            else if (r[0] == 3) MessageBox.Show(this, "The station is in its menu.", "Ramp mode", MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (s != null) ShowStatus(s);
        }

        private void ShowStatus(UniSolderComm.RampStatus s)
        {
            inMode = s.State != 0;
            RefreshButtons();
            string name;
            switch (s.State)
            {
                case 0: name = "Off (normal station)"; break;
                case 1:
                    if (s.Lift == 1) name = "Starting in " + (s.LiftMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s";
                    else if (s.Lift == 2) name = "Waiting: the tip is above the ready limit";
                    else if (s.Ready) name = protocol >= 9 && pars.Length > 12 && pars[12] == 1 ? "Ready: lift the iron to start" : "Ready";
                    else if (pars.Length > 8 && s.Actual > pars[8]) name = "Cooling: waiting for the ready limit";
                    else name = "Heating to the idle temperature";
                    break;
                case 2: name = s.Step > 0 ? "Step " + s.Step + ": ramping" : "Ramping"; break;
                case 3: name = s.Step > 0 ? "Step " + s.Step + ": holding" : "Peak"; break;
                case 4:
                case 5: name = "Lockout"; break;
                default: name = "?"; break;
            }
            statusText.Text = name;
            var sb = new StringBuilder();
            if (s.State != 0)
            {
                sb.Append("Target ").Append(s.Target).Append("°C, tip ").Append(s.Actual).Append("°C");
                if (s.MsLeft > 0 && s.State >= 2) sb.Append(", ").Append((s.MsLeft / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)).Append(" s left");
                if (s.Highest > 0) sb.Append("\r\nHighest temperature since the start: ").Append(s.Highest).Append("°C");
            }
            noteText.Text = sb.ToString();
        }
    }
}
