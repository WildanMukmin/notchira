using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace Notchira
{
    sealed class TrackInfo
    {
        public string Title = "", Artist = "", Album = "";
        public bool Playing;
        public TimeSpan Position, Duration;
        public double Rate = 1;
        public DateTime Stamp = DateTime.UtcNow;
        public Bitmap Thumb;

        public TimeSpan Now()
        {
            if (!Playing) return Position;
            var t = Position + TimeSpan.FromSeconds((DateTime.UtcNow - Stamp).TotalSeconds * Rate);
            return Duration > TimeSpan.Zero && t > Duration ? Duration : t;
        }
    }

    // Reads the Windows media session (Spotify, browsers, VLC, ...) and raises Changed on a pool thread.
    sealed class MediaWatcher
    {
        public event Action<TrackInfo> Changed;

        GlobalSystemMediaTransportControlsSessionManager mgr;
        readonly List<GlobalSystemMediaTransportControlsSession> attached = new List<GlobalSystemMediaTransportControlsSession>();
        readonly object gate = new object();
        Bitmap thumb;
        string thumbKey;

        // AsTask() generics are not resolvable with the bundled csc and the winmd references, so wait on Completed.
        static T Await<T>(Windows.Foundation.IAsyncOperation<T> op)
        {
            using (var done = new System.Threading.ManualResetEventSlim(false))
            {
                op.Completed = delegate { done.Set(); };
                if (!done.Wait(5000)) throw new TimeoutException();
                return op.GetResults();
            }
        }

        public void Start()
        {
            Task.Run(() =>
            {
                try
                {
                    mgr = Await(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
                    mgr.SessionsChanged += (s, e) => Sync();
                    mgr.CurrentSessionChanged += (s, e) => Push();
                    Sync();
                }
                catch { }
            });
        }

        void Sync()
        {
            lock (gate)
            {
                foreach (var s in attached)
                {
                    s.MediaPropertiesChanged -= OnEvent;
                    s.PlaybackInfoChanged -= OnEvent;
                    s.TimelinePropertiesChanged -= OnEvent;
                }
                attached.Clear();
                foreach (var s in mgr.GetSessions())
                {
                    s.MediaPropertiesChanged += OnEvent;
                    s.PlaybackInfoChanged += OnEvent;
                    s.TimelinePropertiesChanged += OnEvent;
                    attached.Add(s);
                }
            }
            Push();
        }

        void OnEvent(object sender, object args) { Push(); }

        GlobalSystemMediaTransportControlsSession Pick()
        {
            if (mgr == null) return null;
            lock (gate)
            {
                foreach (var s in attached)
                    if (s.GetPlaybackInfo().PlaybackStatus ==
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) return s;
            }
            return mgr.GetCurrentSession() ?? (attached.Count > 0 ? attached[0] : null);
        }

        void Push()
        {
            TrackInfo t = null;
            try
            {
                var s = Pick();
                if (s != null)
                {
                    var props = Await(s.TryGetMediaPropertiesAsync());
                    var info = s.GetPlaybackInfo();
                    var tl = s.GetTimelineProperties();
                    string key = props.Title + "|" + props.Artist + "|" + props.AlbumTitle;
                    if (key != thumbKey) { thumbKey = key; thumb = LoadThumb(props.Thumbnail); }
                    t = new TrackInfo
                    {
                        Title = props.Title ?? "",
                        Artist = props.Artist ?? "",
                        Album = props.AlbumTitle ?? "",
                        Playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                        Position = tl.Position,
                        Duration = tl.EndTime - tl.StartTime,
                        Rate = info.PlaybackRate.HasValue ? info.PlaybackRate.Value : 1,
                        Stamp = tl.LastUpdatedTime.UtcDateTime,
                        Thumb = thumb
                    };
                    if (t.Title.Length == 0 && t.Artist.Length == 0) t = null;
                }
            }
            catch { }
            var h = Changed;
            if (h != null) h(t);
        }

        static Bitmap LoadThumb(Windows.Storage.Streams.IRandomAccessStreamReference r)
        {
            if (r == null) return null;
            try
            {
                var rs = Await(r.OpenReadAsync());
                uint size = (uint)rs.Size;
                if (size == 0 || size > 8 * 1024 * 1024) return null;
                var bytes = new byte[size];
                using (var rd = new Windows.Storage.Streams.DataReader(rs))
                {
                    Await(rd.LoadAsync(size));
                    rd.ReadBytes(bytes);
                }
                using (var ms = new MemoryStream(bytes))
                using (var img = Image.FromStream(ms)) return CoverCrop(img, 192);
            }
            catch { return null; }
        }

        internal static Bitmap CoverCrop(Image img, int size)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                float s = Math.Max((float)size / img.Width, (float)size / img.Height);
                float w = img.Width * s, h = img.Height * s;
                g.DrawImage(img, (size - w) / 2, (size - h) / 2, w, h);
            }
            return bmp;
        }

        public void Toggle() { var s = Pick(); if (s != null) s.TryTogglePlayPauseAsync(); }
        public void Next() { var s = Pick(); if (s != null) s.TrySkipNextAsync(); }
        public void Prev() { var s = Pick(); if (s != null) s.TrySkipPreviousAsync(); }
        public void Seek(TimeSpan to) { var s = Pick(); if (s != null) s.TryChangePlaybackPositionAsync(to.Ticks); }
    }
}

