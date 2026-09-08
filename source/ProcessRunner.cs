using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Wubuntu
{
    internal sealed class ProcessResult
    {
        internal readonly string Output;
        internal readonly string Error;

        internal ProcessResult(string output, string error) { Output = output; Error = error; }
    }

    // Owns a short-lived process and its redirected streams. Callers supply command details.
    internal static class ProcessRunner
    {
        internal static async Task<ProcessResult> RunAsync(ProcessStartInfo info, int timeoutMs, string input = null)
        {
            using (Process process = new Process { StartInfo = info })
            {
                if (!process.Start()) throw new IOException("Could not start " + info.FileName);
                Task deadline = Task.Delay(timeoutMs);
                Task<ProcessResult> exchange = Task.Run(() => ExchangeAsync(process, input, timeoutMs));
                Exception failure = null;
                try
                {
                    await WithinAsync(exchange, deadline, "Command timed out: " + info.Arguments);
                    return await exchange;
                }
                catch (Exception ex) { failure = ex; }
                Exception cleanup = null;
                try
                {
                    if (!process.HasExited) process.Kill();
                    if (!await Task.Run(() => process.WaitForExit(1000)))
                        cleanup = new IOException("Process did not exit within one second after cleanup.");
                }
                catch (Exception ex) { cleanup = ex; }
                if (cleanup != null)
                    throw new IOException("Command failed and its process could not be cleaned up.", new AggregateException(failure, cleanup));
                throw failure;
            }
        }

        private static async Task<ProcessResult> ExchangeAsync(Process process, string input, int timeoutMs)
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            Observe(output);
            Observe(error);
            if (input != null) await process.StandardInput.WriteAsync(input).ConfigureAwait(false);
            process.StandardInput.Close();
            if (!await Task.Run(() => process.WaitForExit(timeoutMs)).ConfigureAwait(false))
                throw new TimeoutException("Command timed out: " + process.StartInfo.Arguments);
            string stdout = await output.ConfigureAwait(false);
            string stderr = await error.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new IOException("Command: " + process.StartInfo.Arguments + "\nExit code: " + process.ExitCode + "\n" + stdout + "\n" + stderr);
            return new ProcessResult(stdout, stderr);
        }

        // A shared deadline includes every I/O step, including blocked writes and pipe EOF.
        internal static async Task WithinAsync(Task operation, Task deadline, string failure)
        {
            Observe(operation);
            if (await Task.WhenAny(operation, deadline) != operation) throw new TimeoutException(failure);
            await operation;
        }

        internal static void Observe(Task task)
        {
            task.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }
    }
}
