using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Notchira
{
    // Local functions the chat assistant can call (Ai.cs runs them through the provider's tool-calling
    // protocol). Each one only touches what Notchira already has local, read-only access to; nothing here
    // reaches the network beyond what the tool itself says it does.
    static class AiTools
    {
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
                    Description = "Membuka aplikasi lain di komputer pengguna yang jalurnya sudah dikenal Notchira (diatur lewat settings.ini, kunci apps.<nama>).",
                    Params = new[] { new AiToolParam("nama", "string", "Nama aplikasi yang ingin dibuka, misalnya \"youfarm\"") },
                    Run = args => OpenApp(ArgStr(args, "nama"))
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

        static AiToolResult OpenApp(string name)
        {
            name = (name ?? "").Trim().ToLowerInvariant();
            if (name.Length == 0) return new AiToolResult("Nama aplikasi tidak disebutkan.");
            string path = Settings.Get("apps." + name, "");
            if (path.Length == 0 || !System.IO.File.Exists(path))
                return new AiToolResult("Aplikasi \"" + name + "\" belum dikenal Notchira. Tambahkan jalurnya di settings.ini, kunci apps." + name + "=<jalur .exe>.");
            try { Process.Start(path); return new AiToolResult("Membuka " + name + ".", "⧉ Membuka " + name); }
            catch (Exception ex) { return new AiToolResult("Gagal membuka " + name + ": " + ex.Message); }
        }
    }
}
