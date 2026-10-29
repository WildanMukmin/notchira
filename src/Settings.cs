using System;
using System.Collections.Generic;
using System.IO;

namespace Notchira
{
    // Flat key=value file in %APPDATA%\Notchira. Kept out of git (see .gitignore).
    static class Settings
    {
        public static readonly string Dir =
            Environment.GetEnvironmentVariable("NOTCHIRA_DIR") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Notchira");
        static readonly string file = Path.Combine(Dir, "settings.ini");
        static readonly Dictionary<string, string> map = new Dictionary<string, string>();

        public static void Load()
        {
            map.Clear();
            try
            {
                if (!File.Exists(file)) return;
                foreach (string line in File.ReadAllLines(file))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) map[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
            }
            catch { }
        }

        public static string Get(string key, string def)
        {
            string v;
            return map.TryGetValue(key, out v) ? v : def;
        }

        public static void Set(string key, string value)
        {
            map[key] = value;
            try
            {
                Directory.CreateDirectory(Dir);
                var lines = new List<string>();
                foreach (var kv in map) lines.Add(kv.Key + "=" + kv.Value);
                string tmp = file + ".tmp";
                File.WriteAllLines(tmp, lines.ToArray());
                if (File.Exists(file)) File.Delete(file);
                File.Move(tmp, file);
            }
            catch { }
        }
    }
}
