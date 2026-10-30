using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Notchira
{
    sealed class Song
    {
        public string Id = "", Title = "", Artist = "";
        public int Seconds;
        public long Added;
        public string Ext = "m4a";   // downloads are m4a; imported files keep their own extension
        public bool Local;           // imported from disk: the id is not a YouTube video id

        public string AudioPath { get { return Path.Combine(Library.MusicDir, Id + "." + Ext); } }
        public string ThumbPath { get { return Path.Combine(Library.MusicDir, Id + ".jpg"); } }
    }

    // Downloaded songs and playlists. Plain tab-separated files, so there is no parser to load and
    // nothing to carry in memory beyond the rows.
    //   music\library.tsv    id  title  artist  seconds  added  [ext  local]  [ext  local]
    //   music\playlists.tsv  name  id,id,id
    static class Library
    {
        public static readonly string MusicDir = Path.Combine(Settings.Dir, "music");
        static string LibFile { get { return Path.Combine(MusicDir, "library.tsv"); } }
        static string ListFile { get { return Path.Combine(MusicDir, "playlists.tsv"); } }

        static List<Song> songs;
        static List<KeyValuePair<string, List<string>>> lists;

        static string Clean(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder();
            foreach (char c in s) sb.Append(c < 0x20 ? ' ' : c);
            return sb.ToString().Trim();
        }

        static void EnsureLoaded()
        {
            if (songs != null) return;
            songs = new List<Song>();
            lists = new List<KeyValuePair<string, List<string>>>();
            try
            {
                if (File.Exists(LibFile))
                    foreach (string line in File.ReadAllLines(LibFile, Encoding.UTF8))
                    {
                        string[] p = line.Split('\t');
                        if (p.Length < 5) continue;
                        int sec; long add;
                        int.TryParse(p[3], out sec);
                        long.TryParse(p[4], out add);
                        string ext = p.Length > 5 && p[5].Length > 0 ? p[5] : "m4a";
                        songs.Add(new Song { Id = p[0], Title = p[1], Artist = p[2], Seconds = sec, Added = add, Ext = ext, Local = p.Length > 6 && p[6] == "1" });
                    }
                if (File.Exists(ListFile))
                    foreach (string line in File.ReadAllLines(ListFile, Encoding.UTF8))
                    {
                        string[] p = line.Split('\t');
                        if (p.Length < 2 || p[0].Length == 0) continue;
                        lists.Add(new KeyValuePair<string, List<string>>(p[0],
                            p[1].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList()));
                    }
            }
            catch { }
        }

        static void WriteAtomic(string file, IEnumerable<string> lines)
        {
            try
            {
                Directory.CreateDirectory(MusicDir);
                string tmp = file + ".tmp";
                File.WriteAllLines(tmp, lines.ToArray(), Encoding.UTF8);
                if (File.Exists(file)) File.Delete(file);
                File.Move(tmp, file);
            }
            catch { }
        }

        static void Save()
        {
            WriteAtomic(LibFile, songs.Select(s =>
                s.Id + "\t" + Clean(s.Title) + "\t" + Clean(s.Artist) + "\t" + s.Seconds + "\t" + s.Added + "\t" + s.Ext + "\t" + (s.Local ? "1" : "0")));
            WriteAtomic(ListFile, lists.Select(l => Clean(l.Key) + "\t" + string.Join(",", l.Value.ToArray())));
        }

        public static List<Song> All()
        {
            EnsureLoaded();
            return songs.OrderByDescending(s => s.Added).ToList();
        }

        public static Song Find(string id)
        {
            EnsureLoaded();
            return songs.FirstOrDefault(s => s.Id == id);
        }

        public static void Add(Song s)
        {
            EnsureLoaded();
            songs.RemoveAll(x => x.Id == s.Id);
            songs.Add(s);
            Save();
        }

        public static void Remove(string id)
        {
            EnsureLoaded();
            Song s = Find(id);
            songs.RemoveAll(x => x.Id == id);
            foreach (var l in lists) l.Value.RemoveAll(x => x == id);
            Save();
            if (s != null)
            {
                try { File.Delete(s.AudioPath); } catch { }
                try { File.Delete(s.ThumbPath); } catch { }
            }
        }

        public static List<string> PlaylistNames()
        {
            EnsureLoaded();
            return lists.Select(l => l.Key).ToList();
        }

        public static List<Song> Playlist(string name)
        {
            EnsureLoaded();
            var l = lists.FirstOrDefault(x => x.Key == name);
            var result = new List<Song>();
            if (l.Value == null) return result;
            foreach (string id in l.Value)
            {
                Song s = Find(id);
                if (s != null) result.Add(s);
            }
            return result;
        }

        // Returns the stored name (cleaned), creating the playlist when it does not exist yet.
        public static string EnsurePlaylist(string name)
        {
            EnsureLoaded();
            name = Clean(name);
            if (name.Length == 0) name = "Playlist YouTube";
            if (!lists.Any(l => l.Key == name))
            {
                lists.Add(new KeyValuePair<string, List<string>>(name, new List<string>()));
                Save();
            }
            return name;
        }

        public static bool CreatePlaylist(string name)
        {
            EnsureLoaded();
            name = Clean(name);
            if (name.Length == 0 || lists.Any(l => l.Key == name)) return false;
            lists.Add(new KeyValuePair<string, List<string>>(name, new List<string>()));
            Save();
            return true;
        }

        public static void DeletePlaylist(string name)
        {
            EnsureLoaded();
            lists.RemoveAll(l => l.Key == name);
            Save();
        }

        public static void AddToPlaylist(string name, string id)
        {
            EnsureLoaded();
            var l = lists.FirstOrDefault(x => x.Key == name);
            if (l.Value == null || l.Value.Contains(id)) return;
            l.Value.Add(id);
            Save();
        }

        public static void RemoveFromPlaylist(string name, string id)
        {
            EnsureLoaded();
            var l = lists.FirstOrDefault(x => x.Key == name);
            if (l.Value == null) return;
            l.Value.RemoveAll(x => x == id);
            Save();
        }
    }
}
