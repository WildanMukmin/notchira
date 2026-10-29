using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace Notchira
{
    // Time-synced lyrics. Lines come from LRCLIB (https://lrclib.net), YouTube captions, or a transcription;
    // all of them end up as LRC text, which this class parses.
    sealed class Lyrics
    {
        static readonly Regex Stamp = new Regex(@"\[(\d+):(\d+(?:[.:]\d+)?)\]", RegexOptions.Compiled);

        public TimeSpan[] Times;
        public string[] Lines;

        public int IndexAt(TimeSpan pos)
        {
            int idx = -1;
            for (int i = 0; i < Times.Length && Times[i] <= pos; i++) idx = i;
            return idx;
        }

        public string LineAt(TimeSpan pos)
        {
            int i = IndexAt(pos);
            return i < 0 ? "" : Lines[i];
        }

        public string Line(int i) { return i < 0 || i >= Lines.Length ? null : Lines[i]; }

        public bool NextTime(TimeSpan pos, out TimeSpan next)
        {
            for (int i = 0; i < Times.Length; i++)
                if (Times[i] > pos) { next = Times[i]; return true; }
            next = TimeSpan.Zero;
            return false;
        }

        public static Lyrics Parse(string lrc)
        {
            var items = new List<KeyValuePair<TimeSpan, string>>();
            foreach (string raw in lrc.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                var ms = Stamp.Matches(line);
                if (ms.Count == 0) continue;
                string text = line.Substring(ms[ms.Count - 1].Index + ms[ms.Count - 1].Length).Trim();
                foreach (Match m in ms)
                {
                    double min = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    double sec = double.Parse(m.Groups[2].Value.Replace(':', '.'), CultureInfo.InvariantCulture);
                    items.Add(new KeyValuePair<TimeSpan, string>(TimeSpan.FromSeconds(min * 60 + sec), text));
                }
            }
            if (items.Count == 0) return null;
            items.Sort((a, b) => a.Key.CompareTo(b.Key));
            var l = new Lyrics { Times = new TimeSpan[items.Count], Lines = new string[items.Count] };
            for (int i = 0; i < items.Count; i++) { l.Times[i] = items[i].Key; l.Lines[i] = items[i].Value; }
            return l;
        }

        // seconds + text pairs to LRC text
        public static string ToLrc(List<KeyValuePair<double, string>> items)
        {
            var sb = new StringBuilder();
            foreach (var it in items)
            {
                string t = (it.Value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
                if (t.Length == 0) continue;
                int cs = (int)Math.Round(Math.Max(0, it.Key) * 100);
                sb.AppendFormat(CultureInfo.InvariantCulture, "[{0:00}:{1:00}.{2:00}]{3}\n", cs / 6000, cs / 100 % 60, cs % 100, t);
            }
            return sb.ToString();
        }

        static string CachePath(string title, string artist, string album, int secs)
        {
            using (var sha = SHA1.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes((title + "|" + artist + "|" + album + "|" + secs).ToLowerInvariant()));
                var sb = new StringBuilder();
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return Path.Combine(Settings.Dir, "lyrics", sb + ".lrc");
            }
        }

        // Calls done on a pool thread with the lyrics, or null when none exist or the lookup failed.
        public static void Fetch(string title, string artist, string album, TimeSpan duration, bool online, Action<Lyrics> done)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                Lyrics result = null;
                try
                {
                    int secs = (int)Math.Round(duration.TotalSeconds);
                    string cache = CachePath(title, artist, album, secs);
                    if (File.Exists(cache))
                    {
                        string txt = File.ReadAllText(cache, Encoding.UTF8);
                        result = txt.Length == 0 ? null : Parse(txt);
                    }
                    else if (online)
                    {
                        string lrc = Lookup(artist, title, album, secs);
                        if (lrc != null)
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(cache));
                            File.WriteAllText(cache, lrc, Encoding.UTF8);
                            result = lrc.Length == 0 ? null : Parse(lrc);
                        }
                    }
                }
                catch { }
                done(result);
            });
        }

        // Exact match first, then a search. Returns "" when LRCLIB has no synced lyrics, null on network failure.
        public static string Lookup(string artist, string title, string album, int secs)
        {
            string exact = artist.Length == 0 ? "" : Get("https://lrclib.net/api/get?track_name=" + Uri.EscapeDataString(title) +
                "&artist_name=" + Uri.EscapeDataString(artist) +
                (album != null && album.Length > 0 ? "&album_name=" + Uri.EscapeDataString(album) : "") +
                (secs > 0 ? "&duration=" + secs : ""));
            if (exact == null) return null;
            var obj = new JavaScriptSerializer().DeserializeObject(exact.Length == 0 ? "{}" : exact) as Dictionary<string, object>;
            object synced;
            if (obj != null && obj.TryGetValue("syncedLyrics", out synced) && synced is string && Usable((string)synced))
                return (string)synced;

            string found = Get("https://lrclib.net/api/search?q=" + Uri.EscapeDataString((artist + " " + title).Trim()));
            if (found == null) return null;
            var arr = new JavaScriptSerializer().DeserializeObject(found.Length == 0 ? "[]" : found) as object[];
            if (arr == null) return "";
            foreach (object o in arr)
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                object s, dur;
                if (!d.TryGetValue("syncedLyrics", out s) || !(s is string) || !Usable((string)s)) continue;
                if (secs > 0 && d.TryGetValue("duration", out dur) && dur != null)
                {
                    double dv = Convert.ToDouble(dur, CultureInfo.InvariantCulture);
                    if (Math.Abs(dv - secs) > 6) continue;
                }
                return (string)s;
            }
            return "";
        }

        // community entries are sometimes junk, so a result needs at least three timed lines
        static bool Usable(string lrc) { return lrc.Length > 0 && Stamp.Matches(lrc).Count >= 3; }

        // Body text, "" on 404, null on other failures.
        static string Get(string url)
        {
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = "Notchira/0.6 (https://github.com/notchira)";
                req.Timeout = 8000;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return rd.ReadToEnd();
            }
            catch (WebException ex)
            {
                var r = ex.Response as HttpWebResponse;
                if (r != null && r.StatusCode == HttpStatusCode.NotFound) return "";
                Log(ex);
                return null;
            }
            catch (Exception ex) { Log(ex); return null; }
        }

        public static void Log(Exception ex)
        {
            if (Environment.GetEnvironmentVariable("NOTCHIRA_DEBUG") == null) return;
            try { File.AppendAllText(Path.Combine(Settings.Dir, "debug.log"), ex + Environment.NewLine); } catch { }
        }
    }
}
