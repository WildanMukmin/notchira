using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace Notchira
{
    // Looks for a newer release on GitHub and replaces the exe in place. The download is only used when its SHA-256
    // matches the one printed in the release notes. A running exe can be renamed but not overwritten, so the old one
    // moves aside (.old), the new one takes its path, and the app starts again from there.
    static class Updater
    {
        public const string Current = "0.18";
        const string Api = "https://api.github.com/repos/WildanMukmin/notchira/releases/latest";

        public static string Latest = "";   // newest version found, without the "v"
        public static string Status = "";   // short line for the Atur tab
        public static bool Busy;
        static string assetUrl, sha;

        public static bool Available { get { return Latest.Length > 0 && Newer(Latest, Current); } }
        static string ExePath { get { return Application.ExecutablePath; } }

        static bool Newer(string a, string b)
        {
            Version x, y;
            return Version.TryParse(a, out x) && Version.TryParse(b, out y) && x > y;
        }

        // A leftover from the last update is removed at start.
        public static void CleanUp()
        {
            try { if (File.Exists(ExePath + ".old")) File.Delete(ExePath + ".old"); } catch { }
        }

        // Once a day at most, unless the user turned it off. Quiet: a failure only leaves the status empty.
        public static bool DueForAutoCheck()
        {
            if (Settings.Get("update.auto", "1") != "1" || Environment.GetEnvironmentVariable("NOTCHIRA_DIR") != null) return false;
            long last;
            long.TryParse(Settings.Get("update.checked", "0"), out last);
            return DateTime.UtcNow.Ticks - last > TimeSpan.FromHours(24).Ticks;
        }

        // Runs on a pool thread; done is called there as well.
        public static void Check(bool manual, Action done)
        {
            if (Busy) return;
            Busy = true;
            if (manual) Status = "Memeriksa...";
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
                    var req = (HttpWebRequest)WebRequest.Create(Api);
                    req.UserAgent = "Notchira/" + Current;
                    req.Accept = "application/vnd.github+json";
                    req.Timeout = 15000;
                    string text;
                    using (var res = req.GetResponse())
                    using (var sr = new StreamReader(res.GetResponseStream(), Encoding.UTF8)) text = sr.ReadToEnd();
                    var j = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
                    string tag = ((string)j["tag_name"] ?? "").TrimStart('v', 'V');
                    string body = j.ContainsKey("body") ? (j["body"] as string ?? "") : "";
                    assetUrl = null;
                    foreach (object o in (object[])j["assets"])
                    {
                        var a = (Dictionary<string, object>)o;
                        if ((string)a["name"] == "Notchira.exe") assetUrl = (string)a["browser_download_url"];
                    }
                    var m = Regex.Match(body, @"SHA-256:\s*`?([0-9A-Fa-f]{64})");
                    sha = m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
                    Latest = tag;
                    Settings.Set("update.checked", DateTime.UtcNow.Ticks.ToString());
                    Status = Available ? "Versi " + tag + " tersedia" : "Sudah versi terbaru";
                }
                catch (Exception ex) { if (manual) Status = "Gagal memeriksa: " + Describe(ex); Log(ex); }
                finally { Busy = false; if (done != null) done(); }
            });
        }

        // The download, with the timeouts set and the progress shown. HttpWebRequest follows GitHub's redirect to its file host.
        static void Download(string url, string to)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "Notchira/" + Current;
            req.Timeout = 30000;
            req.ReadWriteTimeout = 30000;
            using (var res = (HttpWebResponse)req.GetResponse())
            using (var src = res.GetResponseStream())
            using (var dst = File.Create(to))
            {
                var buf = new byte[81920];
                long total = res.ContentLength, got = 0;
                int n, last = -1;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    dst.Write(buf, 0, n);
                    got += n;
                    int pct = total > 0 ? (int)(got * 100 / total) : 0;
                    if (pct != last && pct % 5 == 0) { last = pct; Status = "Mengunduh... " + pct + "%"; }
                }
            }
        }

        // The innermost message, which is the real reason; the outer ones are generic wrappers
        // ("An exception occurred during a WebClient request"). A web error adds its status or HTTP code.
        static string Describe(Exception ex)
        {
            var parts = new List<string>();
            for (Exception e = ex; e != null; e = e.InnerException)
            {
                var we = e as WebException;
                var hr = we == null ? null : we.Response as HttpWebResponse;
                string m = e.Message;
                if (hr != null) m += " (HTTP " + (int)hr.StatusCode + ")";
                else if (we != null) m += " [" + we.Status + "]";
                if (!parts.Contains(m)) parts.Add(m);
            }
            return string.Join(" > ", parts.ToArray());
        }

        // The whole exception goes to update.log in the data folder, so a failure on another computer can be read.
        static void Log(Exception ex)
        {
            try
            {
                Directory.CreateDirectory(Settings.Dir);
                File.AppendAllText(Path.Combine(Settings.Dir, "update.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " v" + Current + " " + Environment.OSVersion + Environment.NewLine + ex + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }

        // Downloads, verifies, swaps, and restarts. done(false, message) on any failure; on success the app exits.
        public static void Apply(Action done, Action exit)
        {
            if (Busy || !Available) return;
            if (assetUrl == null || !assetUrl.StartsWith("https://github.com/WildanMukmin/notchira/", StringComparison.Ordinal))
            { Status = "Berkas rilis tidak ditemukan"; return; }
            if (sha == null) { Status = "Rilis tidak memuat SHA-256, tidak dipasang"; return; }
            Busy = true;
            Status = "Mengunduh...";
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                string exe = ExePath, tmp = exe + ".new", old = exe + ".old";
                bool swapped = false;
                try
                {
                    ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
                    Download(assetUrl, tmp);
                    string got;
                    using (var h = SHA256.Create())
                    using (var fs = File.OpenRead(tmp))
                    {
                        var sb = new StringBuilder();
                        foreach (byte b in h.ComputeHash(fs)) sb.Append(b.ToString("x2"));
                        got = sb.ToString();
                    }
                    if (got != sha) { File.Delete(tmp); Status = "SHA-256 tidak cocok, berkas dibuang"; return; }
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(exe, old);
                    swapped = true;
                    File.Move(tmp, exe);
                    Status = "Memulai ulang...";
                    var psi = new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & start \"\" \"" + exe + "\"")
                    { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
                    Process.Start(psi);
                    exit();
                }
                catch (Exception ex)
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    // if the swap broke halfway, put the working exe back
                    try { if (swapped && !File.Exists(exe)) File.Move(old, exe); } catch { }
                    Status = "Gagal memperbarui: " + Describe(ex);
                    Log(ex);
                }
                finally { Busy = false; if (done != null) done(); }
            });
        }
    }
}
