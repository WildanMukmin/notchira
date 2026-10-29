using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace Notchira
{
    // Time-synced lyrics from LRCLIB (https://lrclib.net). Results, including "not found", are cached on disk.
    sealed class Lyrics
    {
        static readonly Regex Stamp = new Regex(@"\[(\d+):(\d+(?:[.:]\d+)?)\]", RegexOptions.Compiled);

        public TimeSpan[] Times;
        public string[] Lines;

        public string LineAt(TimeSpan pos)
        {
            int idx = -1;
            for (int i = 0; i < Times.Length && Times[i] <= pos; i++) idx = i;
            return idx < 0 ? "" : Lines[idx];
        }

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
                    double min = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    double sec = double.Parse(m.Groups[2].Value.Replace(':', '.'), System.Globalization.CultureInfo.InvariantCulture);
                    items.Add(new KeyValuePair<TimeSpan, string>(TimeSpan.FromSeconds(min * 60 + sec), text));
                }
            }
            if (items.Count == 0) return null;
            items.Sort((a, b) => a.Key.CompareTo(b.Key));
            var l = new Lyrics { Times = new TimeSpan[items.Count], Lines = new string[items.Count] };
            for (int i = 0; i < items.Count; i++) { l.Times[i] = items[i].Key; l.Lines[i] = items[i].Value; }
            return l;
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
                        string lrc = Download(title, artist, album, secs);
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

        // Returns "" when LRCLIB has no synced lyrics, null on network failure (not cached).
        static string Download(string title, string artist, string album, int secs)
        {
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
                var url = "https://lrclib.net/api/get?track_name=" + Uri.EscapeDataString(title) +
                          "&artist_name=" + Uri.EscapeDataString(artist) +
                          (album.Length > 0 ? "&album_name=" + Uri.EscapeDataString(album) : "") +
                          (secs > 0 ? "&duration=" + secs : "");
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = "Notchira/0.2 (https://github.com/notchira)";
                req.Timeout = 6000;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    var obj = new JavaScriptSerializer().DeserializeObject(rd.ReadToEnd()) as Dictionary<string, object>;
                    object synced;
                    if (obj != null && obj.TryGetValue("syncedLyrics", out synced) && synced is string) return (string)synced;
                    return "";
                }
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

        static void Log(Exception ex)
        {
            if (Environment.GetEnvironmentVariable("NOTCHIRA_DEBUG") == null) return;
            try { File.AppendAllText(Path.Combine(Settings.Dir, "debug.log"), ex + Environment.NewLine); } catch { }
        }
    }
}
