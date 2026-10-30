using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Notchira
{
    // YouTube titles look like "Artist - Song (Official Video) [4K]"; LRCLIB wants "Artist" and "Song".
    static class TitleCleaner
    {
        static readonly Regex Noise = new Regex(
            @"[\(\[\{][^\)\]\}]*\b(official|video|lyrics?|audio|visuali[sz]er|mv|m/v|hd|hq|4k|remaster(ed)?|clip|full)\b[^\)\]\}]*[\)\]\}]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex Feat = new Regex(@"[\(\[]?\s*\b(feat|ft|featuring)\b\.?[^\)\]\-]*[\)\]]?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex Dash = new Regex(@"\s[-\u2013\u2014]\s", RegexOptions.Compiled);

        static string Tidy(string s)
        {
            s = s.Trim().Trim('"', '\u201C', '\u201D', '\'', '`');
            return Regex.Replace(s, @"\s+", " ").Trim();
        }

        public static string CleanChannel(string c)
        {
            if (string.IsNullOrEmpty(c) || c == "NA") return "";
            c = Regex.Replace(c, @"\s*-\s*Topic$", "", RegexOptions.IgnoreCase);
            c = Regex.Replace(c, @"\s*(VEVO|Official)$", "", RegexOptions.IgnoreCase);
            return Tidy(c);
        }

        // Most likely first: (artist, title) pairs to try against LRCLIB.
        public static List<KeyValuePair<string, string>> Candidates(string title, string uploader)
        {
            var list = new List<KeyValuePair<string, string>>();
            var all = Build(title, uploader);
            foreach (var kv in all)
                if (!list.Any(x => x.Key == kv.Key && x.Value == kv.Value)) list.Add(kv);
            return list;
        }

        static List<KeyValuePair<string, string>> Build(string title, string uploader)
        {
            var list = new List<KeyValuePair<string, string>>();
            string t = Noise.Replace(title ?? "", "");
            int bar = t.IndexOf('|');
            if (bar > 0) t = t.Substring(0, bar);
            t = Tidy(Feat.Replace(t, ""));
            string channel = CleanChannel(uploader);

            var m = Dash.Match(t);
            if (m.Success)
            {
                string left = Tidy(t.Substring(0, m.Index)), right = Tidy(t.Substring(m.Index + m.Length));
                if (left.Length > 0 && right.Length > 0)
                {
                    list.Add(new KeyValuePair<string, string>(left, right));
                    if (channel.Length > 0) list.Add(new KeyValuePair<string, string>(channel, right));
                    return list;
                }
            }
            if (channel.Length > 0) list.Add(new KeyValuePair<string, string>(channel, t));
            list.Add(new KeyValuePair<string, string>("", t));
            return list;
        }
    }

    // Finds timed text for a downloaded song and keeps it next to the audio as <id>.lrc.
    // Order: LRCLIB, then YouTube captions, then a Groq/Deepgram transcription (only with a stored key).
    static class SongLyrics
    {
        public static event Action<string> Updated;

        static string LrcPath(Song s) { return Path.Combine(Library.MusicDir, s.Id + ".lrc"); }

        public static void Raise(string id) { var h = Updated; if (h != null) h(id); }

        // done(lyrics or null, source name when freshly found, fresh). Runs on a pool thread.
        public static void Resolve(Song s, bool force, Action<Lyrics, string, bool> done)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try { Run(s, force, done); }
                catch (Exception ex) { Lyrics.Log(ex); done(null, null, false); }
            });
        }

        static void Run(Song s, bool force, Action<Lyrics, string, bool> done)
        {
            string path = LrcPath(s);
            bool keys = Transcribe.Available;
            bool onlyTranscribe = false;

            if (!force && File.Exists(path))
            {
                string text = File.ReadAllText(path, Encoding.UTF8);
                if (!text.StartsWith("#none"))
                {
                    var l = Lyrics.Parse(text);
                    if (l != null) { done(l, null, false); return; }
                }
                else
                {
                    // already tried everything; only a newly added key is worth another attempt
                    if (text.Contains("keys=1") || !keys) { done(null, null, false); return; }
                    onlyTranscribe = true;
                }
            }

            string lrc = null, source = null;
            bool transient = false;

            if (!onlyTranscribe && Settings.Get("lyrics.online", "1") == "1")
            {
                foreach (var c in TitleCleaner.Candidates(s.Title, s.Artist))
                {
                    string r = Lyrics.Lookup(c.Key, c.Value, "", s.Seconds);
                    if (r == null) { transient = true; continue; }
                    if (r.Length > 0) { lrc = r; source = "LRCLIB"; break; }
                }
            }

            if (lrc == null && !onlyTranscribe && !s.Local && YtDlp.Installed && Settings.Get("lyrics.captions", "1") == "1")
            {
                bool capTransient;
                string c = Captions(s, out capTransient);
                if (c != null) { lrc = c; source = "caption YouTube"; }
                else if (capTransient) transient = true;
            }

            if (lrc == null && keys && (force || Settings.Get("transcribe.auto", "1") == "1") && File.Exists(s.AudioPath))
            {
                string src, err;
                string t = Transcribe.Run(s.AudioPath, out src, out err);
                if (t != null && t.Length > 0) { lrc = t; source = "transkrip " + src; }
                else if (err != null && err.StartsWith("HTTP 5")) transient = true;
            }

            if (lrc != null)
            {
                Directory.CreateDirectory(Library.MusicDir);
                File.WriteAllText(path, lrc, Encoding.UTF8);
                done(Lyrics.Parse(lrc), source, true);
                return;
            }
            if (!transient)
            {
                try { File.WriteAllText(path, "#none keys=" + (keys ? 1 : 0), Encoding.UTF8); } catch { }
            }
            done(null, null, false);
        }

        // ---- YouTube captions via yt-dlp --------------------------------------------------------------------

        // Two passes, so a machine translation is never mistaken for the sung words:
        //   1. captions uploaded by people, in the preferred languages (exact names, no wildcards: a wildcard also
        //      matches dozens of auto-translated tracks, which is slow and gets the requests rate limited)
        //   2. the video's own automatic captions, the "<lang>-orig" track
        static string Captions(Song s, out bool transient)
        {
            transient = false;
            string tmp = Path.Combine(Library.MusicDir, "tmp");
            Directory.CreateDirectory(tmp);
            CleanTmp(tmp, s.Id);
            string langs = Settings.Get("lyrics.langs", "id,en,ja,ko");
            if (!Regex.IsMatch(langs, @"^[A-Za-z0-9,\-]+$")) langs = "id,en,ja,ko";
            string common = "--skip-download --no-warnings --sub-format json3 -o \"" + tmp + "\\%(id)s.%(ext)s\" -- \"https://www.youtube.com/watch?v=" + s.Id + "\"";
            try
            {
                string output;
                YtDlp.Run("--write-subs --sub-langs \"" + langs + "\" " + common, 90000, out output);
                if (output.Contains("429")) transient = true;
                string lrc = PickCaption(tmp, s.Id, langs.Split(','));
                if (lrc != null) return lrc;

                YtDlp.Run("--write-auto-subs --sub-langs \".*-orig\" " + common, 90000, out output);
                if (output.Contains("429")) transient = true;
                return PickCaption(tmp, s.Id, new string[0]);
            }
            finally { CleanTmp(tmp, s.Id); }
        }

        static void CleanTmp(string tmp, string id)
        {
            foreach (string f in Directory.GetFiles(tmp, id + ".*")) { try { File.Delete(f); } catch { } }
        }

        static string PickCaption(string tmp, string id, string[] preferred)
        {
            string[] files = Directory.GetFiles(tmp, id + ".*.json3");
            if (files.Length == 0) return null;
            string pick = null;
            foreach (string p in preferred)
            {
                pick = files.Where(f => LangOf(f, id).StartsWith(p, StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.Length).FirstOrDefault();
                if (pick != null) break;
            }
            if (pick == null) pick = files.OrderBy(f => f.Length).First();
            string lrc = Json3ToLrc(File.ReadAllText(pick, Encoding.UTF8));
            return lrc.Split('\n').Count(x => x.Length > 0) >= 3 ? lrc : null;
        }
        static string LangOf(string file, string id)
        {
            string n = Path.GetFileName(file);
            return n.Substring(id.Length + 1, n.Length - id.Length - 1 - ".json3".Length);
        }

        // json3: {"events":[{"tStartMs":1000,"dDurationMs":2000,"segs":[{"utf8":"text"}]}]}
        public static string Json3ToLrc(string json)
        {
            var items = new List<KeyValuePair<double, string>>();
            var obj = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>;
            object evs;
            if (obj == null || !obj.TryGetValue("events", out evs) || !(evs is object[])) return "";
            foreach (object o in (object[])evs)
            {
                var e = o as Dictionary<string, object>;
                object segs, start;
                if (e == null || !e.TryGetValue("segs", out segs) || !(segs is object[]) || !e.TryGetValue("tStartMs", out start)) continue;
                var sb = new StringBuilder();
                foreach (object sg in (object[])segs)
                {
                    var d = sg as Dictionary<string, object>;
                    object u;
                    if (d != null && d.TryGetValue("utf8", out u)) sb.Append(Convert.ToString(u));
                }
                string text = Regex.Replace(sb.ToString().Replace('\n', ' '), @"\s+", " ").Trim();
                if (text.Length == 0) continue;
                if (Regex.IsMatch(text, @"^[\[\(][^\]\)]*[\]\)]$")) text = "\u266A"; // [Music], (Applause)
                items.Add(new KeyValuePair<double, string>(Convert.ToDouble(start, CultureInfo.InvariantCulture) / 1000.0, text));
            }
            return Lyrics.ToLrc(items);
        }
    }
}
