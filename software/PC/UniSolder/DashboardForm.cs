using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace UniSolder
{
    /// <summary>
    /// Full-screen bench view, like the dashboard of the Android app: big actual temperature, set temperature and heater power.
    /// Click, Escape or a double-click closes it.
    /// </summary>
    public class DashboardForm : Form
    {
        private static readonly Color Yellow = Color.FromArgb(255, 193, 7);
        private static readonly Color Green = Color.FromArgb(105, 240, 174);

        private readonly Label actual = new Label();
        private readonly Label set = new Label();
        private readonly Label status = new Label();
        private readonly Panel powerTrack = new Panel();
        private readonly Panel powerFill = new Panel();
        private readonly Label powerText = new Label();
        private float power;

        public DashboardForm()
        {
            Text = "UniSolder dashboard";
            BackColor = Color.Black;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            KeyPreview = true;
            ShowInTaskbar = true;

            var col = new TableLayoutPanel();
            col.Dock = DockStyle.Fill;
            col.ColumnCount = 1;
            col.RowCount = 6;
            col.BackColor = Color.Black;
            col.Padding = new Padding(40);
            col.RowStyles.Add(new RowStyle(SizeType.Percent, 15));
            col.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            col.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            col.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            col.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            col.RowStyles.Add(new RowStyle(SizeType.Percent, 15));

            Setup(actual, 160f, Yellow, "---", FontFamily.GenericMonospace);
            Setup(set, 36f, Color.White, "", null);
            Setup(status, 22f, Color.LightGray, "", null);

            powerTrack.Height = 28;
            powerTrack.Dock = DockStyle.Fill;
            powerTrack.BackColor = Color.FromArgb(60, 60, 64);
            powerTrack.Margin = new Padding(40, 30, 40, 0);
            powerFill.BackColor = Yellow;
            powerFill.Dock = DockStyle.Left;
            powerFill.Width = 0;
            powerTrack.Controls.Add(powerFill);
            powerTrack.Resize += (s, e) => ShowPower();
            Setup(powerText, 26f, Color.White, "", null);

            col.Controls.Add(new Panel { BackColor = Color.Black }, 0, 0);
            col.Controls.Add(actual, 0, 1);
            col.Controls.Add(set, 0, 2);
            col.Controls.Add(status, 0, 3);
            var powerBox = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, AutoSize = true, BackColor = Color.Black };
            powerBox.Controls.Add(powerTrack, 0, 0);
            powerBox.Controls.Add(powerText, 0, 1);
            col.Controls.Add(powerBox, 0, 4);
            Controls.Add(col);

            foreach (Control c in new Control[] { this, col, actual, set, status, powerTrack, powerFill, powerText, powerBox }) c.Click += (s, e) => Close();
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            ShowNoData();
        }

        private static void Setup(Label l, float size, Color color, string text, FontFamily family)
        {
            l.AutoSize = false;
            l.Dock = DockStyle.Fill;
            l.TextAlign = ContentAlignment.MiddleCenter;
            l.Font = new Font(family ?? SystemFonts.DefaultFont.FontFamily, size, FontStyle.Bold);
            l.ForeColor = color;
            l.BackColor = Color.Black;
            l.Text = text;
            l.Height = (int)(size * 2.0f);
        }

        private void ShowPower()
        {
            powerFill.Width = (int)(powerTrack.ClientSize.Width * Math.Max(0f, Math.Min(100f, power)) / 100f);
        }

        public void UpdateSample(LiveSample s)
        {
            actual.Text = s.Temp.ToString("0", CultureInfo.InvariantCulture) + "°";
            actual.ForeColor = s.Reached ? Green : Yellow;
            set.Text = "set " + s.Set.ToString("0", CultureInfo.InvariantCulture) + " °C";
            status.Text = s.Reached ? "at temperature" : "heating";
            power = s.PowerPct;
            ShowPower();
            powerText.Text = "heater " + power.ToString("0", CultureInfo.InvariantCulture) + " %";
        }

        public void ShowNoData()
        {
            actual.Text = "---";
            actual.ForeColor = Yellow;
            set.Text = "";
            status.Text = "not connected";
            power = 0;
            ShowPower();
            powerText.Text = "";
        }
    }
}
