using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Notchira
{
    // Microphone and speaker through the classic waveIn/waveOut API: 16-bit mono PCM, no extra libraries.
    // Both exist only while a voice session runs.
    static class WaveApi
    {
        public const int WHDR_DONE = 1, CALLBACK_EVENT = 0x50000, WAVE_MAPPER = -1;
        // WAVEHDR on x64: lpData 0, dwBufferLength 8, dwBytesRecorded 12, dwUser 16, dwFlags 24, dwLoops 28, lpNext 32, reserved 40
        public const int HdrSize = 48, OffLength = 8, OffRecorded = 12, OffFlags = 24;

        [StructLayout(LayoutKind.Sequential)]
        public struct WAVEFORMATEX { public ushort wFormatTag, nChannels; public uint nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }

        public static WAVEFORMATEX Pcm(int rate)
        {
            return new WAVEFORMATEX { wFormatTag = 1, nChannels = 1, nSamplesPerSec = (uint)rate, nAvgBytesPerSec = (uint)(rate * 2), nBlockAlign = 2, wBitsPerSample = 16 };
        }

        [DllImport("winmm.dll")] public static extern int waveInOpen(out IntPtr h, int dev, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, int flags);
        [DllImport("winmm.dll")] public static extern int waveInPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] public static extern int waveInUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] public static extern int waveInAddBuffer(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] public static extern int waveInStart(IntPtr h);
        [DllImport("winmm.dll")] public static extern int waveInReset(IntPtr h);
        [DllImport("winmm.dll")] public static extern int waveInClose(IntPtr h);
        [DllImport("winmm.dll")] public static extern int waveOutOpen(out IntPtr h, int dev, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, int flags);
        [DllImport("winmm.dll")] public static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] public static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] public static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] public static extern int waveOutReset(IntPtr h);
        [DllImport("winmm.dll")] public static extern int waveOutClose(IntPtr h);

        // One header plus its sample memory, both unmanaged so the driver can hold on to them.
        public static IntPtr NewBlock(int bytes)
        {
            IntPtr hdr = Marshal.AllocHGlobal(HdrSize);
            for (int i = 0; i < HdrSize; i += 4) Marshal.WriteInt32(hdr, i, 0);
            Marshal.WriteIntPtr(hdr, 0, Marshal.AllocHGlobal(Math.Max(1, bytes)));
            Marshal.WriteInt32(hdr, OffLength, bytes);
            return hdr;
        }

        public static void FreeBlock(IntPtr hdr)
        {
            Marshal.FreeHGlobal(Marshal.ReadIntPtr(hdr, 0));
            Marshal.FreeHGlobal(hdr);
        }

        public static bool Done(IntPtr hdr) { return (Marshal.ReadInt32(hdr, OffFlags) & WHDR_DONE) != 0; }
    }

    interface IMic : IDisposable { void Start(); }

    // Captures 16 kHz mono in 40 ms pieces and hands each piece to the callback on its own thread.
    sealed class Mic : IMic
    {
        public const int Rate = 16000;
        const int Bytes = Rate * 2 / 25, Count = 8;
        readonly Action<byte[]> sink;
        readonly AutoResetEvent ready = new AutoResetEvent(false);
        readonly IntPtr[] blocks = new IntPtr[Count];
        IntPtr h;
        volatile bool running;
        Thread thread;

        public Mic(Action<byte[]> sink) { this.sink = sink; }

        // Throws with a readable message when no microphone can be opened (none plugged in, or blocked in Windows privacy settings).
        public void Start()
        {
            var fmt = WaveApi.Pcm(Rate);
            int r = WaveApi.waveInOpen(out h, WaveApi.WAVE_MAPPER, ref fmt, ready.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, WaveApi.CALLBACK_EVENT);
            if (r != 0) throw new Exception("Mikrofon tidak bisa dibuka (kode " + r + "). Cek mikrofon dan izin mikrofon untuk aplikasi desktop di Pengaturan Windows.");
            for (int i = 0; i < Count; i++)
            {
                blocks[i] = WaveApi.NewBlock(Bytes);
                WaveApi.waveInPrepareHeader(h, blocks[i], WaveApi.HdrSize);
                WaveApi.waveInAddBuffer(h, blocks[i], WaveApi.HdrSize);
            }
            running = true;
            WaveApi.waveInStart(h);
            thread = new Thread(Loop) { IsBackground = true, Name = "mic" };
            thread.Start();
        }

        void Loop()
        {
            int next = 0;
            while (running)
            {
                ready.WaitOne(200);
                while (running && WaveApi.Done(blocks[next]))
                {
                    IntPtr b = blocks[next];
                    int n = Marshal.ReadInt32(b, WaveApi.OffRecorded);
                    if (n > 0)
                    {
                        var data = new byte[n];
                        Marshal.Copy(Marshal.ReadIntPtr(b, 0), data, 0, n);
                        try { sink(data); } catch { }
                    }
                    Marshal.WriteInt32(b, WaveApi.OffFlags, Marshal.ReadInt32(b, WaveApi.OffFlags) & ~WaveApi.WHDR_DONE);
                    if (running) WaveApi.waveInAddBuffer(h, b, WaveApi.HdrSize);
                    next = (next + 1) % Count;
                }
            }
        }

        public void Dispose()
        {
            if (h == IntPtr.Zero) return;
            running = false;
            ready.Set();
            if (thread != null) thread.Join(1000);
            WaveApi.waveInReset(h);
            foreach (IntPtr b in blocks)
            {
                if (b == IntPtr.Zero) continue;
                WaveApi.waveInUnprepareHeader(h, b, WaveApi.HdrSize);
                WaveApi.FreeBlock(b);
            }
            WaveApi.waveInClose(h);
            h = IntPtr.Zero;
            ready.Dispose();
        }

        // Loudness of a piece, 0..1, for the level meter and the silence timer.
        public static float Level(byte[] pcm)
        {
            if (pcm.Length < 2) return 0;
            double sum = 0;
            int n = pcm.Length / 2;
            for (int i = 0; i < n; i++) { short s = (short)(pcm[2 * i] | (pcm[2 * i + 1] << 8)); sum += (double)s * s; }
            return (float)Math.Min(1.0, Math.Sqrt(sum / n) / 6000.0);
        }
    }

    // Dev hook (NOTCHIRA_VOICE_WAV): plays a 16 kHz mono WAV file into the session in real time, then silence,
    // so a spoken conversation can be tested without anyone talking.
    sealed class FileMic : IMic
    {
        readonly Action<byte[]> sink;
        readonly byte[] pcm;
        volatile bool running;

        public FileMic(string wav, Action<byte[]> sink)
        {
            this.sink = sink;
            byte[] all = System.IO.File.ReadAllBytes(wav);
            int i = 12;
            while (i + 8 <= all.Length)
            {
                int len = BitConverter.ToInt32(all, i + 4);
                if (System.Text.Encoding.ASCII.GetString(all, i, 4) == "data") { pcm = new byte[Math.Min(len, all.Length - i - 8)]; Array.Copy(all, i + 8, pcm, 0, pcm.Length); break; }
                i += 8 + len + (len & 1);
            }
            if (pcm == null) pcm = new byte[0];
        }

        public void Start()
        {
            running = true;
            new Thread(delegate()
            {
                const int chunk = Mic.Rate * 2 / 25;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                for (int sent = 0; running; sent++)
                {
                    var piece = new byte[chunk];
                    int off = sent * chunk;
                    if (off < pcm.Length) Array.Copy(pcm, off, piece, 0, Math.Min(chunk, pcm.Length - off));
                    try { sink(piece); } catch { }
                    int wait = (sent + 1) * 40 - (int)clock.ElapsedMilliseconds;
                    if (wait > 0) Thread.Sleep(wait);
                }
            }) { IsBackground = true }.Start();
        }

        public void Dispose() { running = false; }
    }

    // Plays 24 kHz mono pieces in the order they arrive. Clear() drops everything still queued (when the user interrupts).
    // With NOTCHIRA_VOICE_MUTE=1 (tests) nothing is played; the time it would take is still counted.
    sealed class Speaker : IDisposable
    {
        public const int Rate = 24000;
        public long Received;
        readonly bool mute = Environment.GetEnvironmentVariable("NOTCHIRA_VOICE_MUTE") == "1";
        long muteUntil;
        readonly object gate = new object();
        readonly List<IntPtr> queued = new List<IntPtr>();
        readonly AutoResetEvent done = new AutoResetEvent(false);
        IntPtr h;

        public void Start()
        {
            if (mute) return;
            var fmt = WaveApi.Pcm(Rate);
            int r = WaveApi.waveOutOpen(out h, WaveApi.WAVE_MAPPER, ref fmt, done.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, WaveApi.CALLBACK_EVENT);
            if (r != 0) throw new Exception("Speaker tidak bisa dibuka (kode " + r + ").");
        }

        public void Write(byte[] pcm)
        {
            lock (gate)
            {
                Received += pcm.Length;
                if (mute)
                {
                    long now = Environment.TickCount;
                    muteUntil = Math.Max(muteUntil, now) + pcm.Length * 1000L / (Rate * 2);
                    return;
                }
                if (h == IntPtr.Zero || pcm.Length == 0) return;
                Reap();
                IntPtr b = WaveApi.NewBlock(pcm.Length);
                Marshal.Copy(pcm, 0, Marshal.ReadIntPtr(b, 0), pcm.Length);
                WaveApi.waveOutPrepareHeader(h, b, WaveApi.HdrSize);
                WaveApi.waveOutWrite(h, b, WaveApi.HdrSize);
                queued.Add(b);
            }
        }

        // True while something is still being played.
        public bool Busy
        {
            get { lock (gate) { if (mute) return Environment.TickCount < muteUntil; Reap(); return queued.Count > 0; } }
        }

        public void Clear()
        {
            lock (gate)
            {
                muteUntil = 0;
                if (h == IntPtr.Zero) return;
                WaveApi.waveOutReset(h);
                Reap();
            }
        }

        void Reap()
        {
            for (int i = queued.Count - 1; i >= 0; i--)
            {
                IntPtr b = queued[i];
                if (!WaveApi.Done(b)) continue;
                WaveApi.waveOutUnprepareHeader(h, b, WaveApi.HdrSize);
                WaveApi.FreeBlock(b);
                queued.RemoveAt(i);
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (h == IntPtr.Zero) return;
                WaveApi.waveOutReset(h);
                Reap();
                WaveApi.waveOutClose(h);
                h = IntPtr.Zero;
            }
            done.Dispose();
        }
    }
}
