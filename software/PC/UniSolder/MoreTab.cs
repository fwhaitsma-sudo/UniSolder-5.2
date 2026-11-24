using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO.Ports;
using System.Windows.Forms;
using Microsoft.Win32;

namespace UniSolder
{
    /// <summary>
    /// More page: Bluetooth settings (COM port of the paired bridge, PIN). Like the Android app the station is reached over USB
    /// when a cable is connected, and over Bluetooth otherwise. The values are kept in the settings file of the app.
    /// </summary>
    class MoreTab : Page
    {
        public const string KeyPort = "bt_port";
        public const string KeyPin = "bt_pin";
        public const string KeyUse = "bt_use";

        private class PortItem
        {
            public string Name;
            public string Text;
            public override string ToString() { return Text; }
        }

        private readonly Label status = new Label();
        private readonly ComboBox ports = new ComboBox();
        private readonly TextBox pin = new TextBox();
        private readonly ThemedCheck use = new ThemedCheck();
        private readonly Label message = new Label();

        /// <summary>The user saved new Bluetooth settings: the connection should be tried again with them.</summary>
        public event Action BluetoothSaved;

        public MoreTab()
        {
            var col = Col;
            col.Controls.Add(Theme.Heading("Bluetooth"));

            status.AutoSize = true;
            status.Font = Theme.MakeFont(12f, FontStyle.Bold);
            status.ForeColor = Theme.Yellow;
            status.BackColor = Theme.Bg;
            status.Margin = new Padding(0, 0, 0, 6);
            status.Text = "Bluetooth is off";
            col.Controls.Add(status);

            col.Controls.Add(Theme.Hint("Needs the ESP32 bridge in the station. First pair the bridge in the Windows Bluetooth settings (the PIN of the pairing is the PIN " +
                "of the bridge, default 0000). Windows then adds two Bluetooth serial (COM) ports: choose the one for outgoing connections. " +
                "If the bridge does not answer, try the other Bluetooth port."));

            col.Controls.Add(Theme.Body("COM port of the bridge"));
            ports.DropDownStyle = ComboBoxStyle.DropDownList;
            ports.FlatStyle = FlatStyle.Flat;
            ports.BackColor = Theme.Field;
            ports.ForeColor = Theme.Text;
            ports.Font = Theme.MakeFont(10.5f);
            ports.Tag = "fill";
            ports.Margin = new Padding(0, 0, 0, 6);
            col.Controls.Add(ports);

            col.Controls.Add(Theme.Body("PIN of the bridge"));
            pin.BackColor = Theme.Field;
            pin.ForeColor = Theme.Text;
            pin.BorderStyle = BorderStyle.FixedSingle;
            pin.Font = Theme.MakeFont(11f);
            pin.MaxLength = 8;
            pin.UseSystemPasswordChar = true;
            pin.Width = 200;
            pin.Margin = new Padding(0, 0, 0, 10);
            pin.Text = AppPrefs.GetString(KeyPin, "0000");
            col.Controls.Add(pin);

            use.Text = "Use Bluetooth when no USB cable is connected";
            use.Checked = AppPrefs.GetBool(KeyUse, false);
            use.Margin = new Padding(0, 4, 0, 6);
            col.Controls.Add(use);

            var save = new ThemedButton { Strong = true };
            Theme.Style(save, "Save and connect", 220);
            save.Click += (s, e) => Save();
            var refresh = new ThemedButton();
            Theme.Style(refresh, "Refresh the ports", 220);
            refresh.Click += (s, e) => FillPorts();
            col.Controls.Add(Theme.Row(save, refresh));

            message.AutoSize = true;
            message.Font = Theme.MakeFont(10f);
            message.ForeColor = Theme.Text;
            message.BackColor = Theme.Bg;
            message.Margin = new Padding(0, 6, 0, 0);
            col.Controls.Add(message);

            col.Controls.Add(Theme.Hint("A USB cable always wins: the station is used over USB as soon as one is connected. The firmware cannot be updated over Bluetooth."));

            FillPorts();
        }

        public string SavedPort { get { return AppPrefs.GetString(KeyPort, ""); } }
        public string SavedPin { get { return AppPrefs.GetString(KeyPin, "0000"); } }
        public bool UseBluetooth { get { return AppPrefs.GetBool(KeyUse, false); } }

        /// <summary>Shows the state of the link (called by the main window).</summary>
        public void SetStatus(string text)
        {
            status.Text = text;
        }

        private void Save()
        {
            var item = ports.SelectedItem as PortItem;
            string p = pin.Text.Trim();
            bool digits = p.Length >= 4 && p.Length <= 8;
            foreach (char c in p) if (c < '0' || c > '9') digits = false;
            if (!digits)
            {
                message.Text = "The PIN must be 4 to 8 digits.";
                return;
            }
            if (use.Checked && item == null)
            {
                message.Text = "Choose the COM port of the bridge first.";
                return;
            }
            AppPrefs.PutString(KeyPort, item != null ? item.Name : "");
            AppPrefs.PutString(KeyPin, p);
            AppPrefs.PutBool(KeyUse, use.Checked);
            message.Text = use.Checked ? "Saved. Bluetooth is tried when no USB cable is connected." : "Saved. Bluetooth is off.";
            BluetoothSaved?.Invoke();
        }

        private void FillPorts()
        {
            string wanted = AppPrefs.GetString(KeyPort, "");
            var bt = BluetoothPorts();
            var items = new List<PortItem>();
            string[] names;
            try { names = SerialPort.GetPortNames(); }
            catch (Exception) { names = new string[0]; }
            Array.Sort(names, (a, b) => PortNumber(a).CompareTo(PortNumber(b)));
            foreach (var n in names)
            {
                string d;
                bool isBt = bt.TryGetValue(n, out d);
                items.Add(new PortItem { Name = n, Text = isBt ? n + "   " + d : n });
            }
            //Bluetooth ports first
            items.Sort((a, b) =>
            {
                bool ab = bt.ContainsKey(a.Name), bb = bt.ContainsKey(b.Name);
                if (ab != bb) return ab ? -1 : 1;
                return PortNumber(a.Name).CompareTo(PortNumber(b.Name));
            });
            ports.BeginUpdate();
            ports.Items.Clear();
            int select = -1;
            for (int i = 0; i < items.Count; i++)
            {
                ports.Items.Add(items[i]);
                if (string.Equals(items[i].Name, wanted, StringComparison.OrdinalIgnoreCase)) select = i;
            }
            ports.EndUpdate();
            if (select >= 0) ports.SelectedIndex = select;
            else if (items.Count > 0 && bt.Count > 0) ports.SelectedIndex = 0;
            if (items.Count == 0) message.Text = "No COM ports found. Pair the bridge in the Windows Bluetooth settings first.";
        }

        private static int PortNumber(string name)
        {
            int n;
            return name != null && name.Length > 3 && int.TryParse(name.Substring(3), out n) ? n : int.MaxValue;
        }

        /// <summary>COM ports that belong to a paired Bluetooth device: port name to the name Windows gives it.</summary>
        private static Dictionary<string, string> BluetoothPorts()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\BTHENUM"))
                {
                    if (root == null) return d;
                    foreach (var dev in root.GetSubKeyNames())
                    {
                        using (var k1 = root.OpenSubKey(dev))
                        {
                            if (k1 == null) continue;
                            foreach (var inst in k1.GetSubKeyNames())
                            {
                                using (var k2 = k1.OpenSubKey(inst))
                                {
                                    if (k2 == null) continue;
                                    string port = null;
                                    using (var dp = k2.OpenSubKey("Device Parameters"))
                                    {
                                        if (dp != null) port = dp.GetValue("PortName") as string;
                                    }
                                    if (string.IsNullOrEmpty(port)) continue;
                                    string friendly = k2.GetValue("FriendlyName") as string;
                                    d[port] = string.IsNullOrEmpty(friendly) ? "Bluetooth serial port" : friendly;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                SSComm.Log.Warn("Bluetooth ports: cannot read the registry: " + e.Message);
            }
            return d;
        }
    }
}
