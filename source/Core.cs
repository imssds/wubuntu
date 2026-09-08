using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Wubuntu
{
    internal enum RunState { Starting, Running, Restarting, Stopping, Error }

    // One session file, retained on exit. Trim complete oldest lines without rotation files.
    internal sealed class SessionLog : IDisposable
    {
        internal const int Limit = 1024 * 1024;
        private readonly object sync = new object();
        private readonly UTF8Encoding utf8 = new UTF8Encoding(false);
        internal readonly string Path;
        internal string LastFailure { get; private set; }

        internal SessionLog(string path)
        {
            Path = path;
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                using (FileStream file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite)) { }
            }
            catch (IOException ex) { LastFailure = ex.Message; }
            catch (UnauthorizedAccessException ex) { LastFailure = ex.Message; }
        }

        internal void Write(string level, string message)
        {
            try { WriteRecord(level, message); LastFailure = null; }
            catch (IOException ex) { LastFailure = ex.Message; }
            catch (UnauthorizedAccessException ex) { LastFailure = ex.Message; }
        }

        // Record a failure once at the operation boundary, followed by its diagnostic context.
        internal void Error(Exception exception, string message = null)
        {
            Write("ERROR", message ?? exception.Message);
            Write("DETAIL", (exception.InnerException ?? exception).ToString());
        }

        private void WriteRecord(string level, string message)
        {
            lock (sync)
            using (FileStream file = new FileStream(Path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                // Keep each record bounded, including diagnostic output from a failed command.
                message = (message ?? "").Replace("\0", "").Replace("\r\n", "\n").Replace('\r', '\n');
                if (message.Length > 8192) message = message.Substring(0, 8192) + " [truncated]";
                string prefix = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + " [" + level + "] ";
                StringBuilder lines = new StringBuilder();
                foreach (string line in message.Split('\n'))
                    if (!String.IsNullOrWhiteSpace(line)) lines.Append(prefix).Append(line).Append(Environment.NewLine);
                byte[] record = utf8.GetBytes(lines.ToString());
                if (file.Length + record.Length > Limit)
                {
                    byte[] tail = new byte[Limit / 2];
                    file.Position = Math.Max(0, file.Length - tail.Length);
                    int count = file.Read(tail, 0, tail.Length);
                    int start = 0;
                    while (start < count && tail[start] != (byte)'\n') start++;
                    if (start < count) start++;
                    file.Position = 0;
                    file.SetLength(0);
                    file.Write(tail, start, count - start);
                }
                file.Position = file.Length;
                file.Write(record, 0, record.Length);
                file.Flush(true);
            }
        }

        public void Dispose() { }
    }

    internal sealed class SshProbeResult
    {
        internal readonly bool Ready;
        internal readonly string Endpoint;
        internal readonly string Diagnostic;

        internal SshProbeResult(bool ready, string endpoint = null, string diagnostic = null)
        {
            Ready = ready;
            Endpoint = endpoint;
            Diagnostic = diagnostic;
        }
    }

    internal interface IWslBackend : IDisposable
    {
        string Distribution { get; }
        bool StartedByApp { get; }
        bool WasAlreadyRunning { get; }
        bool KeeperAlive { get; }
        Task PrepareAsync();
        Task StartAsync();
        Task ShutdownAsync();
        Task<bool> IsUbuntuRunningAsync();
        Task<SshProbeResult> SshDiagnosticAsync(int timeoutMs);
    }

    // Call from the WinForms UI thread; awaits retain that context. The gate serializes
    // async operations with health checks, not arbitrary callers on multiple threads.
    internal sealed class Controller
    {
        private readonly IWslBackend backend;
        private readonly SessionLog log;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private readonly Func<long> milliseconds;
        private readonly Func<int, Task> delay;
        private long nextFullCheckAt;
        private bool monitorHealth;
        private Exception reportedFailure;
        private int failureVersion;
        internal RunState State { get; private set; }
        internal bool Busy { get; private set; }
        internal bool ExitReady { get; private set; }
        internal string LastError { get; private set; }
        internal string Distribution { get { return backend.Distribution; } }
        internal event Action Changed;

        internal Controller(IWslBackend backend, SessionLog log, Func<long> milliseconds = null, Func<int, Task> delay = null)
        {
            this.backend = backend;
            this.log = log;
            this.milliseconds = milliseconds ?? (() => (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency));
            this.delay = delay ?? (ms => Task.Delay(ms));
            State = RunState.Starting;
        }

        private void Publish() { if (Changed != null) Changed(); }
        private void SetState(RunState value)
        {
            State = value;
            log.Write("STATE", value.ToString());
            Publish();
        }

        internal void ReportFailure(Exception ex)
        {
            reportedFailure = ex;
            failureVersion++;
            monitorHealth = false;
            LastError = ex.Message;
            log.Error(ex);
            SetState(RunState.Error);
        }

        private void RequireNoReportedFailure()
        {
            if (reportedFailure != null) throw new OperationCanceledException("Operation interrupted by a reported failure.", reportedFailure);
        }

        internal Task<bool> StartAsync() { return OperateAsync(RunState.Starting); }
        internal Task<bool> RestartAsync() { return OperateAsync(RunState.Restarting); }
        internal Task<bool> ExitAsync() { return OperateAsync(RunState.Stopping); }

        private async Task<bool> OperateAsync(RunState operation)
        {
            if (Busy || ExitReady) return false;
            int requestedVersion = failureVersion;
            Busy = true;
            Publish();
            await gate.WaitAsync();
            try
            {
                // Leave a running health check's failure intact until it releases the gate.
                // A newer failure while this request waited must still interrupt the request.
                if (failureVersion == requestedVersion) reportedFailure = null;
                monitorHealth = false;
                try
                {
                    RequireNoReportedFailure();
                    LastError = null;
                    if (operation == RunState.Restarting) log.Write("INFO", "Restart requested");
                    if (operation == RunState.Stopping) log.Write("INFO", "Exit requested");
                    SetState(operation);
                    RequireNoReportedFailure();
                    if (operation == RunState.Starting) await backend.PrepareAsync();
                    RequireNoReportedFailure();
                    if (operation != RunState.Starting) await backend.ShutdownAsync();
                    RequireNoReportedFailure();
                    if (operation == RunState.Stopping)
                    {
                        ExitReady = true;
                        return true;
                    }
                    await backend.StartAsync();
                    RequireNoReportedFailure();
                    bool running = backend.KeeperAlive && await backend.IsUbuntuRunningAsync();
                    RequireNoReportedFailure();
                    if (!running)
                        throw new IOException("Ubuntu did not remain running after startup.");
                    await WaitForSshAsync();
                    RequireNoReportedFailure();
                    if (!backend.KeeperAlive) throw new IOException("Ubuntu's keep-alive connection closed during startup.");
                    nextFullCheckAt = milliseconds() + 60000;
                    monitorHealth = true;
                    SetState(RunState.Running);
                    return true;
                }
                catch (Exception ex)
                {
                    if (reportedFailure == null)
                    {
                        LastError = ex.Message;
                        log.Error(ex);
                    }
                }
                if (operation == RunState.Starting && backend.StartedByApp)
                {
                    log.Write("INFO", "Stopping Ubuntu because startup failed");
                    try { await backend.ShutdownAsync(); }
                    catch (Exception cleanup)
                    {
                        log.Error(cleanup, "Could not stop Ubuntu after startup failed");
                        LastError += " Ubuntu could not be stopped.";
                    }
                }
                else if (operation == RunState.Starting)
                    log.Write("INFO", backend.WasAlreadyRunning ? "Leaving previously running Ubuntu unchanged" : "Ubuntu was not started by Wubuntu");
                SetState(RunState.Error);
                return false;
            }
            finally
            {
                Busy = false;
                gate.Release();
                Publish();
            }
        }

        internal async Task CheckAsync()
        {
            if (!monitorHealth || Busy || !gate.Wait(0)) return;
            try
            {
                RequireKeeper();
                // The ten-second tick checks our existing process without launching anything.
                // SSH failures keep the minute checks; an unconfirmed/stopped Ubuntu ends them.
                if (milliseconds() < nextFullCheckAt) return;
                nextFullCheckAt = milliseconds() + 60000;
                monitorHealth = false;
                bool running = await backend.IsUbuntuRunningAsync();
                RequireNoReportedFailure();
                if (!running)
                    throw new IOException("Ubuntu stopped. Use Restart WSL to recover.");
                RequireKeeper();
                monitorHealth = true;
                SshProbeResult diagnostic = await backend.SshDiagnosticAsync(3000);
                RequireNoReportedFailure();
                RequireSsh(diagnostic);
                RequireKeeper();
                if (State == RunState.Error)
                {
                    LastError = null;
                    log.Write("INFO", "SSH recovered. Ubuntu and SSH are available");
                    SetState(RunState.Running);
                }
                else log.Write("INFO", "Health check passed. Ubuntu and SSH are available");
            }
            catch (Exception ex)
            {
                if (reportedFailure != null) return;
                if (State != RunState.Error || LastError != ex.Message)
                {
                    LastError = ex.Message;
                    log.Error(ex);
                    if (State != RunState.Error) SetState(RunState.Error);
                    else Publish();
                }
            }
            finally { gate.Release(); }
        }

        private void RequireKeeper()
        {
            if (backend.KeeperAlive) return;
            monitorHealth = false;
            throw new IOException("Ubuntu or the keep-alive connection stopped. Use Restart WSL to recover.");
        }

        private async Task WaitForSshAsync()
        {
            log.Write("INFO", "Waiting for SSH for up to 15 seconds");
            long deadline = milliseconds() + 15000;
            SshProbeResult diagnostic = new SshProbeResult(false, diagnostic: "SSH server is not reachable.");
            Exception lastFailure = null;
            while (milliseconds() < deadline)
            {
                if (!backend.KeeperAlive) throw new IOException("Ubuntu's keep-alive connection closed during startup.");
                try
                {
                    diagnostic = await backend.SshDiagnosticAsync((int)Math.Min(3000, deadline - milliseconds()));
                    lastFailure = null;
                }
                catch (IOException ex)
                {
                    diagnostic = new SshProbeResult(false, diagnostic: ex.Message);
                    lastFailure = ex;
                }
                RequireNoReportedFailure();
                if (diagnostic != null && diagnostic.Ready)
                {
                    if (milliseconds() > deadline) throw new IOException("SSH server is not reachable.");
                    log.Write("INFO", String.IsNullOrEmpty(diagnostic.Endpoint) ? "SSH is available" :
                        "SSH is available at " + diagnostic.Endpoint);
                    return;
                }
                int remaining = (int)(deadline - milliseconds());
                if (remaining > 0) await delay(Math.Min(500, remaining));
                RequireNoReportedFailure();
            }
            RequireSsh(diagnostic, lastFailure);
        }

        private static void RequireSsh(SshProbeResult result, Exception failure = null)
        {
            if (result == null || !result.Ready)
                throw new IOException(result == null || String.IsNullOrWhiteSpace(result.Diagnostic) ?
                    "SSH server is not reachable." : result.Diagnostic, failure);
        }
    }
}
