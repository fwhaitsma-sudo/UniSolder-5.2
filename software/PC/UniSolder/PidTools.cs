using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

namespace UniSolder
{
    /// <summary>Result of one analysed step of the set temperature.</summary>
    class StepResult
    {
        public float StepFrom, Target, Overshoot, SteadyError;
        public double? RiseS, SettleS;
        public string PidText;

        public string Text()
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append(string.Format(ci, "{0:0} → {1:0}°C  ({2})\r\n", StepFrom, Target, PidText));
            sb.Append("rise 10-90%: ").Append(RiseS.HasValue ? string.Format(ci, "{0:0.0} s", RiseS.Value) : "not reached");
            sb.Append(string.Format(ci, "   overshoot: {0:0}°C ({1:0}%)", Overshoot, Overshoot / (Target - StepFrom) * 100f));
            sb.Append("\r\nsettled within ±3°C: ").Append(SettleS.HasValue ? string.Format(ci, "{0:0.0} s", SettleS.Value) : "not settled");
            sb.Append(string.Format(ci, "   steady error: {0:+0.0;-0.0;0.0}°C", SteadyError));
            return sb.ToString();
        }
    }

    /// <summary>Watches the live data for a rise of the set temperature and measures how the iron follows it (same as the Android app).</summary>
    class StepAnalyzer
    {
        private float lastSet;
        private bool running;
        private long t0;
        private float from, target;
        private readonly List<double> ts = new List<double>();
        private readonly List<float> temps = new List<float>();
        private long reachedSince;

        public bool IsRunning { get { return running; } }

        public void Reset()
        {
            running = false;
            lastSet = 0f;
        }

        /// <summary>Returns a result when a step has finished, otherwise null.</summary>
        public StepResult Add(long now, LiveSample s, string pidText)
        {
            StepResult result = null;
            if (running)
            {
                if (s.Set != target)
                {
                    running = false;        //the set temperature moved again: the measurement is not clean
                }
                else
                {
                    ts.Add((now - t0) / 1000.0);
                    temps.Add(s.Temp);
                    if (s.Reached)
                    {
                        if (reachedSince == 0) reachedSince = now;
                    }
                    else reachedSince = 0;
                    bool settledLong = reachedSince != 0 && now - reachedSince >= 15000;
                    if (settledLong || now - t0 >= 90000)
                    {
                        running = false;
                        result = Analyse(pidText);
                    }
                }
            }
            else if (lastSet > 0f && s.Set - lastSet >= 20f && s.Set > 0f)
            {
                running = true;
                t0 = now;
                from = s.Temp;
                target = s.Set;
                ts.Clear();
                temps.Clear();
                reachedSince = 0;
            }
            lastSet = s.Set;
            return result;
        }

        private StepResult Analyse(string pidText)
        {
            float step = target - from;
            if (step < 15f || temps.Count < 20) return null;
            double? t10 = null, t90 = null;
            float max = temps[0];
            for (int i = 0; i < temps.Count; i++)
            {
                float v = temps[i];
                if (v > max) max = v;
                if (t10 == null && v >= from + 0.1f * step) t10 = ts[i];
                if (t90 == null && v >= from + 0.9f * step) t90 = ts[i];
            }
            double? rise = t10 != null && t90 != null ? t90 - t10 : null;
            int lastOutside = -1;
            for (int i = 0; i < temps.Count; i++) if (Math.Abs(temps[i] - target) > 3f) lastOutside = i;
            double? settle;
            if (lastOutside < 0) settle = 0.0;
            else if (lastOutside >= temps.Count - 1) settle = null;
            else settle = ts[lastOutside + 1];
            double end = ts[ts.Count - 1];
            float sum = 0f;
            int n = 0;
            for (int i = 0; i < temps.Count; i++)
                if (ts[i] >= end - 10.0)
                {
                    sum += temps[i] - target;
                    n++;
                }
            return new StepResult
            {
                StepFrom = from,
                Target = target,
                RiseS = rise,
                Overshoot = Math.Max(0f, max - target),
                SettleS = settle,
                SteadyError = n > 0 ? sum / n : 0f,
                PidText = pidText
            };
        }
    }

    /// <summary>Small dark dialog asking for one line of text.</summary>
    class PromptForm : Form
    {
        private readonly TextBox box = new TextBox();

        public string Value { get { return box.Text; } }

        public PromptForm(string title, string hint, string initial = "", int maxLength = 0)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.MakeFont(10f);
            ClientSize = new Size(420, 150);
            HandleCreated += (s, e) => Theme.DarkTitle(this);
            var label = new Label { Text = hint, AutoSize = false, Location = new Point(16, 14), Size = new Size(388, 24), ForeColor = Theme.Text, BackColor = Theme.Bg };
            box.Location = new Point(16, 44);
            box.Size = new Size(388, 28);
            box.BackColor = Theme.Field;
            box.ForeColor = Theme.Text;
            box.BorderStyle = BorderStyle.FixedSingle;
            box.Font = Theme.MakeFont(11f);
            box.Text = initial ?? "";
            if (maxLength > 0) box.MaxLength = maxLength;
            var ok = new ThemedButton { Strong = true, Text = "Save", Size = new Size(110, 40), Location = new Point(176, 92), DialogResult = DialogResult.OK };
            var cancel = new ThemedButton { Text = "Cancel", Size = new Size(110, 40), Location = new Point(294, 92), DialogResult = DialogResult.Cancel };
            Controls.Add(label);
            Controls.Add(box);
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }

    /// <summary>
    /// Extra tools on the PID page, like the Android app: revert to the values read at connect, save in the station, saved PID sets per
    /// iron ID (kept in the settings file of the app), step response analysis with a step test, and the calibration helper for the Gain.
    /// All commands run on the UI thread (each waits at most about two seconds).
    /// </summary>
    class PidTools
    {
        private readonly Page page;
        private readonly UniSolderComm comm;
        private readonly Func<UniSolderComm.PID> getPid;
        private readonly Action<UniSolderComm.PID> applyPid;
        private readonly Action reloadPid;

        private int ironId;
        private UniSolderComm.PID original;
        private readonly StepAnalyzer analyzer = new StepAnalyzer();
        private readonly List<string> results = new List<string>();
        private readonly Label resultText = new Label();
        private readonly ThemedButton testButton = new ThemedButton();
        private readonly FlowLayoutPanel setsBox = new FlowLayoutPanel();
        private readonly TextBox calRef = new TextBox();
        private readonly TextBox calAmb = new TextBox();
        private readonly Label calText = new Label();
        private readonly Label storedText = new Label();
        private readonly ThemedButton calApply = new ThemedButton();
        private int calSuggest;
        private float lastTemp;
        private bool lastReached;
        private int restoreTo = -1;
        private bool testWaiting;
        private readonly Timer restoreTimer = new Timer { Interval = 120000 };
        private readonly Timer waitTimer = new Timer { Interval = 3000 };
        private readonly Timer reloadTimer = new Timer { Interval = 3000 };

        public PidTools(Page page, UniSolderComm comm, Func<UniSolderComm.PID> getPid, Action<UniSolderComm.PID> applyPid, Action reloadPid)
        {
            this.page = page;
            this.comm = comm;
            this.getPid = getPid;
            this.applyPid = applyPid;
            this.reloadPid = reloadPid;
            var col = page.Col;

            restoreTimer.Tick += (s, e) => RestoreTemp();
            waitTimer.Tick += (s, e) => { waitTimer.Stop(); testWaiting = false; };
            reloadTimer.Tick += (s, e) => { reloadTimer.Stop(); this.reloadPid(); };

            // revert
            var revert = new ThemedButton();
            Theme.Style(revert, "Revert to the values read at connect", 330);
            revert.Margin = new Padding(0, 16, 0, 6);
            revert.Click += (s, e) =>
            {
                if (original == null) MessageBox.Show(page, "No values read yet.", "PID", MessageBoxButtons.OK, MessageBoxIcon.Information);
                else this.applyPid(Copy(original));
            };
            col.Controls.Add(revert);

            // permanent storage in the station
            col.Controls.Add(Theme.Heading("Save in the station"));
            col.Controls.Add(Theme.Hint("Normally the iron forgets changed PID and Gain values when it restarts. Saving keeps the values now on the sliders in the station for this iron, " +
                "and they are loaded again every time this iron is detected. Needs the remote-control firmware (version 2 of the protocol)."));
            storedText.AutoSize = true;
            storedText.ForeColor = Theme.Text;
            storedText.BackColor = Theme.Bg;
            storedText.Font = Theme.MakeFont(10f);
            storedText.Margin = new Padding(0, 2, 0, 6);
            col.Controls.Add(storedText);
            var keep = new ThemedButton();
            Theme.Style(keep, "Save the current values in the station", 330);
            keep.Click += (s, e) => SaveInStation();
            var forget = new ThemedButton();
            Theme.Style(forget, "Restore the standard values", 260);
            forget.Click += (s, e) => ForgetInStation();
            col.Controls.Add(Theme.Row(keep, forget));
            col.Controls.Add(Theme.Hint("Standard values are the ones built into the firmware for this iron. Restoring them also removes values you saved in the station and values you changed but did not save."));

            // saved sets
            col.Controls.Add(Theme.Heading("Saved PID sets"));
            col.Controls.Add(Theme.Hint("Sets are kept per iron ID on this computer. Loading a set sends it to the iron now; the iron forgets it when it restarts."));
            var save = new ThemedButton();
            Theme.Style(save, "Save the current values as a set", 300);
            save.Click += (s, e) => AskSetName();
            col.Controls.Add(save);
            setsBox.FlowDirection = FlowDirection.TopDown;
            setsBox.WrapContents = false;
            setsBox.AutoSize = true;
            setsBox.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            setsBox.BackColor = Theme.Bg;
            setsBox.Margin = new Padding(0, 4, 0, 0);
            col.Controls.Add(setsBox);

            // step response
            col.Controls.Add(Theme.Heading("Step response"));
            col.Controls.Add(Theme.Hint("Whenever the set temperature is raised by 20°C or more, the app measures how the iron follows it. " +
                "Use the test button to do that by itself: it raises the set temperature by 30°C and puts it back afterwards."));
            Theme.Style(testButton, "Run step test (+30°C)", 250);
            testButton.Click += (s, e) => StartStepTest();
            col.Controls.Add(testButton);
            resultText.AutoSize = true;
            resultText.Text = "No step measured yet.";
            resultText.ForeColor = Theme.Text;
            resultText.BackColor = Theme.Bg;
            resultText.Font = Theme.MakeFont(10f);
            resultText.Margin = new Padding(0, 8, 0, 0);
            col.Controls.Add(resultText);

            // calibration
            col.Controls.Add(Theme.Heading("Calibration helper"));
            col.Controls.Add(Theme.Hint("Heat the iron to a steady temperature, measure the tip with a reference thermometer, and enter its reading. " +
                "The app suggests a new Gain. It is an estimate that assumes the room temperature below: check it by measuring again."));
            col.Controls.Add(Theme.Body("Reference temperature (°C)"));
            StyleBox(calRef);
            col.Controls.Add(calRef);
            col.Controls.Add(Theme.Body("Room temperature (°C)"));
            StyleBox(calAmb);
            calAmb.Text = "25";
            col.Controls.Add(calAmb);
            var calc = new ThemedButton();
            Theme.Style(calc, "Suggest Gain", 200);
            calc.Margin = new Padding(0, 10, 10, 6);
            calc.Click += (s, e) => SuggestGain();
            col.Controls.Add(calc);
            calText.AutoSize = true;
            calText.ForeColor = Theme.Text;
            calText.BackColor = Theme.Bg;
            calText.Font = Theme.MakeFont(10f);
            calText.Margin = new Padding(0, 2, 0, 6);
            col.Controls.Add(calText);
            Theme.Style(calApply, "Apply suggested Gain", 240);
            calApply.Visible = false;
            calApply.Click += (s, e) =>
            {
                var p = this.getPid();
                if (p != null && calSuggest > 0)
                {
                    var n = Copy(p);
                    n.Gain = (ushort)calSuggest;
                    this.applyPid(n);
                    MessageBox.Show(page, "Applied. Use \"Save the current values in the station\" to keep it after a restart.", "Calibration", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    calApply.Visible = false;
                }
            };
            col.Controls.Add(calApply);
            RefreshSets();
        }

        private static void StyleBox(TextBox t)
        {
            t.BackColor = Theme.Field;
            t.ForeColor = Theme.Text;
            t.BorderStyle = BorderStyle.FixedSingle;
            t.Font = Theme.MakeFont(11f);
            t.Width = 200;
            t.Margin = new Padding(0, 0, 0, 6);
        }

        public int IronId
        {
            get { return ironId; }
            set
            {
                ironId = value;
                RefreshSets();
            }
        }

        private static UniSolderComm.PID Copy(UniSolderComm.PID p)
        {
            return new UniSolderComm.PID { Gain = p.Gain, Offset = p.Offset, KP = p.KP, KI = p.KI, DGain = p.DGain, OVSGain = p.OVSGain };
        }

        private static string PidTextOf(UniSolderComm.PID p)
        {
            return string.Format(CultureInfo.InvariantCulture, "Kp {0:0.00} Ki {1:0.000} D {2} OVS {3} Gain {4}", p.KP / 32767.0, p.KI / 32767.0, p.DGain, p.OVSGain, p.Gain);
        }

        private string PidText()
        {
            var p = getPid();
            return p == null ? "PID unknown" : PidTextOf(p);
        }

        /// <summary>Remember the values read from the iron the first time after connecting or changing iron.</summary>
        public void OnPidLoaded(UniSolderComm.PID p)
        {
            if (original == null) original = Copy(p);
            RefreshStored();
        }

        public void ForgetOriginal()
        {
            original = null;
        }

        public void OnDisconnected()
        {
            original = null;
            analyzer.Reset();
            if (restoreTo >= 0)
            {
                restoreTimer.Stop();
                restoreTo = -1;
            }
            testButton.Enabled = true;
            storedText.Text = "";
        }

        // ------------------------------------------------------------------ permanent storage in the station

        private void RefreshStored()
        {
            string text;
            var info = comm.GetAppInfo();
            if (info == null) text = "Cannot tell: no answer from the station.";
            else if (info.Protocol < 2) text = "This station firmware cannot store PID values. Update the firmware.";
            else
            {
                int saved = comm.PidSaved();
                if (saved == 1) text = "Values are saved in the station for this iron.";
                else if (saved == 0) text = "No values saved in the station for this iron: it uses its built-in values.";
                else text = "Cannot tell: no answer from the station.";
            }
            storedText.Text = text;
        }

        private void SaveInStation()
        {
            if (getPid() == null)
            {
                MessageBox.Show(page, "No PID values read from the iron.", "Save in the station", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var answer = MessageBox.Show(page, "Store " + PidText() + " in the station for this iron? It uses them every time this iron is detected, until you forget them.",
                "Save in the station", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (answer != DialogResult.OK) return;
            int r = comm.PidSave();
            string msg;
            switch (r)
            {
                case 0: msg = "Saved in the station."; break;
                case 1: msg = "No iron detected."; break;
                case 2: msg = "The station refused the values: out of range."; break;
                case 3: msg = "The station has no free space for another iron."; break;
                default: msg = "No answer from the station: firmware too old?"; break;
            }
            SSComm.Log.Info("Save PID in station: " + msg);
            MessageBox.Show(page, msg, "Save in the station", MessageBoxButtons.OK, r == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            RefreshStored();
        }

        private void ForgetInStation()
        {
            int r = comm.PidClear();
            string msg;
            switch (r)
            {
                case 0: msg = "Standard values restored (the saved values were removed)."; break;
                case 1: msg = "No iron detected."; break;
                case 2: msg = "Standard values restored."; break;
                default: msg = "No answer from the station: firmware too old?"; break;
            }
            SSComm.Log.Info("Restore standard PID: " + msg);
            MessageBox.Show(page, msg, "Restore the standard values", MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (r == 0 || r == 2)
            {
                original = null;
                reloadTimer.Stop();
                reloadTimer.Start();      //the station identifies the iron again, then the sliders are read back
            }
            RefreshStored();
        }

        // ------------------------------------------------------------------ live data

        public void OnSample(LiveSample s)
        {
            lastTemp = s.Temp;
            lastReached = s.Reached;
            var r = analyzer.Add(Environment.TickCount64, s, PidText());
            if (r != null)
            {
                results.Insert(0, r.Text());
                while (results.Count > 5) results.RemoveAt(results.Count - 1);
                resultText.Text = string.Join("\r\n\r\n", results);
                SSComm.Log.Info("Step measured: " + r.Text().Replace("\r\n", " "));
                if (restoreTo >= 0) RestoreTemp();
            }
            else if (!analyzer.IsRunning && restoreTo >= 0 && !testWaiting)
            {
                //the step was abandoned (set temperature moved again): put the temperature back anyway
                RestoreTemp();
            }
        }

        // ------------------------------------------------------------------ step test

        private void StartStepTest()
        {
            if (!comm.Transport.Connected)
            {
                MessageBox.Show(page, "The station is not connected.", "Step test", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!lastReached)
            {
                MessageBox.Show(page, "Wait until the iron has reached its set temperature.", "Step test", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            testButton.Enabled = false;
            var st = comm.GetState();
            string msg = null;
            int from = 0;
            if (st == null) msg = "No answer from the station.";
            else if (st.InMenu || st.Standby || st.ManualSleep) msg = "The station is in its menu or asleep.";
            else if (st.Set + 30 > st.Max) msg = "The set temperature is too close to the maximum for a +30°C step.";
            else
            {
                from = st.Set;
                int applied = comm.SetTemperature(st.Set + 30);
                if (applied < 0 || applied - from < 20) msg = "The station did not accept the new set temperature.";
            }
            if (msg != null)
            {
                testButton.Enabled = true;
                MessageBox.Show(page, msg, "Step test", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            restoreTo = from;
            testWaiting = true;
            //the analyzer starts by itself when the live data shows the rise; give up and restore after 2 minutes
            restoreTimer.Stop();
            restoreTimer.Start();
            waitTimer.Stop();
            waitTimer.Start();
            resultText.Text = "Step test running ...\r\n\r\n" + string.Join("\r\n\r\n", results);
        }

        private void RestoreTemp()
        {
            int back = restoreTo;
            restoreTo = -1;
            restoreTimer.Stop();
            testButton.Enabled = true;
            if (back < 0) return;
            int ok = comm.SetTemperature(back);
            SSComm.Log.Info("Step test: set temperature restored to " + back + (ok < 0 ? " (no answer)" : ""));
            if (ok < 0) MessageBox.Show(page, "Could not restore the set temperature: check the station.", "Step test", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // ------------------------------------------------------------------ saved sets

        private string Key { get { return "pidsets_" + ironId; } }

        private List<KeyValuePair<string, UniSolderComm.PID>> LoadSets()
        {
            var list = new List<KeyValuePair<string, UniSolderComm.PID>>();
            foreach (var item in AppPrefs.GetString(Key, "").Split(';'))
            {
                var c = item.Split('|');
                if (c.Length != 6) continue;
                int[] v = new int[5];
                bool ok = true;
                for (int i = 0; i < 5; i++) if (!int.TryParse(c[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out v[i]) || v[i] < 0) ok = false;
                if (!ok) continue;
                list.Add(new KeyValuePair<string, UniSolderComm.PID>(c[0], new UniSolderComm.PID
                {
                    KP = (ushort)v[0], KI = (ushort)v[1], DGain = (byte)v[2], OVSGain = (byte)v[3], Gain = (ushort)v[4]
                }));
            }
            return list;
        }

        private void StoreSets(List<KeyValuePair<string, UniSolderComm.PID>> list)
        {
            var parts = new List<string>();
            foreach (var kv in list)
                parts.Add(kv.Key + "|" + kv.Value.KP + "|" + kv.Value.KI + "|" + kv.Value.DGain + "|" + kv.Value.OVSGain + "|" + kv.Value.Gain);
            AppPrefs.PutString(Key, string.Join(";", parts));
        }

        private void AskSetName()
        {
            var p = getPid();
            if (p == null)
            {
                MessageBox.Show(page, "No PID values read from the iron.", "Save PID set", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (var dlg = new PromptForm("Save PID set", "Name, e.g. T15 fine tip"))
            {
                if (dlg.ShowDialog(page) != DialogResult.OK) return;
                string name = dlg.Value.Replace('|', ' ').Replace(';', ' ').Replace('\n', ' ').Replace('\r', ' ').Trim();
                if (name.Length == 0) return;
                var list = LoadSets().FindAll(kv => kv.Key != name);
                list.Add(new KeyValuePair<string, UniSolderComm.PID>(name, Copy(p)));
                StoreSets(list);
                RefreshSets();
            }
        }

        private void RefreshSets()
        {
            setsBox.SuspendLayout();
            while (setsBox.Controls.Count > 0)
            {
                var c = setsBox.Controls[0];
                setsBox.Controls.RemoveAt(0);
                c.Dispose();
            }
            var list = LoadSets();
            if (list.Count == 0)
            {
                setsBox.Controls.Add(Theme.Body("No saved sets for iron ID 0x" + ironId.ToString("X") + "."));
            }
            foreach (var kv in list)
            {
                string name = kv.Key;
                var p = kv.Value;
                var label = new Label
                {
                    AutoSize = true,
                    Text = name + "\r\n" + PidTextOf(p),
                    Font = Theme.MakeFont(10f),
                    ForeColor = Theme.Text,
                    BackColor = Theme.Bg,
                    Margin = new Padding(0, 6, 16, 0),
                    MaximumSize = new Size(420, 0)
                };
                var load = new ThemedButton();
                Theme.Style(load, "Load", 90, 40);
                load.Click += (s, e) =>
                {
                    var cur = getPid();
                    if (cur == null)
                    {
                        MessageBox.Show(page, "No PID values read from the iron.", "PID set", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    applyPid(new UniSolderComm.PID { Gain = p.Gain, Offset = cur.Offset, KP = p.KP, KI = p.KI, DGain = p.DGain, OVSGain = p.OVSGain });
                };
                var del = new ThemedButton();
                Theme.Style(del, "Delete", 90, 40);
                del.Click += (s, e) =>
                {
                    StoreSets(LoadSets().FindAll(x => x.Key != name));
                    RefreshSets();
                };
                setsBox.Controls.Add(Theme.Row(label, load, del));
            }
            setsBox.ResumeLayout(true);
            page.Fit();
        }

        // ------------------------------------------------------------------ calibration

        private void SuggestGain()
        {
            var p = getPid();
            float refT, amb;
            bool okRef = float.TryParse(calRef.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out refT);
            bool okAmb = float.TryParse(calAmb.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out amb);
            calApply.Visible = false;
            if (p == null)
            {
                calText.Text = "No PID values read from the iron.";
                return;
            }
            if (!okRef || !okAmb || refT <= amb + 30f)
            {
                calText.Text = "Enter the reference temperature (it must be well above the room temperature) and the room temperature.";
                return;
            }
            if (!lastReached || lastTemp <= amb + 30f)
            {
                calText.Text = "The iron must be heated up and steady at its set temperature.";
                return;
            }
            float raw = p.Gain * (lastTemp - amb) / (refT - amb);
            int n = Math.Max(8, Math.Min(256, (int)Math.Round(raw)));
            calSuggest = n;
            calText.Text = string.Format(CultureInfo.InvariantCulture, "The station shows {0:0}°C, the reference {1:0}°C. Gain {2} → {3} ({4:+0.0;-0.0;0.0}%).",
                lastTemp, refT, p.Gain, n, (n - p.Gain) * 100f / p.Gain);
            if (n != p.Gain) calApply.Visible = true;
        }
    }
}
