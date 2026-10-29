using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace Notchira
{
    // `Notchira.exe --notify "title" ["body"] [--progress N --id name]` talks to the running instance
    // over a named pipe that only the current Windows user can open.
    //
    // Wire format, one message per connection, tab separated:
    //   T  title  body
    //   P  id  percent  title  body
    static class Notify
    {
        public static string PipeName()
        {
            string dir = Environment.GetEnvironmentVariable("NOTCHIRA_DIR");
            return "Notchira.Notify" + (dir == null ? "" : "." + dir.GetHashCode().ToString("x"));
        }

        static string Clean(string s, int max)
        {
            if (s == null) return "";
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (c < 0x20 || c == 0x7F) { sb.Append(' '); continue; }
                // zero-width and bidi controls could disguise text
                if ((c >= 0x200B && c <= 0x200F) || (c >= 0x202A && c <= 0x202E) || (c >= 0x2066 && c <= 0x2069) || c == 0xFEFF) continue;
                sb.Append(c);
                if (sb.Length >= max) break;
            }
            return sb.ToString().Trim();
        }

        // Client side. Returns the process exit code.
        public static int Send(string[] args)
        {
            string title = null, body = "", id = "";
            int progress = -1;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--progress" && i + 1 < args.Length) { int.TryParse(args[++i], out progress); }
                else if (args[i] == "--id" && i + 1 < args.Length) id = args[++i];
                else if (title == null) title = args[i];
                else body = args[i];
            }
            if (title == null) return 2;
            title = Clean(title, 60);
            body = Clean(body, 120);
            id = Clean(id, 32).Replace('\t', ' ');
            string msg = progress >= 0
                ? "P\t" + id + "\t" + Math.Min(100, progress) + "\t" + title + "\t" + body
                : "T\t" + title + "\t" + body;
            try
            {
                using (var c = new NamedPipeClientStream(".", PipeName(), PipeDirection.Out))
                {
                    c.Connect(800);
                    byte[] data = Encoding.UTF8.GetBytes(msg);
                    c.Write(data, 0, data.Length);
                }
                return 0;
            }
            catch { return 1; }
        }

        public sealed class Message
        {
            public string Title = "", Body = "", Id = "";
            public int Progress = -1;
        }

        // Server side. onMessage runs on the pipe thread.
        public static void StartServer(Action<Message> onMessage)
        {
            var t = new Thread(delegate() { Serve(onMessage); });
            t.IsBackground = true;
            t.Start();
        }

        static void Serve(Action<Message> onMessage)
        {
            var sec = new PipeSecurity();
            sec.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
            while (true)
            {
                try
                {
                    using (var s = new NamedPipeServerStream(PipeName(), PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.None, 4096, 0, sec))
                    {
                        s.WaitForConnection();
                        var buf = new byte[4096];
                        int total = 0, n;
                        while (total < buf.Length && (n = s.Read(buf, total, buf.Length - total)) > 0) total += n;
                        var m = Parse(Encoding.UTF8.GetString(buf, 0, total));
                        if (m != null) onMessage(m);
                    }
                }
                catch { Thread.Sleep(200); }
            }
        }

        static Message Parse(string raw)
        {
            string[] p = raw.Split('\t');
            if (p.Length >= 3 && p[0] == "T")
                return new Message { Title = Clean(p[1], 60), Body = Clean(p[2], 120) };
            if (p.Length >= 5 && p[0] == "P")
            {
                int pr;
                if (!int.TryParse(p[2], out pr)) return null;
                return new Message { Id = Clean(p[1], 32), Progress = Math.Max(0, Math.Min(100, pr)), Title = Clean(p[3], 60), Body = Clean(p[4], 120) };
            }
            return null;
        }
    }
}
