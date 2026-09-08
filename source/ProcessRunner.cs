using System;
using System.Diagnostics;
using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Wubuntu
{
    internal sealed class ProcessResult
    {
        internal readonly string Output;
        internal readonly string Error;

        internal ProcessResult(string output, string error) { Output = output; Error = error; }
    }

    // Framework Process.Dispose does not close redirected streams. Own their handles
    // explicitly so inherited writers cannot leave our reads pending after a timeout.
    internal sealed class ProcessPipes : IDisposable
    {
        internal readonly StreamWriter Input;
        internal readonly StreamReader Output;
        internal readonly StreamReader Error;
        private readonly SafeFileHandle[] handles;
        private bool disposed;

        internal ProcessPipes(Process process)
        {
            Input = process.StandardInput;
            Output = process.StandardOutput;
            Error = process.StandardError;
            // Capture before any writes: retrieving FileStream.SafeFileHandle can flush.
            handles = new[] { ((FileStream)Input.BaseStream).SafeFileHandle,
                ((FileStream)Output.BaseStream).SafeFileHandle, ((FileStream)Error.BaseStream).SafeFileHandle };
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIoEx(IntPtr handle, IntPtr overlapped);

        // Reads may already be queued when cancellation runs. Repeat until all tracked
        // I/O settles, rather than assuming a single CancelIoEx also cancels future reads.
        internal async Task CloseAsync(Task pending, Task deadline)
        {
            ProcessRunner.Observe(pending);
            try
            {
                while (!pending.IsCompleted)
                {
                    CancelPending();
                    Task completed = await Task.WhenAny(pending, deadline, Task.Delay(25)).ConfigureAwait(false);
                    if (completed == deadline && !pending.IsCompleted)
                        throw new IOException("Redirected process I/O did not finish during cleanup.");
                }
            }
            finally { Dispose(); }
        }

        private void CancelPending()
        {
            foreach (SafeFileHandle handle in handles)
            {
                if (handle.IsClosed) continue;
                bool retained = false;
                try
                {
                    handle.DangerousAddRef(ref retained);
                    if (!CancelIoEx(handle.DangerousGetHandle(), IntPtr.Zero))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error != 1168) throw new IOException("Could not cancel redirected process I/O.", new Win32Exception(error));
                    }
                }
                catch (ObjectDisposedException) { } // The writer may close its handle concurrently.
                finally { if (retained) handle.DangerousRelease(); }
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (SafeFileHandle handle in handles) handle.Dispose();
            Output.Dispose();
            Error.Dispose();
            try { Input.Dispose(); }
            catch (IOException) { } // The handle is already closed; do not flush failed input.
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { } // A failed cancellation may still be unwinding.
        }
    }

    // Owns a short-lived process and its redirected streams. Callers supply command details.
    internal static class ProcessRunner
    {
        internal static async Task<ProcessResult> RunAsync(ProcessStartInfo info, int timeoutMs, string input = null)
        {
            if (timeoutMs <= 0) throw new ArgumentOutOfRangeException("timeoutMs");
            using (Process process = new Process { StartInfo = info })
            {
                if (!process.Start()) throw new IOException("Could not start " + info.FileName);
                Task deadline = Task.Delay(timeoutMs);
                using (ProcessPipes pipes = new ProcessPipes(process))
                {
                    Task<string> output = pipes.Output.ReadToEndAsync();
                    Task<string> error = pipes.Error.ReadToEndAsync();
                    Observe(output);
                    Observe(error);
                    Task<ProcessResult> exchange = Task.Run(() => ExchangeAsync(process, pipes.Input, input, output, error, timeoutMs));
                    Exception failure = null;
                    try
                    {
                        await WithinAsync(exchange, deadline, "Command timed out: " + info.Arguments);
                        return await exchange;
                    }
                    catch (Exception ex) { failure = ex; }
                    Task cleanupDeadline = Task.Delay(1000);
                    Exception cleanup = null;
                    try
                    {
                        if (!process.HasExited) process.Kill();
                        if (!await Task.Run(() => process.WaitForExit(1000)))
                            cleanup = new IOException("Process did not exit within one second after cleanup.");
                    }
                    catch (Exception ex) { cleanup = ex; }
                    Task settled = Task.WhenAll(exchange, output, error);
                    try { await pipes.CloseAsync(settled, cleanupDeadline); }
                    catch (Exception ex) { cleanup = cleanup == null ? ex : new AggregateException(cleanup, ex); }
                    if (cleanup != null)
                        throw new IOException("Command failed and its process could not be cleaned up.", new AggregateException(failure, cleanup));
                    throw failure;
                }
            }
        }

        private static async Task<ProcessResult> ExchangeAsync(Process process, StreamWriter writer, string input,
            Task<string> output, Task<string> error, int timeoutMs)
        {
            if (input != null) await writer.WriteAsync(input).ConfigureAwait(false);
            writer.Close();
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
