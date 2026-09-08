using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Runtime.InteropServices;

// Test-only executable. It never starts WSL or touches application data.
internal static class ProcessTestHelper
{
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int kind);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(IntPtr handle, byte[] buffer, int length, out int written, IntPtr overlapped);

    private static int Main(string[] args)
    {
        if (args[0] == "pipes")
        {
            using (Process child = Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,
                "pipe-holder " + args[1] + " \"" + args[2] + "\"") {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true })) { child.StandardInput.Close(); }
            return 0;
        }
        if (args[0] == "blocked-input" || args[0] == "hang" || args[0] == "pipe-holder")
        {
            File.WriteAllText(args[2], Process.GetCurrentProcess().Id.ToString());
            using (EventWaitHandle ready = EventWaitHandle.OpenExisting(args[1])) ready.Set();
            if (args[0] == "pipe-holder")
            {
                using (EventWaitHandle probe = EventWaitHandle.OpenExisting(args[1] + "-probe")) probe.WaitOne();
                // Console.Out suppresses broken-pipe errors; inspect the actual pipe result.
                int written;
                if (!WriteFile(GetStdHandle(-11), new byte[] { 1 }, 1, out written, IntPtr.Zero) &&
                    (Marshal.GetLastWin32Error() == 109 || Marshal.GetLastWin32Error() == 232))
                {
                    using (EventWaitHandle closed = EventWaitHandle.OpenExisting(args[1] + "-closed")) closed.Set();
                }
            }
            using (EventWaitHandle release = EventWaitHandle.OpenExisting(args[1] + "-release")) release.WaitOne();
            return 0;
        }
        Console.Out.Write(Console.In.ReadToEnd());
        Console.Error.Write("helper diagnostic");
        return args[0] == "error" ? 7 : 0;
    }
}
