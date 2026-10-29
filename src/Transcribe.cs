using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace Notchira
{
    // Speech to timed text through Groq (Whisper) or Deepgram. Audio is only sent when the user has stored a key
    // for that provider. Keys are tried in order; a rejected or rate-limited key moves on to the next one.
    static class Transcribe
    {
        const long GroqMaxBytes = 25L * 1024 * 1024;

        static string GroqUrl
        {
            get { return Environment.GetEnvironmentVariable("NOTCHIRA_GROQ_URL") ?? "https://api.groq.com/openai/v1/audio/transcriptions"; }
        }

        static string DeepgramUrl
        {
            get { return Environment.GetEnvironmentVariable("NOTCHIRA_DEEPGRAM_URL") ?? "https://api.deepgram.com/v1/listen"; }
        }

        public static bool Available { get { return Secrets.Has("groq") || Secrets.Has("deepgram"); } }

        // Returns LRC text, or null with a reason in error.
        public static string Run(string audioPath, out string source, out string error)
        {
            source = null;
            error = "Tidak ada kunci API Groq atau Deepgram.";
            string result;
            string e;

            if (Secrets.Has("groq"))
            {
                long size = new FileInfo(audioPath).Length;
                if (size > GroqMaxBytes) error = "File lebih dari 25 MB, terlalu besar untuk Groq.";
                else
                {
                    result = TryKeys(Secrets.Get("groq"), k => Groq(audioPath, k), out e);
                    if (result != null) { source = "Groq"; error = null; return result; }
                    error = "Groq: " + e;
                }
            }
            if (Secrets.Has("deepgram"))
            {
                result = TryKeys(Secrets.Get("deepgram"), k => Deepgram(audioPath, k), out e);
                if (result != null) { source = "Deepgram"; error = null; return result; }
                error = "Deepgram: " + e;
            }
            return null;
        }

        static string TryKeys(List<string> keys, Func<string, string> call, out string error)
        {
            error = "gagal";
            foreach (string k in keys)
            {
                try
                {
                    string r = call(k);
                    if (!string.IsNullOrEmpty(r)) return r;
                    error = "tidak ada ucapan terdeteksi";
                    return null;
                }
                catch (WebException ex)
                {
                    var resp = ex.Response as HttpWebResponse;
                    int code = resp == null ? 0 : (int)resp.StatusCode;
                    error = code == 0 ? ex.Message : "HTTP " + code;
                    if (code == 401 || code == 403 || code == 429 || code == 0) continue; // try the next key
                    return null;
                }
                catch (Exception ex) { error = ex.Message; return null; }
            }
            return null;
        }

        static void Tls() { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; }

        // ---- Groq: multipart upload, segments back --------------------------------------------------------

        static string Groq(string path, string key)
        {
            Tls();
            string boundary = "----notchira" + Guid.NewGuid().ToString("N");
            var head = new StringBuilder();
            Action<string, string> field = (n, v) =>
                head.Append("--" + boundary + "\r\nContent-Disposition: form-data; name=\"" + n + "\"\r\n\r\n" + v + "\r\n");
            field("model", "whisper-large-v3-turbo");
            field("response_format", "verbose_json");
            field("timestamp_granularities[]", "segment");
            string lang = Settings.Get("lyrics.lang", "");
            if (lang.Length > 0 && lang.Length <= 8) field("language", lang);
            head.Append("--" + boundary + "\r\nContent-Disposition: form-data; name=\"file\"; filename=\"audio.m4a\"\r\nContent-Type: audio/mp4\r\n\r\n");
            byte[] headBytes = Encoding.UTF8.GetBytes(head.ToString());
            byte[] tailBytes = Encoding.UTF8.GetBytes("\r\n--" + boundary + "--\r\n");

            var req = (HttpWebRequest)WebRequest.Create(GroqUrl);
            req.Method = "POST";
            req.Headers["Authorization"] = "Bearer " + key;
            req.ContentType = "multipart/form-data; boundary=" + boundary;
            req.AllowWriteStreamBuffering = false;
            req.SendChunked = false;
            req.Timeout = 300000;
            req.ReadWriteTimeout = 300000;
            using (var fs = File.OpenRead(path))
            {
                req.ContentLength = headBytes.Length + fs.Length + tailBytes.Length;
                using (var body = req.GetRequestStream())
                {
                    body.Write(headBytes, 0, headBytes.Length);
                    var buf = new byte[81920];
                    int n;
                    while ((n = fs.Read(buf, 0, buf.Length)) > 0) body.Write(buf, 0, n);
                    body.Write(tailBytes, 0, tailBytes.Length);
                }
            }
            string json = ReadResponse(req);
            return ParseGroq(json);
        }

        public static string ParseGroq(string json)
        {
            var obj = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            if (obj == null) return null;
            object segs;
            var items = new List<KeyValuePair<double, string>>();
            if (obj.TryGetValue("segments", out segs) && segs is object[])
            {
                foreach (object o in (object[])segs)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null || !d.ContainsKey("start") || !d.ContainsKey("text")) continue;
                    items.Add(new KeyValuePair<double, string>(Convert.ToDouble(d["start"], CultureInfo.InvariantCulture), Convert.ToString(d["text"])));
                }
            }
            return Lyrics.ToLrc(items);
        }

        // ---- Deepgram: raw body, utterances back ------------------------------------------------------------

        static string Deepgram(string path, string key)
        {
            Tls();
            string lang = Settings.Get("lyrics.lang", "");
            string url = DeepgramUrl + "?model=nova-2&punctuate=true&utterances=true&" +
                (lang.Length > 0 && lang.Length <= 8 ? "language=" + Uri.EscapeDataString(lang) : "detect_language=true");
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.Headers["Authorization"] = "Token " + key;
            req.ContentType = "audio/mp4";
            req.AllowWriteStreamBuffering = false;
            req.Timeout = 300000;
            req.ReadWriteTimeout = 300000;
            using (var fs = File.OpenRead(path))
            {
                req.ContentLength = fs.Length;
                using (var body = req.GetRequestStream())
                {
                    var buf = new byte[81920];
                    int n;
                    while ((n = fs.Read(buf, 0, buf.Length)) > 0) body.Write(buf, 0, n);
                }
            }
            return ParseDeepgram(ReadResponse(req));
        }

        public static string ParseDeepgram(string json)
        {
            var obj = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            var items = new List<KeyValuePair<double, string>>();
            object results, utt;
            if (obj != null && obj.TryGetValue("results", out results) && results is Dictionary<string, object> &&
                ((Dictionary<string, object>)results).TryGetValue("utterances", out utt) && utt is object[])
            {
                foreach (object o in (object[])utt)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null || !d.ContainsKey("start") || !d.ContainsKey("transcript")) continue;
                    items.Add(new KeyValuePair<double, string>(Convert.ToDouble(d["start"], CultureInfo.InvariantCulture), Convert.ToString(d["transcript"])));
                }
            }
            return Lyrics.ToLrc(items);
        }

        static string ReadResponse(HttpWebRequest req)
        {
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return rd.ReadToEnd();
        }
    }
}
