using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace UniSolder
{
    /// <summary>
    /// Records the live data to a CSV file, five rows per second, in the same format as the Android app
    /// (Documents\UniSolder\sessions\session-yyyyMMdd-HHmmss.csv).
    /// </summary>
    public class Recorder
    {
        public static string SessionsDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "UniSolder", "sessions");

        private StreamWriter writer;
        private DateTime start;
        private DateTime last;

        public string FilePath { get; private set; }
        public int Rows { get; private set; }
        public bool Recording => writer != null;
        public long ElapsedSeconds => Recording ? (long)(DateTime.Now - start).TotalSeconds : 0;

        public bool Start()
        {
            try
            {
                Directory.CreateDirectory(SessionsDir);
                FilePath = Path.Combine(SessionsDir, "session-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".csv");
                writer = new StreamWriter(FilePath, false);
                writer.NewLine = "\n";
                writer.WriteLine("time_s,set_C,actual_C,power_pct,heater_on,reached,resistance_ohm,filtered_C,adc_C");
                Rows = 0;
                start = DateTime.Now;
                last = DateTime.MinValue;
                SSComm.Log.Info("Recording to " + FilePath);
                return true;
            }
            catch (Exception e)
            {
                SSComm.Log.Error("Cannot start recording: " + e.Message);
                writer = null;
                return false;
            }
        }

        public void Add(LiveSample s)
        {
            if (writer == null) return;
            var now = DateTime.Now;
            if ((now - last).TotalMilliseconds < 200) return;
            last = now;
            try
            {
                writer.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0:0.0},{1:0},{2:0.0},{3:0.0},{4},{5},{6:0.00},{7:0.0},{8:0.0}",
                    (now - start).TotalSeconds, s.Set, s.Temp, s.PowerPct, s.HeaterOn ? 1 : 0, s.Reached ? 1 : 0, s.Resistance, s.Avg, s.Adc));
                Rows++;
                if (Rows % 25 == 0) writer.Flush();
            }
            catch (Exception e)
            {
                SSComm.Log.Error("Recording failed: " + e.Message);
                Stop();
            }
        }

        public void Stop()
        {
            try { writer?.Dispose(); } catch (Exception) { }
            if (writer != null) SSComm.Log.Info("Recording stopped, " + Rows + " rows");
            writer = null;
        }

        public static List<string> Sessions()
        {
            try
            {
                if (!Directory.Exists(SessionsDir)) return new List<string>();
                return Directory.GetFiles(SessionsDir, "*.csv").OrderByDescending(f => Path.GetFileName(f)).ToList();
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }
    }

    /// <summary>Numbers for one recorded session (same as the Android app).</summary>
    public class SessionStats
    {
        public int Rows;
        public double DurationS;
        public double MaxTemp;
        public double AvgPower;
        public double? WarmupS;

        public string DurationText => string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", (int)(DurationS / 60), (int)(DurationS % 60));

        public static SessionStats Read(string path)
        {
            try
            {
                using (var r = new StreamReader(path))
                {
                    if (r.ReadLine() == null) return null;
                    int rows = 0;
                    double lastT = 0, max = 0, power = 0;
                    double? warm = null;
                    bool sawNotReached = false;
                    string line;
                    while ((line = r.ReadLine()) != null)
                    {
                        var c = line.Split(',');
                        if (c.Length < 9) continue;
                        if (!double.TryParse(c[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var t)) continue;
                        rows++;
                        lastT = t;
                        if (double.TryParse(c[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var temp)) max = Math.Max(max, temp);
                        if (double.TryParse(c[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) power += p;
                        bool reached = c[5] == "1";
                        if (!reached) sawNotReached = true;
                        if (reached && sawNotReached && warm == null) warm = t;
                    }
                    if (rows == 0) return null;
                    return new SessionStats { Rows = rows, DurationS = lastT, MaxTemp = max, AvgPower = power / rows, WarmupS = warm };
                }
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
