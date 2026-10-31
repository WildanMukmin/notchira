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
                        new AiToolParam("berkas", "string", "Jalur lengkap berkas yang ingin langsung dibuka dengan aplikasi itu, kalau ada", false)
                    },
                    Run = args => OpenApp(ArgStr(args, "nama"), ArgStr(args, "berkas"))
                },
                new AiTool
                {
                    Name = "buat_file",
                    Description = "Membuat atau menimpa satu berkas teks di komputer pengguna dengan isi yang diberikan (skrip, catatan, markdown, dan sejenisnya). Tidak bisa membuat berkas yang bisa langsung dieksekusi Windows (.exe, .bat, .cmd, .ps1, .vbs, .js, dan sejenisnya).",
                    Params = new[]
                    {
                        new AiToolParam("path", "string", "Jalur berkas termasuk nama dan ekstensinya; kalau tanpa folder, disimpan di folder Documents pengguna"),
                        new AiToolParam("isi", "string", "Isi lengkap berkas, sebagai teks")
                    },
                    Run = args => MakeFile(ArgStr(args, "path"), ArgStr(args, "isi"))
                }
            };
        }

        static string ArgStr(Dictionary<string, object> args, string key)
        {
            object v;
            return args != null && args.TryGetValue(key, out v) && v != null ? v.ToString() : "";
        }

        static AiToolResult PlaySong(string query, Action<Action> post)
        {
            query = (query ?? "").Trim();
            if (query.Length == 0) return new AiToolResult("Judul atau nama artis lagu tidak disebutkan.");
            var all = Library.All();
            if (all.Count == 0) return new AiToolResult("Pustaka musik masih kosong, belum ada lagu yang diunduh atau diimpor.");
            string q = query.ToLowerInvariant();
            int idx = all.FindIndex(s => s.Title.ToLowerInvariant().Contains(q) || s.Artist.ToLowerInvariant().Contains(q));
            if (idx < 0) return new AiToolResult("Tidak ada lagu berjudul atau berartis \"" + query + "\" di pustaka.");
            Song song = all[idx];
            post(delegate { LocalPlayer.I.PlayList(all, idx); });
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
            if (filePath.Length > 0 && !File.Exists(filePath))
                return new AiToolResult("Berkas \"" + filePath + "\" tidak ditemukan, " + name + " tidak dibuka dengan berkas itu.");
            try
            {
                if (filePath.Length > 0) Process.Start(path, "\"" + filePath + "\"");
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
                full = Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), path);
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                File.WriteAllText(full, content, System.Text.Encoding.UTF8);
            }
            catch (Exception ex) { return new AiToolResult("Gagal menyimpan berkas: " + ex.Message); }
            return new AiToolResult("Berkas disimpan di " + full + ".", "✎ Menyimpan: " + full);
        }
    }
}
