using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

// Test-only executable. It never starts WSL or touches application data.
internal static class ProcessTestHelper
{
    private static int Main(string[] args)
    {
        if (args[0] == "pipes")
        {
            using (Process child = Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,
                "hang " + args[1] + " \"" + args[2] + "\"") { UseShellExecute = false, CreateNoWindow = true })) { }
            return 0;
        }
        if (args[0] == "blocked-input" || args[0] == "hang")
        {
            File.WriteAllText(args[2], Process.GetCurrentProcess().Id.ToString());
            using (EventWaitHandle ready = EventWaitHandle.OpenExisting(args[1])) ready.Set();
            using (EventWaitHandle release = EventWaitHandle.OpenExisting(args[1] + "-release")) release.WaitOne();
            return 0;
        }
        Console.Out.Write(Console.In.ReadToEnd());
        Console.Error.Write("helper diagnostic");
        return args[0] == "error" ? 7 : 0;
    }
}
