
using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using SSControls;

namespace UniSolder
{

    partial class Form1
    {
        int CPoint = 0;

        private UniSolderComm lUniSolder = null;
        private SSComm.USBHID usb;
        private SSComm.BtSerial bt;
        private DateTime btNext = DateTime.MinValue;     //next Bluetooth attempt (a failed one is repeated every 8 seconds)
        private bool btBusy;       //a Bluetooth connection attempt is running on a worker thread
        private MoreTab moreTab;
        private UniSolderComm.PID PID = null;

        private System.Windows.Forms.Timer ConnectionTimer;
        private int pidTries;
        private DateTime pidNext = DateTime.MinValue;

        public Form1()
        {
            Disposed += Form1_Disposed;
            InitializeComponent();
            Theme.ApplyIcon(this);
            SSComm.Log.MessageLogged += Log_MessageLogged;
            logStatusLabel.Text = "Log: " + SSComm.Log.FilePath;

            lUniSolder = new UniSolderComm();
            lUniSolder.InstrumentChange += InstrumentChange;
            lUniSolder.LiveDataReceived += LiveDataReceived;
            usb = new SSComm.USBHID { DevVID = 0x4d8, DevPID = 0x3c };
            bt = new SSComm.BtSerial();
            lUniSolder.Transport = usb;

            BuildTabs();

            ConnectionTimer = new System.Windows.Forms.Timer() { Interval = 1000, Enabled = true };
            ConnectionTimer.Tick += ConnectionTimer_Tick;
        }

        private PillNav nav;
        private Control[] pages;
        private ControlTab controlTab;
        private ToolsTab toolsTab;
        private bool wasConnected;

        /// <summary>
        /// Same pages as the Android app, with its look: dark background, yellow headings and rounded buttons, and a page bar at the
        /// bottom. The controls made by the designer are moved to their page here:
        /// Live = chart and trace tick boxes (+ stop/start), PID = sliders, Firmware = query device and update.
        /// </summary>
        private void BuildTabs()
        {
            SuspendLayout();
            Font = Theme.MakeFont(10f);
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            HandleCreated += (s, e) => Theme.DarkTitle(this);

            //status bar
            statusStrip1.Renderer = new DarkRenderer();
            statusStrip1.BackColor = Theme.Card;
            statusStrip1.ForeColor = Theme.Muted;
            statusStrip1.Font = Theme.MakeFont(9f);
            connStatusLabel.ForeColor = Theme.Muted;
            logStatusLabel.ForeColor = Theme.Muted;

            //Live: chart on the left, trace tick boxes and buttons on the right
            buttonsPanel.Controls.Remove(Button8);
            bottomPanel.Controls.Remove(buttonsPanel);
            mainLayout.Controls.Remove(bottomPanel);
            mainLayout.RowCount = 1;
            mainLayout.RowStyles.Clear();
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            Controls.Remove(mainLayout);
            mainLayout.BackColor = Theme.Bg;
            mainLayout.Padding = new Padding(16, 8, 16, 8);
            panel1.BackColor = Theme.Bg;
            panel1.Font = Theme.MakeFont(10f);
            panel1.Padding = new Padding(16, 12, 0, 6);
            foreach (Control c in panel1.Controls) c.BackColor = Theme.Bg;

            Theme.Style(Button8, "STOP", 170);
            Button8.Margin = new Padding(0, 16, 0, 0);
            panel1.Controls.Add(Button8);
            //record and dashboard buttons, like the Android app
            var recButton = new ThemedButton { Strong = true };
            Theme.Style(recButton, "Record", 170);
            recButton.Margin = new Padding(0, 8, 0, 0);
            var dashButton = new ThemedButton();
            Theme.Style(dashButton, "Dashboard", 170);
            dashButton.Margin = new Padding(0, 8, 0, 0);
            panel1.Controls.Add(recButton);
            panel1.Controls.Add(dashButton);
            var live = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            live.Controls.Add(mainLayout);

            //PID: one row per slider, label on the left
            var pid = new Page();
            pid.Col.Controls.Add(Theme.Heading("PID"));
            var table = new TableLayoutPanel();
            table.Tag = "fill";
            table.ColumnCount = 2;
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30F));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70F));
            var pidRows = new[]
            {
                new KeyValuePair<Label, Control>(GLabel, GTrackBar),
                new KeyValuePair<Label, Control>(OVFGLabel, OVFGTrackBar),
                new KeyValuePair<Label, Control>(KpLabel, KpTrackBar),
                new KeyValuePair<Label, Control>(KiLabel, KiTrackBar),
                new KeyValuePair<Label, Control>(DGLabel, DGTrackBar)
            };
            table.RowCount = pidRows.Length;
            table.BackColor = Theme.Bg;
            table.Margin = new Padding(0);
            table.Height = 64 * pidRows.Length;
            table.Width = 400;
            for (int i = 0; i < pidRows.Length; i++)
            {
                table.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
                var label = pidRows[i].Key;
                var bar = pidRows[i].Value;
                label.Parent?.Controls.Remove(label);
                bar.Parent?.Controls.Remove(bar);
                label.AutoSize = false;
                label.Anchor = AnchorStyles.None;
                label.Dock = DockStyle.Fill;
                label.Margin = new Padding(0);
                label.TextAlign = ContentAlignment.MiddleLeft;
                label.Font = Theme.MakeFont(11f);
                label.ForeColor = Theme.Text;
                label.BackColor = Theme.Bg;
                bar.AutoSize = false;
                bar.Anchor = AnchorStyles.None;
                bar.Dock = DockStyle.Fill;
                bar.Margin = new Padding(8, 0, 8, 0);
                bar.BackColor = Theme.Bg;
                table.Controls.Add(label, 0, i);
                table.Controls.Add(bar, 1, i);
            }
            pid.Col.Controls.Add(table);

            //Firmware
            var firmware = new Page();
            firmware.Col.Controls.Add(Theme.Heading("Firmware"));
            Theme.Style(Button6, "Query device", 200);
            Theme.Style(Button3, "Update Firmware", 220);
            firmware.Col.Controls.Add(Theme.Row(Button6, Button3));

            controlTab = new ControlTab(lUniSolder);
            toolsTab = new ToolsTab();
            recButton.Click += (s, e) => toolsTab.ToggleRecording();
            dashButton.Click += (s, e) => toolsTab.ShowDashboard();
            toolsTab.RecordingChanged += on => recButton.Text = on ? "Stop rec." : "Record";

            moreTab = new MoreTab();
            moreTab.BluetoothSaved += ApplyBluetoothSettings;
            ApplyBluetoothSettings();
            pages = new Control[] { live, controlTab, pid, toolsTab, firmware, moreTab };
            var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            foreach (var p in pages)
            {
                p.Dock = DockStyle.Fill;
                p.Visible = false;
                host.Controls.Add(p);
            }
            nav = new PillNav(new[] { "Live", "Control", "PID", "Tools", "Firmware", "More" });
            nav.PageSelected += ShowPage;
            Controls.Add(nav);
            Controls.Add(host);
            Controls.SetChildIndex(host, 0);
            Controls.SetChildIndex(statusStrip1, Controls.Count - 1);
            ShowPage(0);
            nav.Select(0);
            ResumeLayout(true);
        }

        private void ShowPage(int index)
        {
            for (int i = 0; i < pages.Length; i++) pages[i].Visible = i == index;
            if (pages[index] == toolsTab) toolsTab.RefreshSessions();
        }

        /// <summary>Takes the saved Bluetooth settings; a changed PIN or port allows a new attempt after a rejected PIN.</summary>
        private void ApplyBluetoothSettings()
        {
            bool changed = bt.PortName != moreTab.SavedPort || bt.Pin != moreTab.SavedPin;
            bt.PortName = moreTab.SavedPort;
            bt.Pin = moreTab.SavedPin;
            bt.PinRejected = false;
            if (btBusy) return;
            if ((!moreTab.UseBluetooth || changed) && lUniSolder.Transport == bt) bt.Disconnect();
        }

        private bool UseBluetooth => moreTab != null && moreTab.UseBluetooth && !string.IsNullOrEmpty(bt.PortName);

        private void ConnectionTimer_Tick(object sender, EventArgs e)
        {
            if (!btBusy)
            {
                var t = lUniSolder.Transport;
                if (!t.Connected)
                {
                    //USB first; when there is no cable, Bluetooth (on a worker thread: opening the port and the PIN check take seconds)
                    if (usb.Connect())
                    {
                        lUniSolder.Transport = usb;
                        InstrumentChange(null, null);
                    }
                    else if (UseBluetooth && !bt.PinRejected && DateTime.Now >= btNext)
                    {
                        btBusy = true;
                        lUniSolder.Transport = bt;
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            bool ok = false;
                            try { ok = bt.Connect(); }
                            catch (Exception ex) { SSComm.Log.Error("Bluetooth connect failed", ex); }
                            try
                            {
                                BeginInvoke(new Action(() =>
                                {
                                    btBusy = false;
                                    if (!ok) btNext = DateTime.Now.AddSeconds(8);
                                    if (ok) InstrumentChange(null, null);
                                    UpdateConnectionStatus();
                                }));
                            }
                            catch (InvalidOperationException) { btBusy = false; }      //window closed
                        });
                    }
                }
                else if (t == bt && usb.Connect())
                {
                    //a USB cable always wins
                    bt.Disconnect();
                    lUniSolder.Transport = usb;
                    InstrumentChange(null, null);
                }
            }
            UpdateConnectionStatus();
            bool connected = lUniSolder.Transport.Connected;
            //over Bluetooth the first PID query can go unanswered (the link is only just up): ask again a few times, the sliders need it
            if (connected && !btBusy && lUniSolder.Transport == bt && PID == null)
            {
                if (pidTries < 6 && DateTime.Now >= pidNext)
                {
                    pidTries++;
                    pidNext = DateTime.Now.AddSeconds(2);
                    SSComm.Log.Info("Bluetooth: asking for the PID parameters again (try " + pidTries + ")");
                    InstrumentChange(null, null);
                }
            }
            else if (!connected || PID != null)
            {
                pidTries = 0;
            }
            if (wasConnected && !connected) toolsTab?.OnDisconnected();
            wasConnected = connected;
        }

        private void Form1_Disposed(object sender, EventArgs e)
        {
            //static event: unsubscribe so the log does not call a disposed form
            SSComm.Log.MessageLogged -= Log_MessageLogged;
            usb?.Dispose();
            bt?.Dispose();
        }

        private void UpdateConnectionStatus()
        {
            bool viaBt = lUniSolder.Transport == bt;
            string how = viaBt ? " (Bluetooth)" : "";
            if (moreTab != null)
            {
                if (!UseBluetooth) moreTab.SetStatus("Bluetooth is off");
                else if (viaBt && bt.Connected) moreTab.SetStatus("Connected over Bluetooth");
                else if (btBusy) moreTab.SetStatus("Connecting over Bluetooth...");
                else if (bt.PinRejected) moreTab.SetStatus("Bluetooth: wrong PIN (change it and save to try again)");
                else if (usb.Connected) moreTab.SetStatus("Connected over USB (Bluetooth is not needed)");
                else moreTab.SetStatus("Bluetooth: " + (string.IsNullOrEmpty(bt.LastError) ? "waiting for the bridge" : bt.LastError));
            }
            if (btBusy)
            {
                connStatusLabel.Text = "Connecting over Bluetooth...";
            }
            else if (!lUniSolder.Transport.Connected)
            {
                connStatusLabel.Text = viaBt && !string.IsNullOrEmpty(bt.LastError) ? "Bluetooth: " + bt.LastError : "Not connected";
            }
            else if (PID == null)
            {
                //connected but the PID query failed: typically the device is in bootloader mode
                connStatusLabel.Text = "Connected" + how + " - no answer (bootloader?)";
            }
            else
            {
                connStatusLabel.Text = "Connected" + how;
            }
        }

        private void Log_MessageLogged(string level, string msg)
        {
            //messages come from any thread; before the handle exists InvokeRequired cannot be trusted
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string, string>(Log_MessageLogged), level, msg); }
                catch (InvalidOperationException) { } //form closing
                return;
            }
            var firstLine = msg.Split(new[] { '\r', '\n' }, 2)[0];
            logStatusLabel.Text = DateTime.Now.ToString("HH:mm:ss") + "  " + (level == "INFO" ? "" : level + ": ") + firstLine;
            logStatusLabel.ToolTipText = level + ": " + msg + "\n\nDouble-click to open the log file";
        }

        private void StatusBar_DoubleClick(object sender, EventArgs e)
        {
            var path = SSComm.Log.FilePath;
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            {
                MessageBox.Show("The log file is not available.", "UniSolder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                //no application associated with .log files
                Process.Start("notepad.exe", "\"" + path + "\"");
            }
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            SsChart2.DrawingsNum = 13;
            var _with1 = SsChart2.Scales(0);
            _with1.ValFrom = 0;
            _with1.ValTo = 5.11F;
            _with1.ValMinStep = 1F / 50F;
            _with1.ValMajStep = 1F;
            _with1.ValFormat = "0";

            var _with2 = SsChart2.Scales(1);
            _with2.ValFrom = 0;
            _with2.ValTo = 500;
            _with2.ValMinStep = 10;
            _with2.ValMajStep = 50;
            _with2.ValFormat = "0";

            var _with3 = SsChart2.Drawings(0);
            _with3.XScale = SsChart2.Scales(0);
            _with3.Yscale = SsChart2.Scales(1);
            _with3.Pen.Color = Color.Yellow;
            _with3.Pen.Width = 1;
            _with3.PointsNum = 2;
            _with3.Points[0].X = _with3.XScale.ValFrom;
            _with3.Points[0].Y = _with3.Yscale.ValFrom;
            _with3.Points[1].X = _with3.XScale.ValFrom;
            _with3.Points[1].Y = _with3.Yscale.ValTo;

            foreach (var d in SsChart2.Drawings1)
            {
                if (d != SsChart2.Drawings1[0])
                {
                    d.XScale = SsChart2.Scales(0);
                    d.Yscale = SsChart2.Scales(1);
                    d.Pen.Width = 2;
                    d.PointsNum = 256;
                    for (var i = 0; i <= 255; i++)
                    {
                        d.Points[i].X = i / 50F;
                        d.Points[i].Y = 10;
                    }
                }
            }

            SsChart2.Drawings(1).Pen.Color = Color.DarkRed;
            SsChart2.Drawings(2).Pen.Color = Color.Red;
            SsChart2.Drawings(3).Pen.Color = Color.LightGreen;
            SsChart2.Drawings(4).Pen.Color = Color.SkyBlue;
            SsChart2.Drawings(5).Pen.Color = Color.Magenta;
            SsChart2.Drawings(6).Pen.Color = Color.Yellow;
            SsChart2.Drawings(7).Pen.Color = Color.Blue;
            SsChart2.Drawings(8).Pen.Color = Color.LightGray;
            SsChart2.Drawings(9).Pen.Color = Color.Orange;
            SsChart2.Drawings(10).Pen.Color = Color.White;
            SsChart2.Drawings(11).Pen.Color = Color.Green;
            SsChart2.Drawings(12).Pen.Color = Color.DarkSlateBlue;


            //Debug.Print("Connecting.....");
            //Debug.Print(lUniSolder.Transport.Connect().ToString());
            //Dim aa(64) As Byte
            //aa(0) = 0
            //lUniSolder.TransLayer.Write(aa, 0, 64)
            //lUniSolder.TransLayer.Write(aa, 0, 64)

            System.Threading.Thread.Sleep(500);
            InstrumentChange(null, null);
            //PID = lUniSolder.AppGetPIDParameters()
            //KpTrackBar.Value = PID.KP
            //KiTrackBar.Value = PID.KI
            //DGTrackBar.Value = PID.DGain
            //OVFGTrackBar.Value = PID.OVSGain
            //GTrackBar.Value = PID.Gain
            //Dim b(64) As Byte
            //b(0) = 4
            //ud.Write(b, 0, 64)
            //Timer1.Enabled = True
        }

        public delegate void InstrumentChangeDelegate(object sender, EventArgs e);
        private void InstrumentChange(object sender, EventArgs e)
        {
            if ((KpTrackBar.InvokeRequired))
            {
                KpTrackBar.BeginInvoke(new InstrumentChangeDelegate(InstrumentChange), sender, e);
            }
            else
            {
                if (toolsTab != null)
                {
                    toolsTab.IronId = lUniSolder.IronId;
                    toolsTab.ShowTip();
                }
                //PID stays null while the sliders are updated, so ValueChanged does not send anything
                PID = null;
                var pid = lUniSolder.Transport.Connected ? lUniSolder.AppGetPIDParameters() : null;
                bool valid = pid != null;
                foreach (var tb in new[] { KpTrackBar, KiTrackBar, DGTrackBar, OVFGTrackBar, GTrackBar }) tb.Enabled = valid;
                if (!valid)
                {
                    SSComm.Log.Warn("PID parameters not available (device not connected or in bootloader), sliders disabled.");
                    UpdateConnectionStatus();
                    return;
                }
                SetTrackBar(KpTrackBar, pid.KP);
                SetTrackBar(KiTrackBar, pid.KI);
                SetTrackBar(DGTrackBar, pid.DGain);
                SetTrackBar(OVFGTrackBar, pid.OVSGain);
                SetTrackBar(GTrackBar, pid.Gain);
                PID = pid;
                UpdateConnectionStatus();
            }
        }

        private static void SetTrackBar(TrackBar tb, int value)
        {
            tb.Value = Math.Max(tb.Minimum, Math.Min(tb.Maximum, value));
        }

        public delegate void LiveDataReceivedDelegate(object sender, UniSolderComm.LiveDataReceivedEventData e);
        private void LiveDataReceived(object sender, UniSolderComm.LiveDataReceivedEventData e)
        {
            if (SsChart2.InvokeRequired)
            {
                SsChart2.BeginInvoke(new LiveDataReceivedDelegate(LiveDataReceived), sender, e);
            }
            else
            {
                var b = e.Data;
                if (b[0] == 3 && toolsTab != null)
                {
                    var sample = LiveSample.Parse(b);
                    if (sample != null) toolsTab.OnSample(sample);
                }
                switch (b[0])
                {
                    case 3:
                        if (Button8.Text != "START")
                        {
                            SsChart2.Drawings(0).Points[0].X = SsChart2.Drawings(1).Points[CPoint].X;
                            SsChart2.Drawings(0).Points[1].X = SsChart2.Drawings(1).Points[CPoint].X;

                            //CTTemp
                            SsChart2.Drawings(2).Points[CPoint].Y = b[3] * 2.0F;

                            //CTemp
                            SsChart2.Drawings(3).Points[CPoint].Y = b[4] * 0.5F + b[5] * 128.0F;

                            //ADCTemp
                            SsChart2.Drawings(4).Points[CPoint].Y = b[6] * 0.5F + b[7] * 128.0F;

                            //TAvgF
                            SsChart2.Drawings(5).Points[CPoint].Y = b[8] * 0.5F + b[9] * 128.0F;

                            //CHRes - Heater resistance x10
                            SsChart2.Drawings(8).Points[CPoint].Y = (short)(b[10] | (b[11] << 8));

                            //TAvgP
                            SsChart2.Drawings(9).Points[CPoint].Y = b[12] * 0.5F + b[13] * 128.0F;

                            //Power On/Off
                            SsChart2.Drawings(7).Points[CPoint].Y = b[14] != 0 ? 100.0F : 0.0F;

                            //WSDelta
                            SsChart2.Drawings(10).Points[0].Y = (b[15] * 1.0F + b[16] * 256.0F - 2048.0F) / 4.0F + 200.0F;
                            SsChart2.Drawings(10).Points[1].Y = (b[17] * 1.0F + b[18] * 256.0F - 2048.0F) / 4.0F + 200.0F;
                            SsChart2.Drawings(10).Points[2].Y = (b[19] * 1.0F + b[20] * 256.0F - 2048.0F) / 4.0F + 200.0F;
                            SsChart2.Drawings(10).Points[3].Y = (b[21] * 1.0F + b[22] * 256.0F - 2048.0F) / 4.0F + 200.0F;
                            SsChart2.Drawings(10).Points[4].Y = (b[23] * 1.0F + b[24] * 256.0F - 2048.0F) / 4.0F + 200.0F;
                            SsChart2.Drawings(10).Points[5].Y = (b[25] * 1.0F + b[26] * 256.0F - 2048.0F) / 4.0F + 200.0F;
                            SsChart2.Drawings(10).Points[6].Y = (b[27] * 1.0F + b[28] * 256.0F - 2048.0F) / 4.0F + 200.0F;

                            //DestinationReached
                            SsChart2.Drawings(11).Points[CPoint].Y = b[31] * 100.0F;

                            //Duty
                            SsChart2.Drawings(1).Points[CPoint].Y = (b[32] * 1.0F + b[33] * 256.0F) / 128.0F;

                            ////SsChart2.Drawings(12).Points(CPoint).Y = (b(34) * 1.0 + b(35) * 256.0);
                            //TAvgF
                            //SsChart2.Drawings(6).Points(CPoint).Y = (b(10) * 1.0 + b(11) * 256.0 - 2048.0) / 4.0 + 200.0

                            CPoint = (CPoint + 1) % 256;
                        }
                        break;
                }
                if (!SsChart2.redrawactive)
                {
                    SsChart2.Drawings(2).Visible = checkBox1.Checked; //CTTemp
                    SsChart2.Drawings(3).Visible = checkBox2.Checked; //CTemp
                    SsChart2.Drawings(4).Visible = checkBox3.Checked; //ADCTemp
                    SsChart2.Drawings(5).Visible = checkBox4.Checked; //TAvgF
                    SsChart2.Drawings(9).Visible = checkBox6.Checked; //TavgP
                    SsChart2.Drawings(11).Visible = checkBox7.Checked; //DestinationReached
                    SsChart2.Drawings(1).Visible = checkBox8.Checked; //Duty
                    SsChart2.Drawings(8).Visible = checkBox9.Checked; //CHRes
                    SsChart2.Drawings(7).Visible = checkBox10.Checked; //Power On/Off
                    SsChart2.Drawings(10).Visible = checkBox11.Checked; //WsDelta

                    SsChart2._Redraw();
                }
            }
        }

        private void Button3_Click(object sender, EventArgs e)
        {
            if (lUniSolder.Transport == bt && bt.Connected)
            {
                MessageBox.Show("The firmware cannot be updated over Bluetooth. Connect the USB cable and try again.", "UniSolder", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var fd = new OpenFileDialog()
            {
                CheckFileExists = true,
                CheckPathExists = true,
                Multiselect = false,
                Filter = "HEX Files|*.hex",
                Title = "Select HEX file to upload.",
                FilterIndex = 0
            };
            if (fd.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            //all checks are done before anything is sent to the device
            SSComm.Log.Info("Firmware update: loading " + fd.FileName);
            HexFileManager ss = new HexFileManager();
            if (!ss.LoadHexFile(fd.FileName))
            {
                FirmwareUpdateFailed("Invalid HEX file: " + ss.LastError, FlashState.Untouched);
                return;
            }
            var problem = ss.Validate();
            if (problem != null)
            {
                FirmwareUpdateFailed("This HEX file cannot be used: " + problem, FlashState.Untouched);
                return;
            }
            long totalBytes = 0;
            UInt32 appEnd = 0;
            foreach (var r in ss.Records)
            {
                totalBytes += r.RecDataLen;
                if (r.Address >= HexFileManager.APP_FLASH_START && r.Address < 0x1D100000) appEnd = Math.Max(appEnd, r.Address + r.RecDataLen - 1);
            }
            SSComm.Log.Info("Firmware update: HEX file valid (checksums OK, PIC32_with_bootloader layout), " + ss.Records.Count + " records, " + totalBytes +
                " bytes, application 0x" + HexFileManager.APP_FLASH_START.ToString("X8") + "-0x" + appEnd.ToString("X8"));
            if (!lUniSolder.Transport.Connected)
            {
                FirmwareUpdateFailed("The device is not connected.", FlashState.Untouched);
                return;
            }

            var answer = MessageBox.Show(
                "Update the device firmware with:\n\n" + System.IO.Path.GetFileName(fd.FileName) +
                "\n" + totalBytes + " bytes, modified " + System.IO.File.GetLastWriteTime(fd.FileName).ToString("g") +
                "\n\nDo not disconnect the device or close this application until the update has finished.\n\nContinue?",
                "Firmware update", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != System.Windows.Forms.DialogResult.Yes)
            {
                SSComm.Log.Info("Firmware update: cancelled by the user");
                return;
            }

            var oldCursor = Cursor;
            Cursor = Cursors.WaitCursor;
            try
            {
                UpdateFirmware(ss);
            }
            finally
            {
                Cursor = oldCursor;
            }
        }

        private enum FlashState { Untouched, MaybeErased }

        private void FirmwareUpdateFailed(string reason, FlashState state)
        {
            SSComm.Log.Error("Firmware update failed: " + reason + (state == FlashState.Untouched ? " (firmware not modified)" : " (device left in bootloader mode)"));
            var msg = reason + "\n\n";
            if (state == FlashState.Untouched)
            {
                msg += "The device firmware has not been modified.";
            }
            else
            {
                //PROGRAM_COMPLETE was not sent: the bootloader will not start a partially written firmware
                msg += "The firmware may have been erased or partially written, so it has NOT been marked as valid: " +
                       "the device stays in bootloader mode, which is safe.\n\nRetry the update. If the device does not reconnect, " +
                       "power-cycle it; it will restart in bootloader mode.";
            }
            MessageBox.Show(msg + "\n\nDetails in the log file:\n" + SSComm.Log.FilePath, "Firmware update failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private void UpdateFirmware(HexFileManager ss)
        {
            var state = FlashState.Untouched;
            try
            {
                Stopwatch sst = Stopwatch.StartNew();
                byte b = 0;
                lUniSolder.DevGetOpMode(ref b);
                if (b != 16)
                {
                    SSComm.Log.Info("Firmware update: device in application mode (" + b + "), jumping to bootloader");
                    lUniSolder.AppJumpToBootloader();
                    Thread.Sleep(500);
                    lUniSolder.Transport.Disconnect();
                    for (int i = 0; i < 20; i++)
                    {
                        if (lUniSolder.Transport.Connect()) break;
                        Thread.Sleep(500);
                    }
                    if (!lUniSolder.Transport.Connected)
                    {
                        FirmwareUpdateFailed("The device did not reconnect after the jump to bootloader mode. Power-cycle it and retry.", state);
                        return;
                    }
                    SSComm.Log.Info("Firmware update: reconnected, waiting for bootloader mode");
                    for (int i = 0; i < 20 && b != 16; i++)
                    {
                        lUniSolder.DevGetOpMode(ref b);
                    }
                    if (b != 16)
                    {
                        FirmwareUpdateFailed("The device did not enter bootloader mode (operating mode " + b + "). Power-cycle it and retry.", state);
                        return;
                    }
                }

                //from here on the application CRC is erased: any interruption leaves the device in bootloader mode
                state = FlashState.MaybeErased;
                SSComm.Log.Info("Firmware update: erasing flash...");
                var eResult = lUniSolder.BlEraseFlash(out int eStatus);
                //status byte: log only until it has been confirmed to be 0 on success with real devices
                if (eResult == 0) SSComm.Log.Info("Firmware update: erase bootloader status 0x" + eStatus.ToString("X2") + (eStatus == 0 ? "" : " (non-zero, log only: not acted upon)"));
                if (eResult != 0)
                {
                    FirmwareUpdateFailed("No answer from the bootloader to the erase command.", state);
                    return;
                }
                SSComm.Log.Info("Firmware update: erase completed in " + sst.Elapsed.ToString());

                SSComm.Log.Info("Firmware update: programming started...");
                sst.Restart();
                int programmed = 0, nonZeroStatus = 0;
                foreach (var r in ss.Records)
                {
                    var cResult = lUniSolder.BlProgramFlash(r.Address, ref r.Data, 0, (int)r.RecDataLen, out int pStatus);
                    if (cResult != 0)
                    {
                        FirmwareUpdateFailed("No answer from the bootloader while programming address 0x" + r.Address.ToString("X8") +
                            " (record " + (programmed + 1) + " of " + ss.Records.Count + ").", state);
                        return;
                    }
                    programmed++;
                    if (pStatus != 0)
                    {
                        nonZeroStatus++;
                        if (nonZeroStatus <= 10) SSComm.Log.Warn("Firmware update: bootloader status 0x" + pStatus.ToString("X2") + " programming 0x" + r.Address.ToString("X8") + " (" + r.RecDataLen + " bytes) - log only, not acted upon");
                    }
                }
                SSComm.Log.Info("Firmware update: " + programmed + " records programmed in " + sst.Elapsed.ToString() + ", " + nonZeroStatus + " with non-zero bootloader status (log only)");

                //only reached when the erase and every record were acknowledged
                var cpResult = lUniSolder.BlProgramComplete();
                if (cpResult != 0) SSComm.Log.Warn("Firmware update: no answer to PROGRAM_COMPLETE, the bootloader will check the firmware CRC at restart");
                SSComm.Log.Info("Firmware update: jumping to application");
                lUniSolder.BlJumpToApplication();
                MessageBox.Show(cpResult == 0
                        ? "Firmware update completed. The device restarts with the new firmware."
                        : "Firmware written, but the device did not confirm the final step. If it stays in bootloader mode after restarting, retry the update.",
                    "Firmware update", MessageBoxButtons.OK, cpResult == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                SSComm.Log.Error("Firmware update: unexpected error", ex);
                FirmwareUpdateFailed("Unexpected error: " + ex.Message, state);
            }
        }


        private void Button6_Click(object sender, EventArgs e)
        {
            lUniSolder.DevQuery();
        }

        private void KpTrackBar_ValueChanged(object sender, EventArgs e)
        {
            KpLabel.Text = "Kp = " + (KpTrackBar.Value / 32767.0).ToString("0.00");
            if (PID != null && (PID.KP != KpTrackBar.Value))
            {
                PID.KP = (UInt16)KpTrackBar.Value;
                lUniSolder.AppSetPIDParameters(ref PID);
            }
        }

        private void KiTrackBar_ValueChanged(object sender, EventArgs e)
        {
            KiLabel.Text = "Ki = " + (KiTrackBar.Value / 32767.0).ToString("0.000");
            if (PID != null && (PID.KI != KiTrackBar.Value))
            {
                PID.KI = (UInt16)KiTrackBar.Value;
                lUniSolder.AppSetPIDParameters(ref PID);
            }
        }

        private void DGTrackBar_ValueChanged(object sender, EventArgs e)
        {
            DGLabel.Text = "DGain = " + DGTrackBar.Value.ToString();
            if (PID != null && (PID.DGain != DGTrackBar.Value))
            {
                PID.DGain = (byte)DGTrackBar.Value;
                lUniSolder.AppSetPIDParameters(ref PID);
            }
        }

        private void OVFGTrackBar_ValueChanged(object sender, EventArgs e)
        {
            OVFGLabel.Text = "OVFGain = " + OVFGTrackBar.Value.ToString();
            if (PID != null && (PID.OVSGain != OVFGTrackBar.Value))
            {
                PID.OVSGain = (byte)OVFGTrackBar.Value;
                lUniSolder.AppSetPIDParameters(ref PID);
            }
        }

        private void GTrackBar_ValueChanged(object sender, EventArgs e)
        {
            GLabel.Text = "Gain = " + GTrackBar.Value.ToString();
            if (PID != null && (PID.Gain != GTrackBar.Value))
            {
                PID.Gain = (UInt16)GTrackBar.Value;
                lUniSolder.AppSetPIDParameters(ref PID);
            }
        }

        private void Button8_Click(object sender, EventArgs e)
        {
            if (Button8.Text == "STOP")
            {
                Button8.Text = "START";
            }
            else
            {
                Button8.Text = "STOP";
            }
        }
    }
}