using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace UniSolder
{
    /// <summary>
    /// Control tab: set temperature, presets, sleep, switch iron, station info and settings backup/restore.
    /// Same function as the Control tab of the Android app. All commands run on the UI thread (each waits at most a second).
    /// </summary>
    class ControlTab : Panel
    {
        private readonly UniSolderComm comm;
        private readonly Label stateLabel = new Label();
        private readonly Label tempLabel = new Label();
        private readonly Label infoLabel = new Label();
        private readonly TrackBar tempBar = new TrackBar();
        private readonly Button minusButton = new Button();
        private readonly Button plusButton = new Button();
        private readonly Button[] presetButtons = new Button[4];
        private readonly Button sleepButton = new Button();
        private readonly Button ironButton = new Button();
        private readonly Button saveButton = new Button();
        private readonly Button restoreButton = new Button();
        private readonly Timer pollTimer = new Timer();
        private readonly Timer sendTimer = new Timer();

        private UniSolderComm.AppInfo info;
        private UniSolderComm.DeviceState state;
        private int pendingTemp = -1;
        private DateTime holdUntil = DateTime.MinValue;
        private bool updating;

        public ControlTab(UniSolderComm comm)
        {
            this.comm = comm;
            Dock = DockStyle.Fill;
            AutoScroll = true;
            Padding = new Padding(16);

            var flow = new FlowLayoutPanel();
            flow.Dock = DockStyle.Top;
            flow.AutoSize = true;
            flow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            flow.FlowDirection = FlowDirection.TopDown;
            flow.WrapContents = false;

            stateLabel.AutoSize = true;
            stateLabel.Font = new Font(Font.FontFamily, 12f, FontStyle.Bold);
            stateLabel.Margin = new Padding(0, 0, 0, 12);
            stateLabel.Text = "Not connected";
            flow.Controls.Add(stateLabel);

            // temperature
            var tempBox = MakeBox("Temperature");
            tempLabel.AutoSize = true;
            tempLabel.Font = new Font(Font.FontFamily, 28f, FontStyle.Bold);
            tempLabel.Text = "-";
            tempLabel.Margin = new Padding(8, 4, 8, 4);
            tempBar.Width = 520;
            tempBar.TickFrequency = 10;
            tempBar.LargeChange = 10;
            tempBar.SmallChange = 1;
            tempBar.Minimum = 0;
            tempBar.Maximum = 100;
            tempBar.Margin = new Padding(8, 4, 8, 4);
            tempBar.ValueChanged += TempBar_ValueChanged;
            StyleButton(minusButton, "−", 56);
            StyleButton(plusButton, "+", 56);
            minusButton.Click += delegate { StepTemp(-1); };
            plusButton.Click += delegate { StepTemp(1); };
            var barRow = new FlowLayoutPanel();
            barRow.AutoSize = true;
            barRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            barRow.Controls.Add(minusButton);
            barRow.Controls.Add(tempBar);
            barRow.Controls.Add(plusButton);
            tempBox.Controls.Add(tempLabel);
            tempBox.Controls.Add(barRow);
            flow.Controls.Add(Wrap(tempBox));

            // presets
            var presetBox = MakeBox("Presets");
            var presetRow = new FlowLayoutPanel();
            presetRow.AutoSize = true;
            presetRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            for (int i = 0; i < 4; i++)
            {
                int index = i;
                presetButtons[i] = new Button();
                StyleButton(presetButtons[i], "T" + (i + 1), 110);
                presetButtons[i].Height = 56;
                presetButtons[i].Click += delegate { Preset(index); };
                presetRow.Controls.Add(presetButtons[i]);
            }
            presetBox.Controls.Add(presetRow);
            flow.Controls.Add(Wrap(presetBox));

            // station
            var stationBox = MakeBox("Station");
            var stationRow = new FlowLayoutPanel();
            stationRow.AutoSize = true;
            stationRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            StyleButton(sleepButton, "Sleep", 130);
            sleepButton.Click += Sleep_Click;
            StyleButton(ironButton, "Switch iron", 220);
            ironButton.Click += Iron_Click;
            ironButton.Visible = false;
            stationRow.Controls.Add(sleepButton);
            stationRow.Controls.Add(ironButton);
            stationBox.Controls.Add(stationRow);
            flow.Controls.Add(Wrap(stationBox));

            // settings
            var settingsBox = MakeBox("Settings backup");
            var note = new Label();
            note.AutoSize = true;
            note.MaximumSize = new Size(620, 0);
            note.Margin = new Padding(8, 4, 8, 4);
            note.Text = "Saves the station settings (not the pincode) to a file, or writes them back from a file. The station restarts after a restore. The file is the same format as the Android app uses.";
            var settingsRow = new FlowLayoutPanel();
            settingsRow.AutoSize = true;
            settingsRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            StyleButton(saveButton, "Save settings...", 170);
            StyleButton(restoreButton, "Restore settings...", 190);
            saveButton.Click += Save_Click;
            restoreButton.Click += Restore_Click;
            settingsRow.Controls.Add(saveButton);
            settingsRow.Controls.Add(restoreButton);
            settingsBox.Controls.Add(note);
            settingsBox.Controls.Add(settingsRow);
            flow.Controls.Add(Wrap(settingsBox));

            infoLabel.AutoSize = true;
            infoLabel.MaximumSize = new Size(700, 0);
            infoLabel.Margin = new Padding(0, 8, 0, 0);
            flow.Controls.Add(infoLabel);

            Controls.Add(flow);
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

        private static GroupBox MakeBox(string title)
        {
            var g = new GroupBox();
            g.Text = title;
            g.AutoSize = true;
            g.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            g.Padding = new Padding(8);
            return g;
        }

        // GroupBox does not lay out its children by itself: a FlowLayoutPanel inside gives it its size
        private static Control Wrap(GroupBox g)
        {
            var inner = new FlowLayoutPanel();
            inner.FlowDirection = FlowDirection.TopDown;
            inner.WrapContents = false;
            inner.AutoSize = true;
            inner.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            var controls = new List<Control>();
            foreach (Control c in g.Controls) controls.Add(c);
            g.Controls.Clear();
            foreach (var c in controls) inner.Controls.Add(c);
            g.Controls.Add(inner);
            g.Margin = new Padding(0, 0, 0, 12);
            return g;
        }

        private static void StyleButton(Button b, string text, int width)
        {
            b.Text = text;
            b.Width = width;
            b.Height = 40;
            b.Margin = new Padding(8, 6, 8, 6);
            b.UseVisualStyleBackColor = true;
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

        // ---------------------------------------------------------------- settings backup

        private void Save_Click(object sender, EventArgs e)
        {
            var data = comm.ReadSettings();
            if (data == null)
            {
                MessageBox.Show(this, "The station did not answer, nothing was saved.", "Save settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var fd = new SaveFileDialog();
            fd.Filter = "Settings file|*.txt";
            fd.FileName = "unisolder-settings-" + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".txt";
            if (fd.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                File.WriteAllText(fd.FileName, BackupFile.Format(data, info, state), new UTF8Encoding(false));
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
            var data = BackupFile.Parse(text, out summary);
            if (data == null)
            {
                MessageBox.Show(this, "This is not a UniSolder settings file.", "Restore settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var ask = MessageBox.Show(this, "Write the settings from this file to the station?\n\n" + summary + "\n\nThe station restarts afterwards.",
                "Restore settings", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (ask != DialogResult.OK) return;
            if (comm.WriteSettings(data))
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

    /// <summary>Settings backup file, identical to the Android app's format so a backup can be used on either.</summary>
    static class BackupFile
    {
        public static string Format(byte[] data, UniSolderComm.AppInfo info, UniSolderComm.DeviceState state)
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
            return sb.ToString();
        }

        public static byte[] Parse(string text, out string summary)
        {
            summary = "";
            var lines = new List<string>();
            foreach (var l in text.Split('\n')) lines.Add(l.Trim());
            if (lines.Count == 0 || lines[0] != "UniSolder settings backup") return null;
            string dataLine = null;
            var sum = new List<string>();
            foreach (var l in lines)
            {
                if (dataLine == null && l.StartsWith("data=")) dataLine = l;
                if (l.StartsWith("saved=") || l.StartsWith("firmware=")) sum.Add(l);
            }
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
            summary = string.Join("\n", sum.ToArray());
            return outb;
        }
    }
}
