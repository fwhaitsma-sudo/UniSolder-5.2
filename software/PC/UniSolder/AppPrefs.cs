using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace UniSolder
{
    /// <summary>
    /// Small settings store of the Windows app (key=value lines in %APPDATA%\UniSolder\settings.txt),
    /// like the Prefs of the Android app.
    /// </summary>
    public static class AppPrefs
    {
        private static Dictionary<string, string> values;

        public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UniSolder");
        private static string FilePath => Path.Combine(Dir, "settings.txt");

        private static void Load()
        {
            if (values != null) return;
            values = new Dictionary<string, string>();
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) values[line.Substring(0, i)] = line.Substring(i + 1);
                }
            }
            catch (Exception e)
            {
                SSComm.Log.Warn("Settings file not readable: " + e.Message);
            }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var lines = new List<string>();
                foreach (var kv in values) lines.Add(kv.Key + "=" + kv.Value);
                File.WriteAllLines(FilePath, lines);
            }
            catch (Exception e)
            {
                SSComm.Log.Warn("Settings file not writable: " + e.Message);
            }
        }

        public static bool GetBool(string key, bool def)
        {
            Load();
            return values.TryGetValue(key, out var v) ? v == "1" : def;
        }

        public static void PutBool(string key, bool v)
        {
            Load();
            values[key] = v ? "1" : "0";
            Save();
        }

        public static string GetString(string key, string def)
        {
            Load();
            return values.TryGetValue(key, out var v) ? v : def;
        }

        public static void PutString(string key, string v)
        {
            Load();
            values[key] = (v ?? "").Replace("\r", "").Replace("\n", "");
            Save();
        }

        public static float GetFloat(string key, float def)
        {
            Load();
            return values.TryGetValue(key, out var v) && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : def;
        }

        public static void PutFloat(string key, float v)
        {
            Load();
            values[key] = v.ToString("R", CultureInfo.InvariantCulture);
            Save();
        }
    }
}
