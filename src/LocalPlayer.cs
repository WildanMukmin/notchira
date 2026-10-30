using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Notchira
{
    // The built-in player for downloaded songs. The WinRT MediaPlayer is created when a song starts and
    // disposed when the queue ends, so it costs nothing while idle. Its own media-control entry is switched
    // off; the island reads this class directly instead of going through the Windows media session.
    sealed class LocalPlayer
    {
        public static readonly LocalPlayer I = new LocalPlayer();

        public event Action Changed;

        MediaPlayer mp;
        List<Song> queue = new List<Song>();
        List<int> order = new List<int>();
        int pos = -1;
        Bitmap thumb;
        string thumbId;
        readonly Random rng = new Random();

        public bool Shuffle { get; private set; }
        public int Repeat { get; private set; } // 0 off, 1 queue, 2 one

        // Volume of this player alone (0 to 1). It does not touch the system volume, so a call in Meet or Zoom
        // stays as loud as it was while the music gets quieter.
        public double Volume
        {
            get { int v; return int.TryParse(Settings.Get("music.volume", "100"), out v) ? Math.Max(0, Math.Min(100, v)) / 100.0 : 1.0; }
        }

        public int AdjustVolume(int deltaPercent)
        {
            int v = (int)Math.Round(Math.Max(0, Math.Min(1, Volume + deltaPercent / 100.0)) * 100);
            Settings.Set("music.volume", v.ToString());
            if (mp != null) mp.Volume = v / 100.0;
            return v;
        }

        public bool Active { get { return mp != null && Current != null; } }

        public Song Current
        {
            get { return pos >= 0 && pos < order.Count ? queue[order[pos]] : null; }
        }

        void Raise() { var h = Changed; if (h != null) h(); }

        // ---- queue ------------------------------------------------------------------------------------------

        public void PlayList(List<Song> songs, int start)
        {
            if (songs == null || songs.Count == 0) return;
            queue = new List<Song>(songs);
            Rebuild(start);
            Open();
        }

        void Rebuild(int startIndex)
        {
            order.Clear();
            for (int i = 0; i < queue.Count; i++) order.Add(i);
            if (Shuffle)
            {
                for (int i = order.Count - 1; i > 0; i--)
                {
                    int j = rng.Next(i + 1);
                    int t = order[i]; order[i] = order[j]; order[j] = t;
                }
                int at = order.IndexOf(startIndex);
                if (at > 0) { int t = order[0]; order[0] = order[at]; order[at] = t; }
                pos = 0;
            }
            else pos = Math.Max(0, Math.Min(order.Count - 1, startIndex));
        }

        public void SetShuffle(bool on)
        {
            if (Shuffle == on) return;
            Song cur = Current;
            Shuffle = on;
            if (cur != null) Rebuild(queue.IndexOf(cur));
            Raise();
        }

        public void CycleRepeat() { Repeat = (Repeat + 1) % 3; Raise(); }

        // ---- playback ---------------------------------------------------------------------------------------

        void Open()
        {
            Song s = Current;
            if (s == null || !File.Exists(s.AudioPath)) { Stop(); return; }
            if (mp == null)
            {
                mp = new MediaPlayer();
                mp.CommandManager.IsEnabled = false;
                mp.Volume = Volume;
                mp.MediaEnded += delegate { OnEnded(); };
                mp.MediaFailed += delegate { OnEnded(); };
                mp.PlaybackSession.PlaybackStateChanged += delegate { Raise(); };
            }
            if (thumbId != s.Id) LoadThumb(s);
            mp.Source = MediaSource.CreateFromUri(new Uri(s.AudioPath));
            mp.Play();
            Raise();
        }

        void LoadThumb(Song s)
        {
            thumbId = s.Id;
            thumb = null;
            try
            {
                if (File.Exists(s.ThumbPath))
                    using (var img = Image.FromFile(s.ThumbPath)) thumb = MediaWatcher.CoverCrop(img, 192);
            }
            catch { }
        }

        void OnEnded()
        {
            if (Repeat == 2) { Open(); return; }
            Next(true);
        }

        public void Next() { Next(false); }

        void Next(bool auto)
        {
            if (order.Count == 0) return;
            if (pos + 1 < order.Count) pos++;
            else if (Repeat == 1 || !auto) pos = 0;
            else { Stop(); return; }
            Open();
        }

        public void Prev()
        {
            if (order.Count == 0) return;
            // like most players: restart the song first, go back only near its start
            if (mp != null && mp.PlaybackSession.Position.TotalSeconds > 3) { Seek(TimeSpan.Zero); return; }
            pos = pos > 0 ? pos - 1 : order.Count - 1;
            Open();
        }

        public void Toggle()
        {
            if (mp == null) return;
            if (mp.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) mp.Pause(); else mp.Play();
        }

        public void Seek(TimeSpan to)
        {
            if (mp == null) return;
            mp.PlaybackSession.Position = to;
            Raise();
        }

        public void Stop()
        {
            if (mp != null)
            {
                try { mp.Pause(); mp.Source = null; mp.Dispose(); } catch { }
                mp = null;
            }
            queue.Clear();
            order.Clear();
            pos = -1;
            thumb = null;
            thumbId = null;
            Raise();
        }

        public TrackInfo Snapshot()
        {
            Song s = Current;
            if (s == null || mp == null) return null;
            var ses = mp.PlaybackSession;
            TimeSpan dur = ses.NaturalDuration;
            if (dur <= TimeSpan.Zero) dur = TimeSpan.FromSeconds(s.Seconds);
            var st = ses.PlaybackState;
            return new TrackInfo
            {
                Title = s.Title,
                Artist = s.Artist,
                Playing = st == MediaPlaybackState.Playing || st == MediaPlaybackState.Opening || st == MediaPlaybackState.Buffering,
                Position = ses.Position,
                Duration = dur,
                Stamp = DateTime.UtcNow,
                Thumb = thumb,
                SongId = s.Id
            };
        }
    }
}
