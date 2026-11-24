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
            Theme.ApplyIcon(this);

            var col = new TableLayoutPanel();
            col.Dock = DockStyle.Fill;
            col.ColumnCount = 1;
            col.RowCount = 5;
            col.BackColor = Color.Black;
            col.Padding = new Padding(40);
            col.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            col.RowStyles.Add(new RowStyle(SizeType.Percent, 46));
            col.RowStyles.Add(new RowStyle(SizeType.Percent, 14));
            col.RowStyles.Add(new RowStyle(SizeType.Percent, 10));
            col.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            col.RowStyles.Add(new RowStyle(SizeType.Percent, 12));

            Setup(actual, Yellow, "---", "Segoe UI");
            Setup(set, Color.White, "", "Segoe UI");
            Setup(status, Color.LightGray, "", "Segoe UI");
            Setup(powerText, Color.White, "", "Segoe UI");

            powerTrack.Dock = DockStyle.Fill;
            powerTrack.BackColor = Color.FromArgb(60, 60, 64);
            powerTrack.Margin = new Padding(60, 8, 60, 8);
            powerFill.BackColor = Yellow;
            powerFill.Dock = DockStyle.Left;
            powerFill.Width = 0;
            powerTrack.Controls.Add(powerFill);
            powerTrack.Resize += (s, e) => ShowPower();

            col.Controls.Add(actual, 0, 0);
            col.Controls.Add(set, 0, 1);
            col.Controls.Add(status, 0, 2);
            col.Controls.Add(powerTrack, 0, 3);
            col.Controls.Add(powerText, 0, 4);
            Controls.Add(col);

            //the text sizes follow the height of the screen, so the rows never overlap (points = pixels * 72 / dpi)
            col.Resize += (s, e) => FitFonts(col);

            foreach (Control c in new Control[] { this, col, actual, set, status, powerTrack, powerFill, powerText }) c.Click += (s, e) => Close();
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            ShowNoData();
        }

        private static void Setup(Label l, Color color, string text, string family)
        {
            l.AutoSize = false;
            l.Dock = DockStyle.Fill;
            l.Margin = new Padding(0);
            l.TextAlign = ContentAlignment.MiddleCenter;
            l.Font = new Font(family, 20f, FontStyle.Bold);
            l.ForeColor = color;
            l.BackColor = Color.Black;
            l.Text = text;
        }

        private void FitFonts(TableLayoutPanel col)
        {
            float h = Math.Max(300, col.ClientSize.Height - col.Padding.Vertical);
            float toPoints = 72f / Math.Max(1, DeviceDpi);
            SetSize(actual, h * 0.46f * 0.78f * toPoints, "Consolas");
            SetSize(set, h * 0.14f * 0.62f * toPoints, "Segoe UI");
            SetSize(status, h * 0.10f * 0.55f * toPoints, "Segoe UI");
            SetSize(powerText, h * 0.12f * 0.55f * toPoints, "Segoe UI");
        }

        private static void SetSize(Label l, float points, string family)
        {
            var old = l.Font;
            l.Font = new Font(family, Math.Max(8f, points), FontStyle.Bold);
            old.Dispose();
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

        /// <summary>No tip in the iron: the readings are not valid, show only the set temperature.</summary>
        public void ShowNoTip(LiveSample s)
        {
            actual.Text = "---";
            actual.ForeColor = Yellow;
            set.Text = "set " + s.Set.ToString("0", CultureInfo.InvariantCulture) + " °C";
            status.Text = "no tip";
            power = 0;
            ShowPower();
            powerText.Text = "";
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
