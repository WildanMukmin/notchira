using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace Notchira
{
    // Dev only (NOTCHIRA_DEMO=lyricstest): exercises the lyrics pipeline against sample data, a local mock of the
    // transcription APIs, and the real LRCLIB / YouTube caption endpoints. Writes lyricstest.log into NOTCHIRA_DUMP.
    static class SelfTest
    {
        static StreamWriter log;

        static void L(string s) { log.WriteLine(s); log.Flush(); }

        public static void Run(string dir, Action finished)
        {
            Directory.CreateDirectory(dir);
            log = new StreamWriter(Path.Combine(dir, "lyricstest.log"), false, Encoding.UTF8);
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (Environment.GetEnvironmentVariable("NOTCHIRA_TEST_ONLY") == "ai") MockAi();
                    else
                    {
                        Titles();
                        Parsers();
                        MockTranscribe();
                        MockAi();
                        Real();
                    }
                }
                catch (Exception ex) { L("EXCEPTION: " + ex); }
                log.Close();
                finished();
            });
        }

        static void SetKeys(string provider, params string[] keys)
        {
            foreach (var k in Secrets.List(provider)) Secrets.Remove(provider, k.Id);
            foreach (string v in keys) Secrets.Add(provider, null, v);
        }

        static string Describe(object node)
        {
            return new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(node);
        }

        static void MockAi()
        {
            L("== assistant against a local mock");
            var http = new HttpListener();
            string prefix = "http://127.0.0.1:18766/";
            http.Prefixes.Add(prefix);
            http.Start();
            var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
            ThreadPool.QueueUserWorkItem(delegate
            {
                while (http.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = http.GetContext(); } catch { return; }
                    var req = ctx.Request;
                    string body = new StreamReader(req.InputStream, Encoding.UTF8).ReadToEnd();
                    var obj = ser.DeserializeObject(body) as Dictionary<string, object>;
                    string path = req.Url.AbsolutePath;
                    string reply = "{}"; int code = 200;
                    if (path.StartsWith("/openai/") || path.StartsWith("/local/"))
                    {
                        string auth = req.Headers["Authorization"];
                        var msgs = obj["messages"] as object[];
                        var roles = new List<string>();
                        foreach (object m in msgs) roles.Add((string)((Dictionary<string, object>)m)["role"]);
                        L("  " + path + " auth=" + (auth ?? "(none)") + " model=" + obj["model"] + " roles=" + string.Join(",", roles.ToArray()));
                        if (auth == "Bearer badkey") { code = 401; reply = "{\"error\":\"invalid key\"}"; }
                        else reply = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"jawaban dari " + (path.StartsWith("/local/") ? "lokal" : "groq") + "\"}}]}";
                    }
                    else if (path.StartsWith("/gem/"))
                    {
                        string key = req.Headers["x-goog-api-key"];
                        var contents = obj["contents"] as object[];
                        var roles = new List<string>();
                        foreach (object c in contents) roles.Add((string)((Dictionary<string, object>)c)["role"]);
                        L("  " + path + " x-goog-api-key=" + key + " roles=" + string.Join(",", roles.ToArray()) + " systemInstruction=" + obj.ContainsKey("systemInstruction"));
                        if (key == "quotakey") { code = 429; reply = "{\"error\":{\"message\":\"You exceeded your current quota\",\"status\":\"RESOURCE_EXHAUSTED\"}}"; }
                        else reply = "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"jawaban dari gemini\"}]}}]}";
                    }
                    else if (path.StartsWith("/anth/"))
                    {
                        L("  " + path + " x-api-key=" + req.Headers["x-api-key"] + " version=" + req.Headers["anthropic-version"] +
                            " max_tokens=" + obj.ContainsKey("max_tokens") + " system=" + obj.ContainsKey("system") + " model=" + obj["model"]);
                        reply = "{\"content\":[{\"type\":\"text\",\"text\":\"jawaban dari claude\"}]}";
                    }
                    byte[] rb = Encoding.UTF8.GetBytes(reply);
                    ctx.Response.StatusCode = code;
                    ctx.Response.OutputStream.Write(rb, 0, rb.Length);
                    ctx.Response.Close();
                }
            });
            Environment.SetEnvironmentVariable("NOTCHIRA_AI_BASE_GROQ", prefix + "openai");
            Environment.SetEnvironmentVariable("NOTCHIRA_AI_BASE_GEMINI", prefix + "gem");
            Environment.SetEnvironmentVariable("NOTCHIRA_AI_BASE_ANTHROPIC", prefix + "anth");
            Environment.SetEnvironmentVariable("NOTCHIRA_AI_BASE_CUSTOM", prefix + "local");

            var hist = new List<AiMessage> { new AiMessage("user", "halo"), new AiMessage("assistant", "hai"), new AiMessage("user", "siapa kamu?") };
            int switches = 0;
            Action<string, string, string> onSwitch = (p, f, t) => { switches++; L("  event: key " + p + " \"" + f + "\" rested, next=" + (t ?? "none")); };
            Secrets.Switched += onSwitch;

            foreach (string p in new[] { "groq", "gemini", "anthropic", "openai", "custom" }) SetKeys(p);

            SetKeys("groq", "badkey", "goodkey");
            var r = Ai.Ask(hist, "sistem", "groq", false);
            L("T1 groq rotation: text=\"" + r.Text + "\" provider=" + r.Provider + " key=" + r.KeyLabel + " switches=" + switches);
            foreach (var k in Secrets.List("groq")) L("   key " + k.Label + " active=" + k.Active + " limited=" + k.Limited);

            r = Ai.Ask(hist, "sistem", "groq", false);
            L("T7 second ask goes straight to the active key: key=" + r.KeyLabel + " (no new 401 in the log above)");

            SetKeys("gemini", "quotakey");
            r = Ai.Ask(hist, "sistem", "gemini", true);
            L("T2 gemini out of quota, fallback on: text=\"" + r.Text + "\" provider=" + r.Provider);
            foreach (var k in Secrets.List("gemini")) L("   gemini key " + k.Label + " limited=" + k.Limited);

            try { Ai.Ask(hist, "sistem", "gemini", false); L("T3 UNEXPECTED success"); }
            catch (Exception ex) { L("T3 gemini, fallback off: error=\"" + ex.Message + "\""); }

            SetKeys("anthropic", "antkey");
            r = Ai.Ask(hist, "sistem", "anthropic", false);
            L("T4 anthropic: text=\"" + r.Text + "\" provider=" + r.Provider);

            r = Ai.Ask(hist, "sistem", "custom", false);
            L("T5 custom without key: text=\"" + r.Text + "\" provider=" + r.Provider);

            try { Ai.Ask(hist, "sistem", "openai", false); L("T6 UNEXPECTED success"); }
            catch (Exception ex) { L("T6 no key: error=\"" + ex.Message + "\""); }

            // old single-line key format still loads
            string legacy = Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(Encoding.UTF8.GetBytes("old1\nold2"), null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
            string file = Path.Combine(Settings.Dir, "keys.dat");
            File.AppendAllText(file, "legacy\t" + legacy + "\r\n");
            var li = Secrets.List("legacy");
            L("T8 legacy format: " + li.Count + " keys, labels=" + string.Join(",", li.ConvertAll(x => x.Label).ToArray()));

            string raw = File.ReadAllText(file);
            L("T9 keys.dat holds no plaintext: " + (!raw.Contains("goodkey") && !raw.Contains("antkey") && !raw.Contains("quotakey")));
            Secrets.Switched -= onSwitch;
            foreach (string p in new[] { "groq", "gemini", "anthropic", "openai", "custom", "legacy", "deepgram" }) SetKeys(p);
            http.Stop();
        }

        static void Titles()
        {
            L("== title cleaner");
            string[][] cases =
            {
                new[] { "Rick Astley - Never Gonna Give You Up (Official Video) (4K Remaster)", "Rick Astley" },
                new[] { "Daniel Caesar - Best Part ft. H.E.R. [Official Audio]", "Daniel Caesar" },
                new[] { "Lagu Enak (Official Lyric Video) | Label Musik", "Artis VEVO" },
                new[] { "Blank Space", "Taylor Swift - Topic" }
            };
            foreach (var c in cases)
            {
                var list = TitleCleaner.Candidates(c[0], c[1]);
                var parts = new List<string>();
                foreach (var kv in list) parts.Add("[" + kv.Key + " | " + kv.Value + "]");
                L(c[0] + "  =>  " + string.Join(" ", parts.ToArray()));
            }
        }

        static void Parsers()
        {
            L("== parsers");
            string j3 = "{\"events\":[{\"tStartMs\":0,\"dDurationMs\":900,\"segs\":[{\"utf8\":\"[Music]\"}]}," +
                "{\"tStartMs\":1500,\"segs\":[{\"utf8\":\"Hello \"},{\"utf8\":\"world\"}]},{\"tStartMs\":4000,\"segs\":[{\"utf8\":\"\\n\"}]}," +
                "{\"tStartMs\":62250,\"segs\":[{\"utf8\":\"Satu menit lewat\"}]}]}";
            L("json3 -> " + SongLyrics.Json3ToLrc(j3).Replace("\n", " / "));
            string g = "{\"segments\":[{\"start\":0.5,\"end\":2,\"text\":\" Halo dunia\"},{\"start\":3.25,\"text\":\"baris dua\"}]}";
            L("groq -> " + Transcribe.ParseGroq(g).Replace("\n", " / "));
            string d = "{\"results\":{\"utterances\":[{\"start\":1.0,\"end\":2.0,\"transcript\":\"hello deepgram\"}]}}";
            L("deepgram -> " + Transcribe.ParseDeepgram(d).Replace("\n", " / "));
        }

        static void MockTranscribe()
        {
            L("== transcription against a local mock");
            string audio = Path.Combine(Settings.Dir, "fake.m4a");
            Directory.CreateDirectory(Settings.Dir);
            var rnd = new Random(1);
            var data = new byte[300 * 1024];
            rnd.NextBytes(data);
            File.WriteAllBytes(audio, data);

            var http = new HttpListener();
            string prefix = "http://127.0.0.1:18765/";
            http.Prefixes.Add(prefix);
            http.Start();
            ThreadPool.QueueUserWorkItem(delegate
            {
                while (http.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = http.GetContext(); } catch { return; }
                    var req = ctx.Request;
                    var ms = new MemoryStream();
                    req.InputStream.CopyTo(ms);
                    string auth = req.Headers["Authorization"];
                    string ct = req.ContentType;
                    string bodyText = Encoding.UTF8.GetString(ms.ToArray(), 0, (int)Math.Min(ms.Length, 700));
                    bool multipart = ct != null && ct.StartsWith("multipart/form-data; boundary=");
                    L("  request " + req.Url.AbsolutePath + " auth=" + auth + " type=" + (ct ?? "").Split(';')[0] + " bytes=" + ms.Length +
                        (multipart ? " model=" + (bodyText.Contains("name=\"model\"") && bodyText.Contains("whisper-large-v3-turbo")) +
                        " file=" + bodyText.Contains("name=\"file\"") + " fmt=" + bodyText.Contains("verbose_json") : ""));
                    string reply; int code = 200;
                    if (auth == "Bearer badkey") { code = 401; reply = "{\"error\":\"invalid\"}"; }
                    else if (req.Url.AbsolutePath == "/groq") reply = "{\"segments\":[{\"start\":1.5,\"text\":\"baris satu\"},{\"start\":6.0,\"text\":\"baris dua\"}]}";
                    else reply = "{\"results\":{\"utterances\":[{\"start\":2.0,\"transcript\":\"hello from deepgram\"}]}}";
                    byte[] rb = Encoding.UTF8.GetBytes(reply);
                    ctx.Response.StatusCode = code;
                    ctx.Response.OutputStream.Write(rb, 0, rb.Length);
                    ctx.Response.Close();
                }
            });

            Environment.SetEnvironmentVariable("NOTCHIRA_GROQ_URL", prefix + "groq");
            Environment.SetEnvironmentVariable("NOTCHIRA_DEEPGRAM_URL", prefix + "deepgram");
            string src, err;
            SetKeys("groq", "badkey", "goodkey");
            string r = Transcribe.Run(audio, out src, out err);
            L("groq (bad key first, then good): source=" + src + " err=" + err + " lrc=" + (r ?? "null").Replace("\n", " / "));

            SetKeys("groq");
            SetKeys("deepgram", "dgkey");
            r = Transcribe.Run(audio, out src, out err);
            L("deepgram only: source=" + src + " err=" + err + " lrc=" + (r ?? "null").Replace("\n", " / "));

            SetKeys("deepgram");
            r = Transcribe.Run(audio, out src, out err);
            L("no keys: result=" + (r ?? "null") + " err=" + err);
            L("keys file exists=" + File.Exists(Path.Combine(Settings.Dir, "keys.dat")) + " (empty list removes provider)");
            http.Stop();
        }

        static void Real()
        {
            L("== real lookups");
            var song = new Song
            {
                Id = "dQw4w9WgXcQ",
                Title = "Rick Astley - Never Gonna Give You Up (Official Video) (4K Remaster)",
                Artist = "Rick Astley",
                Seconds = 213,
                Added = DateTime.UtcNow.Ticks
            };
            var done = new ManualResetEvent(false);
            SongLyrics.Resolve(song, true, (l, src, fresh) =>
            {
                L("LRCLIB path: found=" + (l != null) + " source=" + src + (l != null ? " lines=" + l.Lines.Length + " first=\"" + l.Lines[0] + "\"" : ""));
                done.Set();
            });
            done.WaitOne(60000);

            // captions only: LRCLIB off, no keys stored
            Settings.Set("lyrics.online", "0");
            done.Reset();
            if (YtDlp.Installed)
            {
                SongLyrics.Resolve(song, true, (l, src, fresh) =>
                {
                    L("caption path (LRCLIB off): found=" + (l != null) + " source=" + src + (l != null ? " lines=" + l.Lines.Length + " first=\"" + l.Lines[0] + "\"" : ""));
                    done.Set();
                });
                done.WaitOne(150000);
            }
            else L("caption path skipped: yt-dlp not installed in this data dir");
            Settings.Set("lyrics.online", "1");
        }
    }
}
