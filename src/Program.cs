using System;
using System.Threading;
using System.Windows.Forms;

namespace Notchira
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--notify") return Notify.Send(args);

            string dir = Environment.GetEnvironmentVariable("NOTCHIRA_DIR");
            string name = "Notchira.Single" + (dir == null ? "" : "." + dir.GetHashCode().ToString("x"));
            bool created;
            using (var mutex = new Mutex(true, name, out created))
            {
                if (!created) return 0;
                // built without a target framework, so .NET would otherwise start without TLS 1.2 (Deepgram requires it)
                System.Net.ServicePointManager.SecurityProtocol |= (System.Net.SecurityProtocolType)3072;
                Settings.Load();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new Island());
            }
            return 0;
        }
    }
}
