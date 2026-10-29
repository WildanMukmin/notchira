using System;
using System.Threading;
using System.Windows.Forms;

namespace Notchira
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            string dir = Environment.GetEnvironmentVariable("NOTCHIRA_DIR");
            string name = "Notchira.Single" + (dir == null ? "" : "." + dir.GetHashCode().ToString("x"));
            bool created;
            using (var mutex = new Mutex(true, name, out created))
            {
                if (!created) return;
                Settings.Load();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new Island());
            }
        }
    }
}
