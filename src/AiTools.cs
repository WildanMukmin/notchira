using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Notchira
{
    // Local functions the chat assistant can call (Ai.cs runs them through the provider's tool-calling
    // protocol). Each one only touches what Notchira already has local, read-only access to; nothing here
    // reaches the network beyond what the tool itself says it does.
    static class AiTools
    {
        // Friendly names for apps whose own exe isn't named after them in Windows' App Paths registry.
        static readonly Dictionary<string, string> AppAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "word", "WINWORD.EXE" }, { "ms word", "WINWORD.EXE" }, { "microsoft word", "WINWORD.EXE" },
            { "excel", "EXCEL.EXE" }, { "ms excel", "EXCEL.EXE" }, { "microsoft excel", "EXCEL.EXE" },
            { "powerpoint", "POWERPNT.EXE" }, { "ms powerpoint", "POWERPNT.EXE" },
            { "chrome", "chrome.exe" }, { "google chrome", "chrome.exe" },
            { "edge", "msedge.exe" }, { "microsoft edge", "msedge.exe" },
            { "notepad", "notepad.exe" }, { "explorer", "explorer.exe" }, { "file explorer", "explorer.exe" }
        };

        // Extensions Windows runs by itself (double-click or a shell verb), kept off buat_file so the
        // assistant can write documents and scripts but not something that executes on its own.
        static readonly string[] BlockedExt =
        {
            ".exe", ".bat", ".cmd", ".com", ".scr", ".msi", ".msp", ".ps1", ".psm1", ".vbs", ".vbe",
            ".js", ".jse", ".wsf", ".wsh", ".msc", ".cpl", ".reg", ".lnk", ".dll", ".sys", ".gadget", ".hta"
        };

        // Common folders by the name a person would actually say, Indonesian and English, so a loosely
        // described location ("taruh di desktop", "folder unduhan") resolves without a literal path.
        static readonly Dictionary<string, Environment.SpecialFolder> FolderWords = new Dictionary<string, Environment.SpecialFolder>(StringComparer.OrdinalIgnoreCase)
        {
            { "desktop", Environment.SpecialFolder.DesktopDirectory }, { "dekstop", Environment.SpecialFolder.DesktopDirectory },
            { "documents", Environment.SpecialFolder.MyDocuments }, { "document", Environment.SpecialFolder.MyDocuments }, { "dokumen", Environment.SpecialFolder.MyDocuments },
            { "pictures", Environment.SpecialFolder.MyPictures }, { "picture", Environment.SpecialFolder.MyPictures }, { "gambar", Environment.SpecialFolder.MyPictures }, { "foto", Environment.SpecialFolder.MyPictures },
            { "music", Environment.SpecialFolder.MyMusic }, { "musik", Environment.SpecialFolder.MyMusic },
            { "videos", Environment.SpecialFolder.MyVideos }, { "video", Environment.SpecialFolder.MyVideos }
        };
        static readonly string[] DownloadWords = { "downloads", "download", "unduhan", "unduh" };

        // Resolves a path the model gave us: rooted paths are trusted as-is; otherwise the first segment
        // is checked against the folder words above (Downloads has no SpecialFolder enum value, so it is
        // just "Downloads" under the user's profile, which is where every normal Windows install puts it);
        // anything left over falls back to the Documents folder, same as before this existed.
        static string ResolvePath(string path)
        {
            path = (path ?? "").Trim();
            if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
            string norm = path.Replace('\\', '/').Trim('/');
            int slash = norm.IndexOf('/');
            string first = slash >= 0 ? norm.Substring(0, slash) : norm;
            string rest = slash >= 0 ? norm.Substring(slash + 1).Replace('/', Path.DirectorySeparatorChar) : "";
            Environment.SpecialFolder special;
            string baseDir = null;
            if (FolderWords.TryGetValue(first, out special)) baseDir = Environment.GetFolderPath(special);
            else if (Array.IndexOf(DownloadWords, first.ToLowerInvariant()) >= 0)
                baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (baseDir != null) return rest.Length > 0 ? Path.GetFullPath(Path.Combine(baseDir, rest)) : baseDir;
            return Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), norm));
        }

        // post marshals a UI-affecting action (changing what plays) onto the island's own thread.
        public static List<AiTool> Default(Action<Action> post)
        {
            return new List<AiTool>
            {
                new AiTool
                {
                    Name = "putar_lagu",
                    Description = "Mencari lagu di pustaka musik Notchira (yang sudah diunduh atau diimpor pengguna) berdasarkan judul atau nama artis, lalu memutarnya.",
                    Params = new[] { new AiToolParam("judul", "string", "Judul lagu atau nama artis yang diminta pengguna") },
                    Run = args => PlaySong(ArgStr(args, "judul"), post)
                },
                new AiTool
                {
                    Name = "buka_aplikasi",
                    Description = "Membuka aplikasi lain yang terpasang di komputer pengguna (dicari lewat daftar aplikasi Windows, lalu lewat pintasan Start Menu), opsional langsung dengan satu berkas (misalnya dokumen) yang sudah ada.",
                    Params = new[]
                    {
                        new AiToolParam("nama", "string", "Nama aplikasi yang ingin dibuka, misalnya \"chrome\", \"word\", atau \"youfarm\""),
                        new AiToolParam("berkas", "string", "Jalur berkas yang ingin langsung dibuka dengan aplikasi itu, kalau ada. Boleh jalur lengkap, atau diawali nama folder umum (Desktop, Documents, Downloads, Pictures, Music, Videos) lalu nama berkasnya, misalnya \"Desktop/laporan.docx\"", false)
                    },
                    Run = args => OpenApp(ArgStr(args, "nama"), ArgStr(args, "berkas"))
                },
                new AiTool
                {
                    Name = "buat_file",
                    Description = "Membuat atau menimpa satu berkas teks di komputer pengguna dengan isi yang diberikan (skrip, catatan, markdown, dan sejenisnya). Tidak bisa membuat berkas yang bisa langsung dieksekusi Windows (.exe, .bat, .cmd, .ps1, .vbs, .js, dan sejenisnya).",
                    Params = new[]
                    {
                        new AiToolParam("path", "string", "Jalur berkas termasuk nama dan ekstensinya. Boleh jalur lengkap, diawali nama folder umum (Desktop, Documents, Downloads, Pictures, Music, Videos) lalu nama berkasnya (misalnya \"Desktop/catatan.md\"), atau cuma nama berkas saja (disimpan di folder Documents)"),
                        new AiToolParam("isi", "string", "Isi lengkap berkas, sebagai teks")
                    },
                    Run = args => MakeFile(ArgStr(args, "path"), ArgStr(args, "isi"))
                },
                new AiTool
                {
                    Name = "baca_file",
                    Description = "Membaca isi satu berkas teks di komputer pengguna (skrip, catatan, markdown, kode, dan sejenisnya) supaya asisten bisa menjawab pertanyaan tentangnya, meringkasnya, atau menyarankan suntingan. Tidak bisa membaca berkas biner (gambar, exe, dan sejenisnya) atau berkas internal Notchira sendiri.",
                    Params = new[] { new AiToolParam("path", "string", "Jalur berkas yang ingin dibaca. Boleh jalur lengkap, diawali nama folder umum (Desktop, Documents, Downloads, Pictures, Music, Videos), atau cuma nama berkas saja (dicari di folder Documents)") },
                    Run = args => ReadFile(ArgStr(args, "path"))
                }
            };
        }

        static string ArgStr(Dictionary<string, object> args, string key)
        {
            object v;
            return args != null && args.TryGetValue(key, out v) && v != null ? v.ToString() : "";
        }

        static readonly char[] WordSplit = { ' ', '\t', '-', '_', ',', '.', '(', ')', '[', ']', '|', '/', '\\' };

        // Scores every song by how many of the query's words show up in its title or artist, instead of
        // requiring the whole query as one literal phrase — so a partial, loosely worded request like
        // "lagu naruto" still finds a title that merely contains "naruto" somewhere in it.
        static AiToolResult PlaySong(string query, Action<Action> post)
        {
            query = (query ?? "").Trim();
            if (query.Length == 0) return new AiToolResult("Judul atau nama artis lagu tidak disebutkan.");
            var all = Library.All();
            if (all.Count == 0) return new AiToolResult("Pustaka musik masih kosong, belum ada lagu yang diunduh atau diimpor.");
            string qLower = query.ToLowerInvariant();
            string[] words = qLower.Split(WordSplit, StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 2).ToArray();
            if (words.Length == 0) words = new[] { qLower };
            int bestIdx = -1, bestScore = 0;
            for (int i = 0; i < all.Count; i++)
            {
                string hay = (all[i].Title + " " + all[i].Artist).ToLowerInvariant();
                int score = words.Count(w => hay.Contains(w));
                if (hay.Contains(qLower)) score += words.Length; // a near-exact phrase match wins over loose word hits
                if (score > bestScore) { bestScore = score; bestIdx = i; }
            }
            if (bestIdx < 0) return new AiToolResult("Tidak ada lagu yang cocok dengan \"" + query + "\" di pustaka.");
            Song song = all[bestIdx];
            post(delegate { LocalPlayer.I.PlayList(all, bestIdx); });
            return new AiToolResult("Memutar \"" + song.Title + "\" oleh " + song.Artist + ".", "▶ Memutar: " + song.Title + " — " + song.Artist);
        }

        // Explicit override (settings.ini, apps.<nama>) first, then Windows' own App Paths registry
        // (what "chrome", "word", "excel" etc. already resolve to for every other app on the machine),
        // then a best-effort name match against Start Menu shortcuts (covers apps with no App Paths entry).
        static string ResolveApp(string name)
        {
            string over = Settings.Get("apps." + name.ToLowerInvariant(), "");
            if (over.Length > 0 && File.Exists(over)) return over;

            string exeName;
            if (!AppAliases.TryGetValue(name, out exeName))
                exeName = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
            string fromPaths = AppPathLookup(exeName);
            if (fromPaths != null) return fromPaths;

            return FindStartMenuShortcut(name);
        }

        static string AppPathLookup(string exeName)
        {
            foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
            {
                try
                {
                    using (var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exeName))
                    {
                        string v = key != null ? key.GetValue("") as string : null;
                        if (!string.IsNullOrEmpty(v) && File.Exists(v)) return v;
                    }
                }
                catch { }
            }
            return null;
        }

        static string FindStartMenuShortcut(string name)
        {
            string needle = name.Trim().ToLowerInvariant();
            if (needle.Length == 0) return null;
            var dirs = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
            };
            foreach (string dir in dirs)
            {
                if (!Directory.Exists(dir)) continue;
                string[] files;
                try { files = Directory.GetFiles(dir, "*.lnk", SearchOption.AllDirectories); } catch { continue; }
                foreach (string f in files)
                {
                    string baseName = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                    if (baseName != needle && !baseName.Contains(needle)) continue;
                    string target = ReadShortcutTarget(f);
                    if (!string.IsNullOrEmpty(target) && File.Exists(target)) return target;
                }
            }
            return null;
        }

        static string ReadShortcutTarget(string lnkPath)
        {
            object shell = null, lnk = null;
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                shell = Activator.CreateInstance(t);
                lnk = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                return lnk.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, lnk, null) as string;
            }
            catch { return null; }
            finally
            {
                if (lnk != null) System.Runtime.InteropServices.Marshal.ReleaseComObject(lnk);
                if (shell != null) System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
            }
        }

        static AiToolResult OpenApp(string name, string filePath)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return new AiToolResult("Nama aplikasi tidak disebutkan.");
            string path = ResolveApp(name);
            if (path == null)
                return new AiToolResult("Aplikasi \"" + name + "\" tidak ditemukan di komputer ini. Bisa didaftarkan manual lewat settings.ini, kunci apps." + name.ToLowerInvariant() + "=<jalur .exe>.");
            filePath = (filePath ?? "").Trim();
            string fullFile = filePath.Length > 0 ? ResolvePath(filePath) : "";
            if (fullFile.Length > 0 && !File.Exists(fullFile))
                return new AiToolResult("Berkas \"" + filePath + "\" tidak ditemukan, " + name + " tidak dibuka dengan berkas itu.");
            try
            {
                if (fullFile.Length > 0) Process.Start(path, "\"" + fullFile + "\"");
                else Process.Start(path);
                string withFile = filePath.Length > 0 ? " (" + Path.GetFileName(filePath) + ")" : "";
                return new AiToolResult("Membuka " + name + withFile + ".", "⧉ Membuka " + name + withFile);
            }
            catch (Exception ex) { return new AiToolResult("Gagal membuka " + name + ": " + ex.Message); }
        }

        static AiToolResult MakeFile(string path, string content)
        {
            path = (path ?? "").Trim();
            if (path.Length == 0) return new AiToolResult("Nama atau jalur berkas tidak disebutkan.");
            content = content ?? "";
            if (content.Length > 2000000) return new AiToolResult("Isi berkas terlalu besar (maksimal sekitar 2 MB).");
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (Array.IndexOf(BlockedExt, ext) >= 0)
                return new AiToolResult("Tidak bisa membuat berkas \"" + ext + "\" karena Windows bisa langsung menjalankannya. Pakai ekstensi lain, misalnya .py, .md, atau .txt.");
            string full;
            try
            {
                full = ResolvePath(path);
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                File.WriteAllText(full, content, System.Text.Encoding.UTF8);
            }
            catch (Exception ex) { return new AiToolResult("Gagal menyimpan berkas: " + ex.Message); }
            return new AiToolResult("Berkas disimpan di " + full + ".", "✎ Menyimpan: " + full);
        }

        const int MaxReadChars = 50000;

        static AiToolResult ReadFile(string path)
        {
            path = (path ?? "").Trim();
            if (path.Length == 0) return new AiToolResult("Nama atau jalur berkas tidak disebutkan.");
            string full;
            try { full = ResolvePath(path); }
            catch (Exception ex) { return new AiToolResult("Jalur berkas tidak valid: " + ex.Message); }
            if (!File.Exists(full)) return new AiToolResult("Berkas \"" + path + "\" tidak ditemukan.");
            // Notchira's own key store and settings never go to a model, even if asked.
            if (full.StartsWith(Settings.Dir, StringComparison.OrdinalIgnoreCase))
                return new AiToolResult("Tidak bisa membaca berkas internal Notchira sendiri demi keamanan.");
            byte[] bytes;
            try { bytes = File.ReadAllBytes(full); }
            catch (Exception ex) { return new AiToolResult("Gagal membaca berkas: " + ex.Message); }
            if (bytes.Length > 5000000) return new AiToolResult("Berkas \"" + Path.GetFileName(full) + "\" terlalu besar untuk dibaca (maksimal sekitar 5 MB).");
            int sample = Math.Min(bytes.Length, 8000);
            for (int i = 0; i < sample; i++)
                if (bytes[i] == 0) return new AiToolResult("Berkas \"" + Path.GetFileName(full) + "\" sepertinya bukan berkas teks (berisi data biner).");
            string text = System.Text.Encoding.UTF8.GetString(bytes);
            bool cut = text.Length > MaxReadChars;
            if (cut) text = text.Substring(0, MaxReadChars);
            string forModel = "Isi " + Path.GetFileName(full) + (cut ? " (" + MaxReadChars + " karakter pertama saja, berkas lebih panjang)" : "") + ":\n" + text;
            return new AiToolResult(forModel, "Membaca: " + full);
        }
    }
}
