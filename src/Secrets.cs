using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Notchira
{
    sealed class KeyException : Exception
    {
        public KeyException(string message) : base(message) { }
    }

    // An HTTP error from an API, kept with its status and body so key rotation can tell a rate limit
    // from a spent quota or a rejected key.
    sealed class ApiException : Exception
    {
        public int Code;
        public string Body;
        public ApiException(int code, string body) : base("HTTP " + code) { Code = code; Body = body ?? ""; }
    }

    sealed class KeyInfo
    {
        public string Id, Label, Last4;
        public bool Active, Limited;
        public DateTime LimitedUntil;
    }

    // API keys, several per provider, in the same spirit as a key ring: one is active, and when it is rate limited,
    // out of quota or rejected, the next usable one takes over and the bad one rests for a while.
    // Values are encrypted with the Windows account (DPAPI); only the label and the last four characters are shown.
    //
    // keys.dat, tab separated:
    //   P  provider  activeId  auto(0/1)
    //   K  provider  id  base64(label)  limitedUntilTicks  base64(DPAPI(value))
    static class Secrets
    {
        public const int RateMs = 2 * 60 * 1000, QuotaMs = 60 * 60 * 1000, RejectedMs = 60 * 60 * 1000;

        sealed class Entry { public string Id, Label, Value; public long LimitedUntil; }
        sealed class Ring { public List<Entry> Keys = new List<Entry>(); public string ActiveId; public bool Auto = true; }

        static readonly object gate = new object();
        static string FilePath { get { return Path.Combine(Settings.Dir, "keys.dat"); } }

        // provider, label of the key left behind, label of the key now used (null when none is ready)
        public static event Action<string, string, string> Switched;

        // ---- storage ----------------------------------------------------------------------------------------

        static Dictionary<string, Ring> Load()
        {
            var map = new Dictionary<string, Ring>();
            try
            {
                if (!File.Exists(FilePath)) return map;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    string[] p = line.Split('\t');
                    if (p.Length >= 4 && p[0] == "P")
                    {
                        Ring r = RingOf(map, p[1]);
                        r.ActiveId = p[2].Length > 0 ? p[2] : null;
                        r.Auto = p[3] != "0";
                    }
                    else if (p.Length >= 6 && p[0] == "K")
                    {
                        long lim; long.TryParse(p[4], out lim);
                        RingOf(map, p[1]).Keys.Add(new Entry
                        {
                            Id = p[2],
                            Label = Encoding.UTF8.GetString(Convert.FromBase64String(p[3])),
                            LimitedUntil = lim,
                            Value = Unprotect(p[5])
                        });
                    }
                    else if (p.Length == 2 && p[0] != "P" && p[0] != "K")
                    {
                        // older format: provider TAB DPAPI(key1 LF key2 ...)
                        string[] vals = Unprotect(p[1]).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        Ring r = RingOf(map, p[0]);
                        foreach (string v in vals)
                            r.Keys.Add(new Entry { Id = NewId(), Label = "Key " + (r.Keys.Count + 1), Value = v });
                    }
                }
                foreach (Ring r in map.Values)
                    if (r.ActiveId == null || r.Keys.All(k => k.Id != r.ActiveId)) r.ActiveId = r.Keys.Count > 0 ? r.Keys[0].Id : null;
            }
            catch { }
            return map;
        }

        static Ring RingOf(Dictionary<string, Ring> map, string provider)
        {
            Ring r;
            if (!map.TryGetValue(provider, out r)) { r = new Ring(); map[provider] = r; }
            return r;
        }

        static string Unprotect(string b64)
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(b64), null, DataProtectionScope.CurrentUser));
        }

        static string NewId()
        {
            var b = new byte[4];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
        }

        static void Save(Dictionary<string, Ring> map)
        {
            try
            {
                Directory.CreateDirectory(Settings.Dir);
                var lines = new List<string>();
                foreach (var kv in map)
                {
                    if (kv.Value.Keys.Count == 0) continue;
                    lines.Add("P\t" + kv.Key + "\t" + (kv.Value.ActiveId ?? "") + "\t" + (kv.Value.Auto ? "1" : "0"));
                    foreach (Entry e in kv.Value.Keys)
                        lines.Add("K\t" + kv.Key + "\t" + e.Id + "\t" + Convert.ToBase64String(Encoding.UTF8.GetBytes(e.Label)) + "\t" +
                            e.LimitedUntil + "\t" + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(e.Value), null, DataProtectionScope.CurrentUser)));
                }
                string tmp = FilePath + ".tmp";
                File.WriteAllLines(tmp, lines.ToArray());
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch { }
        }

        // ---- management -------------------------------------------------------------------------------------

        public static bool Has(string provider)
        {
            lock (gate) { Ring r; return Load().TryGetValue(provider, out r) && r.Keys.Count > 0; }
        }

        public static List<KeyInfo> List(string provider)
        {
            lock (gate)
            {
                Ring r;
                var result = new List<KeyInfo>();
                if (!Load().TryGetValue(provider, out r)) return result;
                long now = DateTime.UtcNow.Ticks;
                foreach (Entry e in r.Keys)
                    result.Add(new KeyInfo
                    {
                        Id = e.Id, Label = e.Label,
                        Last4 = e.Value.Length >= 4 ? e.Value.Substring(e.Value.Length - 4) : "",
                        Active = e.Id == r.ActiveId,
                        Limited = e.LimitedUntil > now,
                        LimitedUntil = new DateTime(Math.Max(e.LimitedUntil, DateTime.MinValue.Ticks), DateTimeKind.Utc).ToLocalTime()
                    });
                return result;
            }
        }

        public static bool GetAuto(string provider)
        {
            lock (gate) { Ring r; return !Load().TryGetValue(provider, out r) || r.Auto; }
        }

        public static void SetAuto(string provider, bool auto)
        {
            Mutate(provider, r => r.Auto = auto);
        }

        public static void Add(string provider, string label, string value)
        {
            value = (value ?? "").Trim();
            if (value.Length == 0 || value.IndexOfAny(new[] { '\r', '\n', '\t', ' ' }) >= 0) throw new ArgumentException("Kunci tidak valid.");
            Mutate(provider, r =>
            {
                if (r.Keys.Count >= 100) throw new ArgumentException("Maksimal 100 kunci per penyedia.");
                string l = string.IsNullOrWhiteSpace(label) ? "Key " + (r.Keys.Count + 1) : label.Trim();
                if (l.Length > 40) l = l.Substring(0, 40);
                var e = new Entry { Id = NewId(), Label = l, Value = value };
                r.Keys.Add(e);
                if (r.ActiveId == null) r.ActiveId = e.Id;
            });
        }

        public static void Remove(string provider, string id)
        {
            Mutate(provider, r =>
            {
                r.Keys.RemoveAll(k => k.Id == id);
                if (r.ActiveId == id) r.ActiveId = r.Keys.Count > 0 ? r.Keys[0].Id : null;
            });
        }

        public static void Select(string provider, string id) { Mutate(provider, r => { if (r.Keys.Any(k => k.Id == id)) r.ActiveId = id; }); }

        public static void ResetLimit(string provider, string id) { Mutate(provider, r => { foreach (Entry e in r.Keys) if (e.Id == id) e.LimitedUntil = 0; }); }

        static void Mutate(string provider, Action<Ring> change)
        {
            lock (gate)
            {
                var map = Load();
                change(RingOf(map, provider));
                Save(map);
            }
        }

        // ---- calling with rotation --------------------------------------------------------------------------

        static void Classify(Exception ex, out int code, out string body)
        {
            code = 0; body = "";
            var api = ex as ApiException;
            if (api != null) { code = api.Code; body = api.Body; return; }
            var web = ex as WebException;
            if (web == null) return;
            var resp = web.Response as HttpWebResponse;
            if (resp == null) return;
            code = (int)resp.StatusCode;
            try { using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) body = rd.ReadToEnd(); } catch { }
        }

        // Runs fn with the active key. A rejected key (401/403), a rate limit or a spent quota (429) rests that key and
        // moves on to the next usable one, if automatic switching is on. Any other failure goes straight to the caller.
        // allowNoKey lets a provider without keys (a local model server) be called with an empty key.
        public static T Call<T>(string provider, Func<string, T> fn, bool allowNoKey, out string label)
        {
            label = "";
            var tried = new HashSet<string>();
            string lastReason = null;
            for (;;)
            {
                Entry e = null;
                bool noKey = false;
                lock (gate)
                {
                    Ring r;
                    var map = Load();
                    if (!map.TryGetValue(provider, out r) || r.Keys.Count == 0)
                    {
                        if (!allowNoKey) throw new KeyException("Belum ada kunci API untuk " + provider + ".");
                        noKey = true;
                    }
                    else
                    {
                        e = Pick(r, tried);
                        if (e == null) throw new KeyException("Semua kunci " + provider + " sedang kena batas" + (lastReason != null ? " (" + lastReason + ")" : "") + ".");
                    }
                }
                if (noKey) return fn("");
                try
                {
                    T result = fn(e.Value);
                    lock (gate)
                    {
                        var map = Load();
                        Ring r = RingOf(map, provider);
                        if (r.ActiveId != e.Id && r.Keys.Any(k => k.Id == e.Id) && tried.Count > 0) { r.ActiveId = e.Id; Save(map); }
                    }
                    label = e.Label;
                    return result;
                }
                catch (Exception ex)
                {
                    int code; string body;
                    Classify(ex, out code, out body);
                    long cooldown;
                    string why;
                    if (code == 401 || code == 403) { cooldown = RejectedMs; why = "ditolak"; }
                    else if (code == 429)
                    {
                        bool quota = body.IndexOf("quota", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                     body.IndexOf("per minute", StringComparison.OrdinalIgnoreCase) < 0 &&
                                     body.IndexOf("rate", StringComparison.OrdinalIgnoreCase) < 0;
                        cooldown = quota ? QuotaMs : RateMs;
                        why = quota ? "kuota habis" : "batas per menit";
                    }
                    else throw;

                    string next = null;
                    bool auto;
                    lock (gate)
                    {
                        var map = Load();
                        Ring r = RingOf(map, provider);
                        foreach (Entry k in r.Keys) if (k.Id == e.Id) k.LimitedUntil = DateTime.UtcNow.AddMilliseconds(cooldown).Ticks;
                        auto = r.Auto;
                        tried.Add(e.Id);
                        Entry n = auto ? Pick(r, tried) : null;
                        if (n != null) { r.ActiveId = n.Id; next = n.Label; }
                        Save(map);
                    }
                    lastReason = why;
                    var h = Switched;
                    if (h != null) h(provider, e.Label, next);
                    if (!auto) throw new KeyException("Kunci " + provider + " \"" + e.Label + "\" " + why + ". Penggantian otomatis mati.");
                }
            }
        }

        // The active key first, then the rest in order. Keys that are resting are skipped, unless every key rests:
        // then the active one is tried once anyway, since its pause may have ended.
        static Entry Pick(Ring r, HashSet<string> tried)
        {
            int start = Math.Max(0, r.Keys.FindIndex(k => k.Id == r.ActiveId));
            long now = DateTime.UtcNow.Ticks;
            var order = new List<Entry>();
            for (int i = 0; i < r.Keys.Count; i++) order.Add(r.Keys[(start + i) % r.Keys.Count]);
            if (!r.Auto) order = order.Take(1).ToList();
            Entry usable = order.FirstOrDefault(k => !tried.Contains(k.Id) && k.LimitedUntil <= now);
            if (usable != null) return usable;
            if (tried.Count == 0) return order.FirstOrDefault();
            return null;
        }
    }
}
