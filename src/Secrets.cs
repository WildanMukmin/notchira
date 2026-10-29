using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Notchira
{
    // API keys, kept per provider (several keys each, so a rate-limited one can be skipped).
    // They are encrypted with the Windows account (DPAPI) and never written anywhere else.
    static class Secrets
    {
        static string FilePath { get { return Path.Combine(Settings.Dir, "keys.dat"); } }

        static Dictionary<string, List<string>> Load()
        {
            var map = new Dictionary<string, List<string>>();
            try
            {
                if (!File.Exists(FilePath)) return map;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int i = line.IndexOf('\t');
                    if (i <= 0) continue;
                    byte[] plain = ProtectedData.Unprotect(Convert.FromBase64String(line.Substring(i + 1)), null, DataProtectionScope.CurrentUser);
                    map[line.Substring(0, i)] = Encoding.UTF8.GetString(plain)
                        .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                }
            }
            catch { }
            return map;
        }

        public static List<string> Get(string provider)
        {
            List<string> v;
            return Load().TryGetValue(provider, out v) ? v : new List<string>();
        }

        public static bool Has(string provider) { return Get(provider).Count > 0; }

        public static void Set(string provider, IEnumerable<string> keys)
        {
            var map = Load();
            var clean = keys.Select(k => k.Trim()).Where(k => k.Length > 0 && k.IndexOfAny(new[] { '\r', '\n', '\t', ' ' }) < 0).ToList();
            if (clean.Count == 0) map.Remove(provider); else map[provider] = clean;
            try
            {
                Directory.CreateDirectory(Settings.Dir);
                var lines = map.Select(kv => kv.Key + "\t" + Convert.ToBase64String(
                    ProtectedData.Protect(Encoding.UTF8.GetBytes(string.Join("\n", kv.Value.ToArray())), null, DataProtectionScope.CurrentUser)));
                string tmp = FilePath + ".tmp";
                File.WriteAllLines(tmp, lines.ToArray());
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch { }
        }
    }
}
