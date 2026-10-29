using System;
using System.Collections.Generic;
using System.IO;
using System.Media;

namespace Notchira
{
    // Short UI sounds. Each one is synthesized in memory, unless %APPDATA%\Notchira\sounds\<name>.wav exists,
    // in which case that file is played instead.
    static class Sounds
    {
        const int Rate = 22050;
        static readonly Dictionary<string, SoundPlayer> cache = new Dictionary<string, SoundPlayer>();

        public static readonly string[] Names = { "toast", "done", "click" };

        public static void Play(string name)
        {
            try
            {
                SoundPlayer p;
                if (!cache.TryGetValue(name, out p))
                {
                    string file = Path.Combine(Settings.Dir, "sounds", name + ".wav");
                    p = File.Exists(file) ? new SoundPlayer(file) : new SoundPlayer(new MemoryStream(Synth(name)));
                    cache[name] = p;
                }
                p.Play();
            }
            catch { }
        }

        // note: frequency in Hz, length in ms, amplitude 0..1
        struct Note
        {
            public float Hz; public int Ms; public float Amp;
            public Note(float hz, int ms, float amp) { Hz = hz; Ms = ms; Amp = amp; }
        }

        public static byte[] Synth(string name)
        {
            switch (name)
            {
                case "toast": return Build(new[] { new Note(1318.5f, 110, 0.30f) });
                case "done": return Build(new[] { new Note(880f, 90, 0.28f), new Note(1318.5f, 140, 0.30f) });
                default: return Build(new[] { new Note(1900f, 22, 0.22f) });
            }
        }

        static byte[] Build(Note[] notes)
        {
            var samples = new List<short>();
            foreach (var n in notes)
            {
                int count = Rate * n.Ms / 1000;
                for (int i = 0; i < count; i++)
                {
                    double t = (double)i / Rate;
                    double attack = Math.Min(1.0, i / (Rate * 0.004));
                    double decay = Math.Exp(-5.5 * i / count);
                    // fundamental plus a soft second harmonic gives a bell-like tone
                    double v = Math.Sin(2 * Math.PI * n.Hz * t) + 0.25 * Math.Sin(4 * Math.PI * n.Hz * t);
                    samples.Add((short)(v / 1.25 * attack * decay * n.Amp * short.MaxValue));
                }
            }
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                int bytes = samples.Count * 2;
                w.Write(new[] { 'R', 'I', 'F', 'F' });
                w.Write(36 + bytes);
                w.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
                w.Write(16);
                w.Write((short)1);
                w.Write((short)1);
                w.Write(Rate);
                w.Write(Rate * 2);
                w.Write((short)2);
                w.Write((short)16);
                w.Write(new[] { 'd', 'a', 't', 'a' });
                w.Write(bytes);
                foreach (short s in samples) w.Write(s);
                w.Flush();
                return ms.ToArray();
            }
        }
    }
}
