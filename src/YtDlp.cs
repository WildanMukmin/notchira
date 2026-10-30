using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Notchira
{
    // Wraps yt-dlp.exe, which Notchira fetches itself on request. The exe is kept in
    // %APPDATA%\Notchira\tools (not in the repo) and checked against the SHA-256 list published with the release.
    static class YtDlp
    {
        const string ReleaseBase = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/";
        static readonly Regex IdPattern = new Regex(@"^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);

        public static string Exe { get { return Path.Combine(Settings.Dir, "tools", "yt-dlp.exe"); } }
        public static bool Installed { get { return File.Exists(Exe); } }

        static Process running;

        static void Tls() { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; }

        // Only YouTube links are accepted; the URL is later passed after "--" so it can never act as an option.
        public static bool TryParseUrl(string text, out string url)
        {
            url = null;
            Uri u;
            if (!Uri.TryCreate((text ?? "").Trim(), UriKind.Absolute, out u)) return false;
            if (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp) return false;
            string h = u.Host.ToLowerInvariant();
            bool ok = h == "youtu.be" || h == "youtube.com" || h.EndsWith(".youtube.com");
            if (!ok || u.AbsoluteUri.Contains("\"")) return false;
            url = u.AbsoluteUri;
            return true;
        }

        // ---- install and update ---------------------------------------------------------------------------

        // Runs on a pool thread. done(ok, message).
        public static void Install(Action<string> status, Action<bool, string> done)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                string tmp = null;
                try
                {
                    Tls();
                    Directory.CreateDirectory(Path.GetDirectoryName(Exe));
                    status("Mengambil daftar SHA-256 rilis...");
                    string sums;
                    using (var wc = new WebClient()) sums = wc.DownloadString(ReleaseBase + "SHA2-256SUMS");
                    string want = null;
                    foreach (string line in sums.Split('\n'))
                    {
                        string[] p = line.Trim().Split(new[] { ' ', '*' }, StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length == 2 && p[1] == "yt-dlp.exe") want = p[0].ToLowerInvariant();
                    }
                    if (want == null) { done(false, "SHA-256 yt-dlp.exe tidak ditemukan di rilis."); return; }

                    status("Mengunduh yt-dlp.exe (sekitar 25 MB)...");
                    tmp = Exe + ".download";
                    using (var wc = new WebClient()) wc.DownloadFile(ReleaseBase + "yt-dlp.exe", tmp);

                    string got;
                    using (var sha = SHA256.Create())
                    using (var fs = File.OpenRead(tmp))
                    {
                        var sb = new StringBuilder();
                        foreach (byte b in sha.ComputeHash(fs)) sb.Append(b.ToString("x2"));
                        got = sb.ToString();
                    }
                    if (got != want)
                    {
                        File.Delete(tmp);
                        done(false, "SHA-256 tidak cocok, berkas dibuang.");
                        return;
                    }
                    if (File.Exists(Exe)) File.Delete(Exe);
                    File.Move(tmp, Exe);
                    done(true, "yt-dlp terpasang.");
                }
                catch (Exception ex)
                {
                    try { if (tmp != null && File.Exists(tmp)) File.Delete(tmp); } catch { }
                    done(false, ex.Message);
                }
            });
        }

        // yt-dlp updates itself (-U); YouTube changes often enough that this matters.
        public static void SelfUpdate(Action<bool, string> done)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    var psi = NewStart("-U");
                    using (var p = Process.Start(psi))
                    {
                        string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                        p.WaitForExit(120000);
                        string last = "";
                        foreach (string l in o.Split('\n')) if (l.Trim().Length > 0) last = l.Trim();
                        done(p.ExitCode == 0, last);
                    }
                }
                catch (Exception ex) { done(false, ex.Message); }
            });
        }

        static ProcessStartInfo NewStart(string args)
        {
            var psi = new ProcessStartInfo(Exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(Exe)
            };
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            psi.EnvironmentVariables["PYTHONUTF8"] = "1";
            return psi;
        }

        // Runs yt-dlp with ready-made arguments; output holds stdout and stderr. False on timeout or failure.
        public static bool Run(string args, int timeoutMs, out string output)
        {
            output = "";
            if (!Installed) return false;
            try
            {
                var sb = new StringBuilder();
                using (var p = Process.Start(NewStart(args)))
                {
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } output = sb.ToString(); return false; }
                    p.WaitForExit();
                    output = sb.ToString();
                    return p.ExitCode == 0;
                }
            }
            catch (Exception ex) { output = ex.Message; return false; }
        }

        // ---- audio download -------------------------------------------------------------------------------

        public const int PlaylistMax = 50;
        static volatile bool cancelAll;

        // A playlist page (youtube.com/playlist?list=...). A watch link that merely carries a list stays one song.
        public static bool IsPlaylistUrl(string url)
        {
            Uri u;
            if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out u)) return false;
            return u.AbsolutePath.TrimEnd('/') == "/playlist" && Regex.IsMatch(u.Query, @"[?&]list=[A-Za-z0-9_-]+");
        }

        // Runs on a pool thread. Lists the playlist, then downloads each song with the single-song path.
        // listed(name, total, truncated) once the entries are known; progress(index, percent); got(id, song) per song
        // (song is null when the file was already on disk); done(failed, error), error null when something arrived.
        public static void DownloadPlaylist(string url, Action<string, int, bool> listed, Action<int, int> progress,
            Action<string, Song> got, Action<int, string> done)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                int failed = 0;
                try
                {
                    cancelAll = false;
                    string output;
                    Run("--flat-playlist --no-warnings --playlist-end " + (PlaylistMax + 1) +
                        " --print \"PL|||%(playlist_title)s|||%(id)s\" -- \"" + url + "\"", 120000, out output);
                    string name = null;
                    var ids = new List<string>();
                    foreach (string line in output.Split('\n'))
                    {
                        string[] m = line.Trim().Split(new[] { "|||" }, StringSplitOptions.None);
                        if (m.Length != 3 || m[0] != "PL" || !IdPattern.IsMatch(m[2]) || ids.Contains(m[2])) continue;
                        if (name == null && m[1] != "NA" && m[1].Length > 0) name = m[1];
                        ids.Add(m[2]);
                    }
                    bool truncated = ids.Count > PlaylistMax;
                    if (truncated) ids.RemoveRange(PlaylistMax, ids.Count - PlaylistMax);
                    if (ids.Count == 0) { done(0, "Playlist kosong, privat, atau tidak bisa dibaca."); return; }
                    listed(name, ids.Count, truncated);

                    int ok = 0;
                    for (int i = 0; i < ids.Count && !cancelAll; i++)
                    {
                        string id = ids[i];
                        int index = i;
                        if (File.Exists(new Song { Id = id }.AudioPath)) { got(id, null); ok++; continue; }
                        using (var wait = new System.Threading.ManualResetEventSlim(false))
                        {
                            Song result = null;
                            Download("https://www.youtube.com/watch?v=" + id, p => progress(index, p), (s, err) => { result = s; wait.Set(); });
                            wait.Wait();
                            if (result != null) { got(id, result); ok++; } else failed++;
                        }
                    }
                    done(failed, ok == 0 ? (cancelAll ? "Dibatalkan." : "Tidak ada lagu yang berhasil diunduh.") : null);
                }
                catch (Exception ex) { done(failed, ex.Message); }
            });
        }

        public static void Cancel()
        {
            cancelAll = true;
            try { if (running != null && !running.HasExited) running.Kill(); } catch { }
        }

        // Runs on a pool thread. progress(percent), done(song or null, error message).
        public static void Download(string url, Action<int> progress, Action<Song, string> done)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                string error = "Unduhan gagal.";
                try
                {
                    Directory.CreateDirectory(Library.MusicDir);
                    string args = "--no-playlist --no-warnings --no-simulate --newline --progress " +
                        "-f \"bestaudio[ext=m4a]\" " +
                        "--progress-template \"download:PROG %(progress._percent_str)s\" " +
                        "--print \"pre_process:META|||%(id)s|||%(title)s|||%(uploader)s|||%(duration)s\" " +
                        "-o \"" + Library.MusicDir + "\\%(id)s.%(ext)s\" -- \"" + url + "\"";
                    var psi = NewStart(args);
                    Song song = null;
                    string lastErr = "";
                    using (var p = Process.Start(psi))
                    {
                        running = p;
                        p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) lastErr = e.Data.Trim(); };
                        p.BeginErrorReadLine();
                        string line;
                        while ((line = p.StandardOutput.ReadLine()) != null)
                        {
                            if (line.StartsWith("META|||"))
                            {
                                string[] m = line.Split(new[] { "|||" }, StringSplitOptions.None);
                                if (m.Length >= 5 && IdPattern.IsMatch(m[1]))
                                {
                                    double secs;
                                    double.TryParse(m[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out secs);
                                    song = new Song
                                    {
                                        Id = m[1], Title = m[2], Artist = m[3] == "NA" ? "" : m[3],
                                        Seconds = (int)secs, Added = DateTime.UtcNow.Ticks
                                    };
                                }
                            }
                            else if (line.StartsWith("PROG"))
                            {
                                double pc;
                                string num = line.Substring(4).Replace("%", "").Trim();
                                if (double.TryParse(num, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out pc))
                                    progress((int)Math.Max(0, Math.Min(100, pc)));
                            }
                        }
                        if (!p.WaitForExit(600000)) { try { p.Kill(); } catch { } error = "Waktu habis."; }
                        else if (p.ExitCode != 0) error = lastErr.Length > 0 ? lastErr : error;
                        else if (song != null && File.Exists(song.AudioPath)) { }
                        else { song = null; error = "File audio m4a tidak dibuat."; }
                        running = null;
                    }
                    if (song != null && File.Exists(song.AudioPath))
                    {
                        FetchThumb(song);
                        done(song, null);
                        return;
                    }
                }
                catch (Exception ex) { error = ex.Message; }
                done(null, error);
            });
        }

        static void FetchThumb(Song s)
        {
            Tls();
            foreach (string name in new[] { "mqdefault", "hqdefault", "default" })
            {
                try
                {
                    using (var wc = new WebClient())
                    {
                        byte[] data = wc.DownloadData("https://i.ytimg.com/vi/" + s.Id + "/" + name + ".jpg");
                        using (var ms = new MemoryStream(data)) using (var img = Image.FromStream(ms)) { }
                        File.WriteAllBytes(s.ThumbPath, data);
                        return;
                    }
                }
                catch { }
            }
        }
    }
}
