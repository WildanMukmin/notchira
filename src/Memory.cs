using System;
using System.Runtime.InteropServices;

namespace Notchira
{
    // Gives memory back to Windows while the island is quiet. Task Manager reports the private working set,
    // which is what trimming reduces; pages come back on demand when the island wakes up.
    static class Memory
    {
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);

        public static void Trim()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
        }
    }
}
