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
            col.Controls.Add(Theme.Row(sleepButton, ironButton, settingsButton));

            // settings
            col.Controls.Add(Theme.Heading("Settings backup"));
            col.Controls.Add(Theme.Hint("Saves the station settings (not the pincode) to a file, or writes them back from a file. The station restarts after a restore. The file is the same format as the Android app uses."));
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
