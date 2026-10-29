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
                    Titles();
                    Parsers();
                    MockTranscribe();
                    Real();
                }
                catch (Exception ex) { L("EXCEPTION: " + ex); }
                log.Close();
                finished();
            });
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
            Secrets.Set("groq", new[] { "badkey", "goodkey" });
            string r = Transcribe.Run(audio, out src, out err);
            L("groq (bad key first, then good): source=" + src + " err=" + err + " lrc=" + (r ?? "null").Replace("\n", " / "));

            Secrets.Set("groq", new string[0]);
            Secrets.Set("deepgram", new[] { "dgkey" });
            r = Transcribe.Run(audio, out src, out err);
            L("deepgram only: source=" + src + " err=" + err + " lrc=" + (r ?? "null").Replace("\n", " / "));

            Secrets.Set("deepgram", new string[0]);
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
