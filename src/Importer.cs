using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Notchira
{
    // Adds songs from disk to the library: the file is copied into the music folder under a generated id, and
    // title, artist, length and cover come from the file's tags (the Windows property system), or from the
    // file name when there are none.
    static class Importer
    {
        public const int MaxFiles = 100;

        static readonly string[] Exts = { ".mp3", ".m4a", ".aac", ".wav", ".flac", ".wma", ".ogg", ".opus" };

        public static string Filter
        {
            get { return "Musik|" + string.Join(";", Exts.Select(e => "*" + e).ToArray()) + "|Semua berkas|*.*"; }
        }

        public static bool IsAudio(string path)
        {
            return Exts.Contains((Path.GetExtension(path) ?? "").ToLowerInvariant());
        }

        // Folders are searched one level deep, which covers dropping an album folder.
        public static List<string> Expand(IEnumerable<string> paths)
        {
            var result = new List<string>();
            foreach (string p in paths)
            {
                try
                {
                    if (Directory.Exists(p))
                        result.AddRange(Directory.GetFiles(p).Where(IsAudio).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
                    else if (File.Exists(p) && IsAudio(p)) result.Add(p);
                }
                catch { }
            }
            return result.Take(MaxFiles).ToList();
        }

        // Runs on a pool thread. progress(done, total), finished(imported songs, skipped count).
        public static void Run(List<string> files, Action<int, int> progress, Action<List<Song>, int> finished)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                var added = new List<Song>();
                int skipped = 0;
                for (int i = 0; i < files.Count; i++)
                {
                    progress(i, files.Count);
                    Song s = null;
                    try { s = ImportOne(files[i]); } catch { }
                    if (s != null) added.Add(s); else skipped++;
                }
                progress(files.Count, files.Count);
                finished(added, skipped);
            });
        }

        // AsTask() generics are not resolvable with the bundled csc and the winmd references, so wait on Completed.
        static T Await<T>(Windows.Foundation.IAsyncOperation<T> op)
        {
            using (var done = new System.Threading.ManualResetEventSlim(false))
            {
                op.Completed = delegate { done.Set(); };
                if (!done.Wait(8000)) throw new TimeoutException();
                return op.GetResults();
            }
        }

        static Song ImportOne(string path)
        {
            if (!File.Exists(path) || !IsAudio(path)) return null;
            string ext = Path.GetExtension(path).ToLowerInvariant().TrimStart('.');
            string id = "L" + Guid.NewGuid().ToString("N").Substring(0, 10);
            Directory.CreateDirectory(Library.MusicDir);

            string title = null, artist = null;
            int secs = 0;
            try
            {
                StorageFile f = Await(StorageFile.GetFileFromPathAsync(path));
                MusicProperties mp = Await(f.Properties.GetMusicPropertiesAsync());
                title = mp.Title;
                artist = mp.Artist;
                secs = (int)mp.Duration.TotalSeconds;
                SaveCover(f, Path.Combine(Library.MusicDir, id + ".jpg"));
            }
            catch { }

            string name = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
            {
                // "Artist - Title" file names are common
                Match m = Regex.Match(name, @"^\s*(.+?)\s+[-–]\s+(.+?)\s*$");
                if (string.IsNullOrWhiteSpace(title)) title = m.Success ? m.Groups[2].Value : name;
                if (string.IsNullOrWhiteSpace(artist) && m.Success) artist = m.Groups[1].Value;
            }

            var song = new Song
            {
                Id = id, Title = title.Trim(), Artist = (artist ?? "").Trim(), Seconds = secs,
                Added = DateTime.UtcNow.Ticks, Ext = ext, Local = true
            };
            File.Copy(path, song.AudioPath, true);
            return song;
        }

        static void SaveCover(StorageFile f, string dest)
        {
            try
            {
                using (var t = Await(f.GetThumbnailAsync(ThumbnailMode.MusicView, 320)))
                {
                    if (t == null || t.Type != ThumbnailType.Image) return;
                    uint size = (uint)t.Size;
                    if (size == 0 || size > 8 * 1024 * 1024) return;
                    var bytes = new byte[size];
                    using (var rd = new Windows.Storage.Streams.DataReader(t))
                    {
                        Await(rd.LoadAsync(size));
                        rd.ReadBytes(bytes);
                    }
                    using (var ms = new MemoryStream(bytes))
                    using (var img = Image.FromStream(ms))
                        img.Save(dest, ImageFormat.Jpeg);
                }
            }
            catch { try { File.Delete(dest); } catch { } }
        }
    }
}
