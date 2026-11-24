using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace UniSolder
{
    /// <summary>
    /// Control tab: set temperature, presets, sleep, switch iron, station info and settings backup/restore.
    /// Same function as the Control tab of the Android app. All commands run on the UI thread (each waits at most a second).
    /// </summary>
    class ControlTab : Page
    {
        private readonly UniSolderComm comm;
        private readonly Label stateLabel = new Label();
        private readonly Label tempLabel = new Label();
        private readonly Label infoLabel = new Label();
        private readonly YellowTrackBar tempBar = new YellowTrackBar();
        private readonly ThemedButton minusButton = new ThemedButton();
        private readonly ThemedButton plusButton = new ThemedButton();
        private readonly ThemedButton[] presetButtons = new ThemedButton[4];
        private readonly ThemedButton sleepButton = new ThemedButton();
        private readonly ThemedButton ironButton = new ThemedButton();
        private readonly ThemedButton saveButton = new ThemedButton();
        private readonly ThemedButton restoreButton = new ThemedButton();
        private readonly Timer pollTimer = new Timer();
        private readonly Timer sendTimer = new Timer();

        /// <summary>The user wants the station settings page.</summary>
        public event Action OpenSettings;
        public event Action OpenRamp;

        private UniSolderComm.AppInfo info;
        private UniSolderComm.DeviceState state;
        private int pendingTemp = -1;
        private DateTime holdUntil = DateTime.MinValue;
        private bool updating;

        public ControlTab(UniSolderComm comm)
        {
            this.comm = comm;
            var col = Col;

            stateLabel.AutoSize = true;
            stateLabel.Font = Theme.MakeFont(12f, FontStyle.Bold);
            stateLabel.ForeColor = Theme.Yellow;
            stateLabel.BackColor = Theme.Bg;
            stateLabel.Margin = new Padding(0, 14, 0, 0);
            stateLabel.Text = "Not connected";
            col.Controls.Add(stateLabel);

            // temperature
            col.Controls.Add(Theme.Heading("Temperature"));
            tempLabel.AutoSize = true;
            tempLabel.Font = Theme.MakeFont(30f, FontStyle.Bold);
            tempLabel.ForeColor = Theme.Text;
            tempLabel.BackColor = Theme.Bg;
            tempLabel.Margin = new Padding(0, 0, 0, 8);
            tempLabel.Text = "-";
            col.Controls.Add(tempLabel);

            tempBar.AutoSize = false;
            tempBar.Dock = DockStyle.Fill;
            tempBar.BackColor = Theme.Bg;
            tempBar.TickFrequency = 10;
            tempBar.LargeChange = 10;
            tempBar.SmallChange = 1;
            tempBar.Minimum = 0;
            tempBar.Maximum = 100;
            tempBar.Margin = new Padding(8, 0, 8, 0);
            tempBar.ValueChanged += TempBar_ValueChanged;
            Theme.Style(minusButton, "\u2212", 56, 52);
            Theme.Style(plusButton, "+", 56, 52);
            minusButton.Dock = DockStyle.Fill;
            plusButton.Dock = DockStyle.Fill;
            minusButton.Margin = new Padding(0);
            plusButton.Margin = new Padding(0);
            minusButton.Click += delegate { StepTemp(-1); };
            plusButton.Click += delegate { StepTemp(1); };
            var barRow = new TableLayoutPanel();
            barRow.Tag = "fill";
            barRow.ColumnCount = 3;
            barRow.RowCount = 1;
            barRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
            barRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            barRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
            barRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            barRow.Height = 56;
            barRow.Width = 400;
            barRow.Margin = new Padding(0, 0, 0, 6);
            barRow.BackColor = Theme.Bg;
            barRow.Controls.Add(minusButton, 0, 0);
            barRow.Controls.Add(tempBar, 1, 0);
            barRow.Controls.Add(plusButton, 2, 0);
            col.Controls.Add(barRow);

            // presets
            col.Controls.Add(Theme.Heading("Presets"));
            var presetRow = Theme.Row();
            for (int i = 0; i < 4; i++)
            {
                int index = i;
                presetButtons[i] = new ThemedButton();
                Theme.Style(presetButtons[i], "T" + (i + 1), 130, 56);
                presetButtons[i].Click += delegate { Preset(index); };
                presetRow.Controls.Add(presetButtons[i]);
            }
            col.Controls.Add(presetRow);

            // station
            col.Controls.Add(Theme.Heading("Station"));
            Theme.Style(sleepButton, "Sleep", 140);
            sleepButton.Click += Sleep_Click;
            Theme.Style(ironButton, "Switch iron", 280);
            ironButton.Click += Iron_Click;
            ironButton.Visible = false;
            var settingsButton = new ThemedButton();
            Theme.Style(settingsButton, "Station settings", 190);
            settingsButton.Click += (s, e) => OpenSettings?.Invoke();
            var rampButton = new ThemedButton();
            Theme.Style(rampButton, "Ramp mode", 150);
            rampButton.Click += (s, e) => OpenRamp?.Invoke();
            col.Controls.Add(Theme.Row(sleepButton, ironButton, settingsButton, rampButton));

            // settings
            col.Controls.Add(Theme.Heading("Settings backup"));
            col.Controls.Add(Theme.Hint("Saves the station settings (not the pincode), the saved PID values and the ramp mode values, active profile and stored profiles to a file, or writes them back from a file. The station restarts after a restore. The file is the same format as the Android app uses."));
            Theme.Style(saveButton, "Save settings...", 190);
            Theme.Style(restoreButton, "Restore settings...", 210);
            saveButton.Click += Save_Click;
            restoreButton.Click += Restore_Click;
            col.Controls.Add(Theme.Row(saveButton, restoreButton));

            infoLabel.AutoSize = true;
            infoLabel.Font = Theme.MakeFont(9f);
            infoLabel.ForeColor = Theme.Muted;
            infoLabel.BackColor = Theme.Bg;
            infoLabel.Margin = new Padding(0, 20, 0, 0);
            col.Controls.Add(infoLabel);

            EnableControls(false);

            pollTimer.Interval = 1000;
            pollTimer.Tick += delegate { Poll(); };
            sendTimer.Interval = 250;
            sendTimer.Tick += SendTimer_Tick;
            pollTimer.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                pollTimer.Dispose();
                sendTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---------------------------------------------------------------- state

        private string Degrees(int c, UniSolderComm.DeviceState s)
        {
            if (s.Fahrenheit) return Math.Round(c * 1.8 + 32.0).ToString("0", CultureInfo.InvariantCulture) + " °F";
            return c.ToString(CultureInfo.InvariantCulture) + " °C";
        }

        private void EnableControls(bool on)
        {
            var s = state;
            bool canSet = on && s != null && !s.InMenu;
            tempBar.Enabled = canSet;
            minusButton.Enabled = canSet;
            plusButton.Enabled = canSet;
            for (int i = 0; i < 4; i++) presetButtons[i].Enabled = canSet && s != null && s.Presets[i] != 0;
            sleepButton.Enabled = on && s != null && !s.InMenu;
            ironButton.Enabled = on && s != null && !s.InMenu;
            saveButton.Enabled = on;
            restoreButton.Enabled = on;
        }

        /// <summary>Called every second by the timer; reads the state while the tab is shown.</summary>
        public void Poll()
        {
            if (!Visible) return;
            if (!comm.Transport.Connected)
            {
                state = null;
                info = null;
                stateLabel.Text = "Not connected";
                infoLabel.Text = "";
                EnableControls(false);
                return;
            }
            if (info == null)
            {
                info = comm.GetAppInfo();
                if (info == null)
                {
                    stateLabel.Text = "The station does not answer (bootloader mode?)";
                    EnableControls(false);
                    return;
                }
                infoLabel.Text = info.Protocol < 1
                    ? "This station firmware has no remote control. Update the firmware on the Firmware tab."
                    : "Station firmware " + info.Major + "." + info.Minor + ", board " + info.BoardName + ", remote control v" + info.Protocol;
            }
            if (info.Protocol < 1)
            {
                stateLabel.Text = "No remote control in this firmware";
                EnableControls(false);
                return;
            }
            var s = comm.GetState();
            if (s == null)
            {
                stateLabel.Text = "The station did not answer";
                EnableControls(false);
                return;
            }
            ApplyState(s);
        }

        private void ApplyState(UniSolderComm.DeviceState s)
        {
            bool first = state == null;
            state = s;
            EnableControls(true);
            updating = true;
            try
            {
                int range = Math.Max(1, s.Max - s.Min);
                tempBar.Minimum = 0;
                tempBar.Maximum = range;
                tempBar.TickFrequency = Math.Max(1, range / 10);
                if (pendingTemp < 0 && (first || DateTime.Now > holdUntil))
                {
                    tempBar.Value = Math.Max(0, Math.Min(range, s.Set - s.Min));
                    tempLabel.Text = Degrees(s.Set, s);
                }
            }
            finally
            {
                updating = false;
            }
            for (int i = 0; i < 4; i++)
                presetButtons[i].Text = "T" + (i + 1) + "  " + (s.Presets[i] == 0 ? "OFF" : Degrees(s.Presets[i], s));
            sleepButton.Text = s.Standby ? "Wake up" : "Sleep";
            ironButton.Visible = s.TwoIrons;
            ironButton.Text = "Switch iron (now iron " + (s.ActIron + 1) + ")";
            if (s.InMenu) stateLabel.Text = "The station is in its menu: remote changes wait until it is closed.";
            else if (s.Standby) stateLabel.Text = "Sleeping: heater off.";
            else if (s.Target == 0) stateLabel.Text = "Heater off.";
            else stateLabel.Text = "Regulating to " + Degrees(s.Target, s) + ".";
        }

        // ---------------------------------------------------------------- commands

        private void TempBar_ValueChanged(object sender, EventArgs e)
        {
            if (updating || state == null) return;
            QueueTemp(state.Min + tempBar.Value);
        }

        private void StepTemp(int dir)
        {
            var s = state;
            if (s == null) return;
            int t = Math.Max(s.Min, Math.Min(s.Max, s.Set + dir * s.TempStep));
            if (pendingTemp >= 0) t = Math.Max(s.Min, Math.Min(s.Max, pendingTemp + dir * s.TempStep));
            updating = true;
            try { tempBar.Value = Math.Max(0, Math.Min(tempBar.Maximum, t - s.Min)); }
            finally { updating = false; }
            QueueTemp(t);
        }

        // sends the temperature a quarter second after the last change, so dragging does not flood the station
        private void QueueTemp(int t)
        {
            pendingTemp = t;
            holdUntil = DateTime.Now.AddSeconds(2);
            if (state != null) tempLabel.Text = Degrees(t, state);
            sendTimer.Stop();
            sendTimer.Start();
        }

        private void SendTimer_Tick(object sender, EventArgs e)
        {
            sendTimer.Stop();
            int t = pendingTemp;
            if (t < 0) return;
            pendingTemp = -1;
            holdUntil = DateTime.Now.AddSeconds(2);
            int r = comm.SetTemperature(t);
            if (r < 0) { stateLabel.Text = "The station did not answer"; return; }
            var s = comm.GetState();
            if (s != null)
            {
                holdUntil = DateTime.MinValue;
                ApplyState(s);
            }
        }

        private void Preset(int index)
        {
            int r = comm.SelectPreset(index);
            if (r < 0) { stateLabel.Text = "The station did not answer"; return; }
            var s = comm.GetState();
            if (s != null) { holdUntil = DateTime.MinValue; ApplyState(s); }
        }

        private void Sleep_Click(object sender, EventArgs e)
        {
            if (state == null) return;
            int r = comm.Sleep(!state.Standby);
            if (r < 0) { stateLabel.Text = "The station did not answer"; return; }
            var s = comm.GetState();
            if (s != null) ApplyState(s);
        }

        private void Iron_Click(object sender, EventArgs e)
        {
            int r = comm.SwitchIron();
            if (r < 0) { stateLabel.Text = "The station did not answer"; return; }
            var s = comm.GetState();
            if (s != null) { holdUntil = DateTime.MinValue; ApplyState(s); }
        }

        /// <summary>Ramp mode values, active steps and stored profiles; null when the station has no step profiles (protocol below 9).</summary>
        private RampBackup ReadRamp(int protocol)
        {
            if (protocol < 9) return null;
            var p = comm.GetRampPars(UniSolderComm.RAMP_PARS9);
            var st = comm.GetRampSteps(protocol >= 10);
            if (p == null || st == null) return null;
            List<UniSolderComm.RampSlot> slots = null;
            if (protocol >= 10)
            {
                slots = new List<UniSolderComm.RampSlot>();
                for (int k = 0; k < UniSolderComm.RAMP_SLOTS; k++)
                {
                    var sl = comm.RampSlotRead(k);
                    if (sl == null) return null;
                    slots.Add(sl);
                }
            }
            return new RampBackup { Pars = p, Count = st.Count, Steps = st.Steps, Active = st.Slot, Slots = slots };
        }

        /// <summary>Writes the ramp part of a backup. The station checks values and steps against each other, so when one order is refused the other is tried.</summary>
        private bool WriteRamp(RampBackup r, int protocol)
        {
            if (protocol < 9) return true;
            int st = comm.SetRampPars(r.Pars);
            if (st == 0) st = comm.SetRampSteps(r.Count, r.Steps);
            else if (st == 2)
            {
                st = comm.SetRampSteps(r.Count, r.Steps);
                if (st == 0) st = comm.SetRampPars(r.Pars);
            }
            if (st != 0) return false;
            if (protocol >= 10 && r.Slots != null)
            {
                for (int k = 0; k < UniSolderComm.RAMP_SLOTS; k++)
                {
                    UniSolderComm.RampSlot sl = null;
                    foreach (var x in r.Slots) if (x.Slot == k && x.Count > 0) sl = x;
                    int res = sl != null ? comm.RampSlotWrite(k, sl.Name, sl.Count, sl.Steps) : comm.RampSlotDelete(k);
                    if (res != 0) return false;
                }
                if (r.Active >= 0 && comm.RampSlotLoad(r.Active) != 0) return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- settings backup

        private void Save_Click(object sender, EventArgs e)
        {
            var data = comm.ReadSettings();
            if (data == null)
            {
                MessageBox.Show(this, "The station did not answer, nothing was saved.", "Save settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            int proto = info != null ? info.Protocol : 0;
            var pid = data != null && proto >= 5 ? comm.ReadPidSlots() : null;
            var ramp = ReadRamp(proto);
            var fd = new SaveFileDialog();
            fd.Filter = "Settings file|*.txt";
            fd.FileName = "unisolder-settings-" + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".txt";
            if (fd.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                File.WriteAllText(fd.FileName, BackupFile.Format(data, info, state, pid, ramp), new UTF8Encoding(false));
                SSComm.Log.Info("Settings saved to " + fd.FileName);
                MessageBox.Show(this, "Settings saved.", "Save settings", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save the file: " + ex.Message, "Save settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void Restore_Click(object sender, EventArgs e)
        {
            var fd = new OpenFileDialog();
            fd.Filter = "Settings file|*.txt|All files|*.*";
            if (fd.ShowDialog(this) != DialogResult.OK) return;
            string text;
            try { text = File.ReadAllText(fd.FileName, Encoding.UTF8); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not read the file: " + ex.Message, "Restore settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string summary;
            List<byte[]> pid;
            RampBackup ramp;
            var data = BackupFile.Parse(text, out summary, out pid, out ramp);
            if (data == null)
            {
                MessageBox.Show(this, "This is not a UniSolder settings file.", "Restore settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var ask = MessageBox.Show(this, "Write the settings from this file to the station?\n\n" + summary + "\n\nThe station restarts afterwards.",
                "Restore settings", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (ask != DialogResult.OK) return;
            int proto = info != null ? info.Protocol : 0;
            bool ok = comm.WriteSettings(data);
            if (ok && pid != null && proto >= 5) ok = comm.WritePidSlots(pid);
            if (ok && ramp != null) ok = WriteRamp(ramp, proto);
            if (ok)
            {
                comm.RestartDevice();
                SSComm.Log.Info("Settings restored");
                MessageBox.Show(this, "Settings restored, the station restarts.", "Restore settings", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                SSComm.Log.Error("Settings restore failed");
                MessageBox.Show(this, "Writing the settings failed. The station was not restarted: check the settings in its menu, or retry.",
                    "Restore settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    /// <summary>Ramp mode part of a backup: 16 values, active steps, the stored profile they came from (-1 none), stored profiles (null = not in the file).</summary>
    class RampBackup
    {
        public int[] Pars;
        public int Count;
        public int[] Steps;
        public int Active;
        public List<UniSolderComm.RampSlot> Slots;
    }

    /// <summary>Settings backup file, identical to the Android app's format so a backup can be used on either.</summary>
    static class BackupFile
    {
        private static string Ints(int[] a)
        {
            return string.Join(",", a.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray());
        }

        public static string Format(byte[] data, UniSolderComm.AppInfo info, UniSolderComm.DeviceState state, List<byte[]> pid = null, RampBackup ramp = null)
        {
            var sb = new StringBuilder();
            sb.Append("UniSolder settings backup\n");
            sb.Append("version=1\n");
            sb.Append("saved=").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append('\n');
            if (info != null) sb.Append("firmware=").Append(info.Major).Append('.').Append(info.Minor).Append(" board=").Append(info.BoardName).Append('\n');
            if (state != null) sb.Append("set=").Append(state.Set).Append('\n');
            sb.Append("data=");
            foreach (var b in data) sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
            sb.Append('\n');
            if (pid != null)
            {
                sb.Append("pid=");
                foreach (var slot in pid) foreach (var b in slot) sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
                sb.Append('\n');
            }
            if (ramp != null)
            {
                sb.Append("ramp=").Append(Ints(ramp.Pars)).Append('\n');
                sb.Append("steps=").Append(ramp.Count).Append(';').Append(Ints(ramp.Steps)).Append(';').Append(ramp.Active).Append('\n');
                if (ramp.Slots != null)
                {
                    sb.Append("profiles=").Append(UniSolderComm.RAMP_SLOTS).Append('\n');
                    foreach (var sl in ramp.Slots)
                    {
                        if (sl.Count == 0) continue;
                        sb.Append("profile").Append(sl.Slot + 1).Append('=');
                        foreach (char c in sl.Name) sb.Append(((int)c & 0xFF).ToString("X2", CultureInfo.InvariantCulture));
                        sb.Append(';').Append(sl.Count).Append(';').Append(Ints(sl.Steps)).Append('\n');
                    }
                }
            }
            return sb.ToString();
        }

        private static int[] ParseInts(string s, int n)
        {
            var p = s.Split(',');
            if (p.Length != n) return null;
            var o = new int[n];
            for (int i = 0; i < n; i++)
                if (!int.TryParse(p[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out o[i])) return null;
            return o;
        }

        private static RampBackup ParseRamp(List<string> lines)
        {
            string rl = lines.FirstOrDefault(l => l.StartsWith("ramp="));
            string sl = lines.FirstOrDefault(l => l.StartsWith("steps="));
            if (rl == null || sl == null) return null;
            var pars = ParseInts(rl.Substring(5), UniSolderComm.RAMP_PARS9);
            var sp = sl.Substring(6).Split(';');
            if (pars == null || sp.Length != 3) return null;
            int count, active;
            if (!int.TryParse(sp[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out count)) return null;
            var steps = ParseInts(sp[1], UniSolderComm.RAMP_STEPS * 3);
            if (steps == null || !int.TryParse(sp[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out active)) return null;
            List<UniSolderComm.RampSlot> slots = null;
            if (lines.Any(l => l.StartsWith("profiles=")))
            {
                slots = new List<UniSolderComm.RampSlot>();
                for (int k = 0; k < UniSolderComm.RAMP_SLOTS; k++)
                {
                    string prefix = "profile" + (k + 1) + "=";
                    string pl = lines.FirstOrDefault(l => l.StartsWith(prefix));
                    if (pl == null) continue;
                    var f = pl.Substring(prefix.Length).Split(';');
                    if (f.Length != 3 || f[0].Length % 2 != 0) return null;
                    var name = new StringBuilder();
                    for (int i = 0; i < f[0].Length / 2; i++)
                    {
                        int c;
                        if (!int.TryParse(f[0].Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out c)) return null;
                        name.Append((char)c);
                    }
                    int cnt;
                    if (!int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out cnt)) return null;
                    var st = ParseInts(f[2], UniSolderComm.RAMP_STEPS * 3);
                    if (st == null) return null;
                    slots.Add(new UniSolderComm.RampSlot { Slot = k, Name = name.ToString(), Count = cnt, Steps = st });
                }
            }
            return new RampBackup { Pars = pars, Count = count, Steps = steps, Active = active, Slots = slots };
        }

        public static byte[] Parse(string text, out string summary, out List<byte[]> pid, out RampBackup ramp)
        {
            summary = "";
            pid = null;
            ramp = null;
            var lines = new List<string>();
            foreach (var l in text.Split('\n')) lines.Add(l.Trim());
            if (lines.Count == 0 || lines[0] != "UniSolder settings backup") return null;
            string dataLine = lines.FirstOrDefault(l => l.StartsWith("data="));
            if (dataLine == null) return null;
            string hex = dataLine.Substring(5);
            if (hex.Length != UniSolderComm.SETTINGS_SIZE * 2) return null;
            var outb = new byte[UniSolderComm.SETTINGS_SIZE];
            for (int i = 0; i < outb.Length; i++)
            {
                byte v;
                if (!byte.TryParse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v)) return null;
                outb[i] = v;
            }
            string pidLine = lines.FirstOrDefault(l => l.StartsWith("pid="));
            int n = UniSolderComm.PID_SLOTS * UniSolderComm.PID_SLOT_LEN;
            if (pidLine != null && pidLine.Length == 4 + n * 2)
            {
                var raw = new byte[n];
                for (int i = 0; i < n; i++)
                    if (!byte.TryParse(pidLine.Substring(4 + i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out raw[i])) return null;
                pid = new List<byte[]>();
                for (int k = 0; k < UniSolderComm.PID_SLOTS; k++)
                {
                    var d = new byte[UniSolderComm.PID_SLOT_LEN];
                    Array.Copy(raw, k * UniSolderComm.PID_SLOT_LEN, d, 0, d.Length);
                    pid.Add(d);
                }
            }
            var sum = lines.Where(l => l.StartsWith("saved=") || l.StartsWith("firmware=")).ToList();
            if (pid != null) sum.Add("Saved PID values: " + pid.Count(d => d[0] == 0xC7) + " iron(s)");
            ramp = ParseRamp(lines);
            if (ramp != null)
            {
                sum.Add("Ramp mode values and the active profile (" + ramp.Count + " step" + (ramp.Count == 1 ? "" : "s") + ")");
                if (ramp.Slots != null) sum.Add("Stored profiles: " + ramp.Slots.Count + " (profiles in the station that are not in the file are deleted)");
            }
            summary = string.Join("\n", sum.ToArray());
            return outb;
        }
    }
}
