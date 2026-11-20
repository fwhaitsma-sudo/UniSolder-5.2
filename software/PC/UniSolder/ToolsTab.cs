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
    class ToolsTab : Panel
    {
        private readonly Recorder recorder = new Recorder();
        private readonly Label recordText = new Label();
        private readonly Button recordButton = new Button();
        private readonly ListView sessions = new ListView();
        private readonly Label tipText = new Label();
        private readonly CheckBox overheatCheck = new CheckBox();
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
            Dock = DockStyle.Fill;
            AutoScroll = true;
            Padding = new Padding(16);

            var flow = new FlowLayoutPanel();
            flow.Dock = DockStyle.Top;
            flow.AutoSize = true;
            flow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            flow.FlowDirection = FlowDirection.TopDown;
            flow.WrapContents = false;

            // recording
            var rec = MakeBox("Recording");
            recordText.AutoSize = true;
            recordText.MaximumSize = new Size(640, 0);
            recordText.Text = "Records set temperature, actual temperature, power and heater resistance to a file, five times a second.";
            rec.Controls.Add(recordText);
            StyleButton(recordButton, "Start recording", 220);
            recordButton.Click += (s, e) => ToggleRecording();
            rec.Controls.Add(recordButton);
            var dashButton = new Button();
            StyleButton(dashButton, "Dashboard (full screen)", 220);
            dashButton.Click += (s, e) => ShowDashboard();
            rec.Controls.Add(dashButton);
            flow.Controls.Add(Wrap(rec));

            // sessions
            var ses = MakeBox("Sessions");
            sessions.View = View.Details;
            sessions.FullRowSelect = true;
            sessions.MultiSelect = false;
            sessions.HideSelection = false;
            sessions.Width = 640;
            sessions.Height = 200;
            sessions.Columns.Add("Session", 180);
            sessions.Columns.Add("Length", 70);
            sessions.Columns.Add("Max °C", 70);
            sessions.Columns.Add("Avg power", 80);
            sessions.Columns.Add("Heat-up", 80);
            sessions.Columns.Add("Rows", 60);
            sessions.DoubleClick += (s, e) => OpenSelected();
            ses.Controls.Add(sessions);
            var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            var openButton = new Button();
            StyleButton(openButton, "Open", 110);
            openButton.Click += (s, e) => OpenSelected();
            var copyButton = new Button();
            StyleButton(copyButton, "Save a copy...", 130);
            copyButton.Click += (s, e) => CopySelected();
            var deleteButton = new Button();
            StyleButton(deleteButton, "Delete", 110);
            deleteButton.Click += (s, e) => DeleteSelected();
            var folderButton = new Button();
            StyleButton(folderButton, "Open folder", 130);
            folderButton.Click += (s, e) => OpenFolder();
            row.Controls.AddRange(new Control[] { openButton, copyButton, deleteButton, folderButton });
            ses.Controls.Add(row);
            flow.Controls.Add(Wrap(ses));

            // tip health
            var tip = MakeBox("Tip health");
            tipText.AutoSize = true;
            tipText.Font = new Font(Font.FontFamily, 10.5f);
            tipText.MaximumSize = new Size(640, 0);
            tip.Controls.Add(tipText);
            var baseNote = new Label();
            baseNote.AutoSize = true;
            baseNote.MaximumSize = new Size(640, 0);
            baseNote.Margin = new Padding(0, 8, 0, 0);
            baseNote.Text = "The heater resistance of a good tip stays about the same. A rising value points to a worn tip or a poor contact. " +
                "Press the button with a new or good tip heated up, so later readings can be compared with it.";
            tip.Controls.Add(baseNote);
            var baseButton = new Button();
            StyleButton(baseButton, "Use the current average as this iron's baseline", 380);
            baseButton.Click += (s, e) => SetBaseline();
            tip.Controls.Add(baseButton);
            flow.Controls.Add(Wrap(tip));

            // alerts
            var al = MakeBox("Alerts");
            overheatCheck.AutoSize = true;
            overheatCheck.Text = "Warn when the tip stays 25 °C above the target and is not cooling";
            overheatCheck.Checked = AppPrefs.GetBool("alert_overheat", false);
            overheatCheck.CheckedChanged += (s, e) => AppPrefs.PutBool("alert_overheat", overheatCheck.Checked);
            al.Controls.Add(overheatCheck);
            var alertNote = new Label();
            alertNote.AutoSize = true;
            alertNote.MaximumSize = new Size(640, 0);
            alertNote.Text = "Alerts only work while this program is running and the station is connected. " +
                "The program can warn, but it cannot switch the heater off.";
            al.Controls.Add(alertNote);
            flow.Controls.Add(Wrap(al));

            Controls.Add(flow);

            notify.Icon = SystemIcons.Warning;
            notify.Text = "UniSolder";
            Disposed += (s, e) => { recorder.Stop(); notify.Visible = false; notify.Dispose(); };

            ShowTip();
            RefreshSessions();
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
            var controls = new System.Collections.Generic.List<Control>();
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
            b.Margin = new Padding(0, 6, 12, 6);
            b.UseVisualStyleBackColor = true;
        }

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
                recordText.Text = "Records set temperature, actual temperature, power and heater resistance to a file, five times a second.";
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
