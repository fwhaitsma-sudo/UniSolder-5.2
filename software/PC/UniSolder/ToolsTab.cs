using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Media;
using System.Text;
using System.Windows.Forms;

namespace UniSolder
{
    /// <summary>
    /// Tools tab, same function as the Tools tab of the Android app: session recording with CSV files, the list of recorded
    /// sessions (open, save a copy, delete), tip health (heater resistance with a baseline per iron ID), the overheat alert
    /// and the full-screen bench dashboard. Fed with every live data packet by Form1 (on the UI thread).
    /// </summary>
    class ToolsTab : Page
    {
        private readonly Recorder recorder = new Recorder();
        private readonly Label recordText = new Label();
        private readonly ThemedButton recordButton = new ThemedButton();
        private readonly ListView sessions = new ListView();
        private readonly Label tipText = new Label();
        private readonly ThemedCheck overheatCheck = new ThemedCheck();
        private readonly NotifyIcon notify = new NotifyIcon();
        private DashboardForm dashboard;

        /// <summary>Iron ID from the last instrument change (for the tip baseline).</summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int IronId { get; set; }

        /// <summary>Called when a recording starts or stops.</summary>
        public event Action<bool> RecordingChanged;

        // tip health
        private double resSum;
        private int resCount;
        private float resMin, resMax, resNow;
        private int sampleCount;

        // alerts
        private DateTime overheatSince = DateTime.MinValue;
        private bool overheatAlerted;
        private float overheatStartTemp;
        private float lastSet = -1f;
        private DateTime quietUntil = DateTime.MinValue;

        public bool Recording => recorder.Recording;

        public ToolsTab()
        {
            var col = Col;

            // recording
            col.Controls.Add(Theme.Heading("Recording"));
            recordText.AutoSize = true;
            recordText.Font = Theme.MakeFont(10f);
            recordText.ForeColor = Theme.Muted;
            recordText.BackColor = Theme.Bg;
            recordText.Margin = new Padding(0, 2, 0, 6);
            recordText.Text = IdleRecordText;
            col.Controls.Add(recordText);
            Theme.Style(recordButton, "Start recording", 220);
            recordButton.Strong = true;
            recordButton.Click += (s, e) => ToggleRecording();
            var dashButton = new ThemedButton();
            Theme.Style(dashButton, "Dashboard (full screen)", 260);
            dashButton.Click += (s, e) => ShowDashboard();
            col.Controls.Add(Theme.Row(recordButton, dashButton));

            // sessions
            col.Controls.Add(Theme.Heading("Sessions"));
            sessions.View = View.Details;
            sessions.FullRowSelect = true;
            sessions.MultiSelect = false;
            sessions.HideSelection = false;
            sessions.Tag = "fill";
            sessions.Height = 230;
            sessions.Margin = new Padding(0, 0, 0, 6);
            sessions.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            sessions.Font = Theme.MakeFont(10f);
            int u = Math.Max(6, TextRenderer.MeasureText("0000000000", sessions.Font).Width / 10);
            sessions.Columns.Add("Session", u * 22);
            sessions.Columns.Add("Length", u * 9);
            sessions.Columns.Add("Max °C", u * 9);
            sessions.Columns.Add("Avg power", u * 11);
            sessions.Columns.Add("Heat-up", u * 10);
            sessions.Columns.Add("Rows", u * 8);
            Theme.StyleList(sessions);
            sessions.DoubleClick += (s, e) => OpenSelected();
            col.Controls.Add(sessions);
            var openButton = new ThemedButton();
            Theme.Style(openButton, "Open", 110);
            openButton.Click += (s, e) => OpenSelected();
            var copyButton = new ThemedButton();
            Theme.Style(copyButton, "Save a copy...", 150);
            copyButton.Click += (s, e) => CopySelected();
            var deleteButton = new ThemedButton();
            Theme.Style(deleteButton, "Delete", 110);
            deleteButton.Click += (s, e) => DeleteSelected();
            var folderButton = new ThemedButton();
            Theme.Style(folderButton, "Open folder", 150);
            folderButton.Click += (s, e) => OpenFolder();
            col.Controls.Add(Theme.Row(openButton, copyButton, deleteButton, folderButton));

            // tip health
            col.Controls.Add(Theme.Heading("Tip health"));
            tipText.AutoSize = true;
            tipText.Font = Theme.MakeFont(10.5f);
            tipText.ForeColor = Theme.Text;
            tipText.BackColor = Theme.Bg;
            tipText.Margin = new Padding(0, 2, 0, 6);
            col.Controls.Add(tipText);
            col.Controls.Add(Theme.Hint("The heater resistance of a good tip stays about the same. A rising value points to a worn tip or a poor contact. " +
                "Press the button with a new or good tip heated up, so later readings can be compared with it."));
            var baseButton = new ThemedButton();
            Theme.Style(baseButton, "Use the current average as baseline", 380);
            baseButton.Click += (s, e) => SetBaseline();
            col.Controls.Add(Theme.Row(baseButton));

            // alerts
            col.Controls.Add(Theme.Heading("Alerts"));
            overheatCheck.Text = "Warn when the tip stays too hot";
            overheatCheck.Checked = AppPrefs.GetBool("alert_overheat", false);
            overheatCheck.CheckedChanged += (s, e) => AppPrefs.PutBool("alert_overheat", overheatCheck.Checked);
            col.Controls.Add(overheatCheck);
            col.Controls.Add(Theme.Hint("Warns when the tip stays 25 °C above the target and is not cooling. Alerts only work while this program is running and the station is connected. " +
                "The program can warn, but it cannot switch the heater off."));

            notify.Icon = SystemIcons.Warning;
            notify.Text = "UniSolder";
            Disposed += (s, e) => { recorder.Stop(); notify.Visible = false; notify.Dispose(); };

            ShowTip();
            RefreshSessions();
        }

        private const string IdleRecordText = "Records set temperature, actual temperature, power and heater resistance to a file, five times a second.";

        // ---------------------------------------------------------------- recording

        public void ToggleRecording()
        {
            if (recorder.Recording)
            {
                recorder.Stop();
                RefreshSessions();
            }
            else if (!recorder.Start())
            {
                MessageBox.Show("Cannot start recording (see the log).", "UniSolder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            UpdateRecordUi();
            RecordingChanged?.Invoke(recorder.Recording);
        }

        private void UpdateRecordUi()
        {
            recordButton.Text = recorder.Recording ? "Stop recording" : "Start recording";
            if (!recorder.Recording)
                recordText.Text = IdleRecordText;
        }

        public void RefreshSessions()
        {
            sessions.BeginUpdate();
            sessions.Items.Clear();
            foreach (var f in Recorder.Sessions())
            {
                bool current = recorder.Recording && string.Equals(recorder.FilePath, f, StringComparison.OrdinalIgnoreCase);
                var name = Path.GetFileNameWithoutExtension(f);
                if (name.StartsWith("session-")) name = name.Substring(8);
                var it = new ListViewItem(name);
                it.Tag = f;
                if (current)
                {
                    it.SubItems.Add("recording ...");
                }
                else
                {
                    var st = SessionStats.Read(f);
                    if (st == null)
                    {
                        it.SubItems.Add("empty");
                    }
                    else
                    {
                        it.SubItems.Add(st.DurationText);
                        it.SubItems.Add(st.MaxTemp.ToString("0", CultureInfo.InvariantCulture));
                        it.SubItems.Add(st.AvgPower.ToString("0", CultureInfo.InvariantCulture) + " %");
                        it.SubItems.Add(st.WarmupS.HasValue ? st.WarmupS.Value.ToString("0", CultureInfo.InvariantCulture) + " s" : "");
                        it.SubItems.Add(st.Rows.ToString(CultureInfo.InvariantCulture));
                    }
                }
                sessions.Items.Add(it);
            }
            sessions.EndUpdate();
        }

        private string Selected()
        {
            if (sessions.SelectedItems.Count == 0)
            {
                MessageBox.Show("Select a session first.", "UniSolder", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;
            }
            return sessions.SelectedItems[0].Tag as string;
        }

        private void OpenSelected()
        {
            var f = Selected();
            if (f == null) return;
            try { Process.Start(new ProcessStartInfo(f) { UseShellExecute = true }); }
            catch (Exception) { Process.Start("notepad.exe", "\"" + f + "\""); }
        }

        private void CopySelected()
        {
            var f = Selected();
            if (f == null) return;
            using (var d = new SaveFileDialog { FileName = Path.GetFileName(f), Filter = "CSV files|*.csv|All files|*.*", Title = "Save a copy of the session" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.Copy(f, d.FileName, true);
                }
                catch (Exception e)
                {
                    MessageBox.Show("Could not save the copy: " + e.Message, "UniSolder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void DeleteSelected()
        {
            var f = Selected();
            if (f == null) return;
            if (recorder.Recording && string.Equals(recorder.FilePath, f, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("This session is being recorded: stop the recording first.", "UniSolder", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show("Delete " + Path.GetFileName(f) + "?", "Delete recording", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try { File.Delete(f); } catch (Exception e) { SSComm.Log.Warn("Delete failed: " + e.Message); }
            RefreshSessions();
        }

        private void OpenFolder()
        {
            try
            {
                Directory.CreateDirectory(Recorder.SessionsDir);
                Process.Start(new ProcessStartInfo(Recorder.SessionsDir) { UseShellExecute = true });
            }
            catch (Exception e)
            {
                SSComm.Log.Warn("Cannot open the folder: " + e.Message);
            }
        }

        // ---------------------------------------------------------------- dashboard

        public void ShowDashboard()
        {
            if (dashboard == null || dashboard.IsDisposed)
            {
                dashboard = new DashboardForm();
                dashboard.FormClosed += (s, e) => dashboard = null;
            }
            dashboard.Show();
            dashboard.Activate();
        }

        // ---------------------------------------------------------------- live data

        /// <summary>Every live data packet, on the UI thread.</summary>
        public void OnSample(LiveSample s)
        {
            recorder.Add(s);
            if (recorder.Recording && ++sampleCount % 25 == 0)
                recordText.Text = "Recording " + recorder.ElapsedSeconds + " s, " + recorder.Rows + " rows";
            if (dashboard != null && !dashboard.IsDisposed) dashboard.UpdateSample(s);
            var now = DateTime.Now;

            // tip health: the heater resistance is only meaningful while it is measured (not zero)
            if (s.Resistance > 0.05f)
            {
                resNow = s.Resistance;
                resSum += s.Resistance;
                resCount++;
                if (resCount == 1 || s.Resistance < resMin) resMin = s.Resistance;
                if (resCount == 1 || s.Resistance > resMax) resMax = s.Resistance;
                if (resCount % 25 == 0) ShowTip();
            }

            // Overheat: the tip stays well above the target and is not cooling down. The target drops when the iron goes in
            // the stand or to sleep, and the tip then cools for a while above the new, lower target: that is no overheating.
            // Any change of the target restarts the check, and after a lowering the check stays quiet for two minutes.
            if (lastSet >= 0f && s.Set != lastSet)
            {
                overheatSince = DateTime.MinValue;
                overheatAlerted = false;
                quietUntil = now.AddSeconds(s.Set < lastSet ? 120 : 10);
            }
            lastSet = s.Set;
            if (s.Set > 0f && s.Temp > s.Set + 25f && now >= quietUntil)
            {
                if (overheatSince == DateTime.MinValue)
                {
                    overheatSince = now;
                    overheatStartTemp = s.Temp;
                }
                if ((now - overheatSince).TotalSeconds >= 10)
                {
                    if (s.Temp < overheatStartTemp - 2f)
                    {
                        overheatSince = now;            // still cooling: look again from here
                        overheatStartTemp = s.Temp;
                    }
                    else if (!overheatAlerted && overheatCheck.Checked)
                    {
                        overheatAlerted = true;
                        Alert("Tip too hot", string.Format(CultureInfo.InvariantCulture, "{0:0} °C, set {1:0} °C", s.Temp, s.Set));
                    }
                }
            }
            else if (s.Temp < s.Set + 10f || s.Set <= 0f || now < quietUntil)
            {
                overheatSince = DateTime.MinValue;
                overheatAlerted = false;
            }
        }

        public void OnDisconnected()
        {
            resSum = 0;
            resCount = 0;
            overheatSince = DateTime.MinValue;
            overheatAlerted = false;
            lastSet = -1f;
            quietUntil = DateTime.MinValue;
            if (dashboard != null && !dashboard.IsDisposed) dashboard.ShowNoData();
            ShowTip();
        }

        // ---------------------------------------------------------------- tip health

        private string BaselineKey() => "baseline_" + IronId;

        private void SetBaseline()
        {
            if (resCount < 25)
            {
                MessageBox.Show("Not enough readings yet: heat the iron up first.", "UniSolder", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            AppPrefs.PutFloat(BaselineKey(), (float)(resSum / resCount));
            ShowTip();
        }

        public void ShowTip()
        {
            if (resCount == 0)
            {
                tipText.Text = "No heater resistance readings yet (iron ID " + Hex(IronId) + ").";
                return;
            }
            float avg = (float)(resSum / resCount);
            float b = AppPrefs.GetFloat(BaselineKey(), 0f);
            var sb = new StringBuilder();
            sb.Append(string.Format(CultureInfo.InvariantCulture, "Now {0:0.0} Ω, average {1:0.0} Ω, range {2:0.0} - {3:0.0} Ω", resNow, avg, resMin, resMax));
            if (b > 0f)
            {
                float change = (avg - b) / b * 100f;
                sb.Append(string.Format(CultureInfo.InvariantCulture, "\nBaseline {0:0.0} Ω: {1:+0;-0;0} %", b, change));
                if (change > 25f) sb.Append("\nMuch higher than the baseline: worn tip or poor contact.");
                else if (change > 10f) sb.Append("\nSlightly higher than the baseline: keep an eye on it.");
                else if (change < -25f) sb.Append("\nMuch lower than the baseline: check the tip and the connection.");
                else sb.Append("\nClose to the baseline.");
            }
            else
            {
                sb.Append("\nNo baseline saved for this iron (ID ").Append(Hex(IronId)).Append(").");
            }
            tipText.Text = sb.ToString();
        }

        private static string Hex(int v) => "0x" + v.ToString("X", CultureInfo.InvariantCulture);

        // ---------------------------------------------------------------- alerts

        private void Alert(string title, string text)
        {
            SSComm.Log.Info("Alert: " + title + " " + text);
            SystemSounds.Exclamation.Play();
            notify.Visible = true;
            notify.ShowBalloonTip(10000, title, text, ToolTipIcon.Warning);
        }
    }
}
