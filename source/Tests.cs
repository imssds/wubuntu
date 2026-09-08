using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Wubuntu
{
    internal sealed class FakeBackend : IWslBackend
    {
        public string Distribution { get { return "My Ubuntu"; } }
        public string UserName { get; set; }
        public bool StartedByApp { get; private set; }
        public bool WasAlreadyRunning { get; private set; }
        public bool KeeperAlive { get; set; }
        internal bool Running;
        internal Exception Failure;
        internal Exception PreparationFailure;
        internal Exception StartFailure;
        internal Func<int, Task<SshProbeResult>> Probe;
        internal TaskCompletionSource<bool> ShutdownHold;
        internal TaskCompletionSource<SshProbeResult> SshHold;
        internal SshProbeResult SshResult = new SshProbeResult(true);
        internal int SshCalls;
        internal List<string> Calls = new List<string>();
        public Task PrepareAsync() { if (PreparationFailure != null) throw PreparationFailure; return Task.FromResult(0); }
        public Task StartAsync()
        {
            Calls.Add("start"); WasAlreadyRunning = Running; StartedByApp = !Running; KeeperAlive = Running = true;
            if (StartFailure != null) throw StartFailure;
            return Task.FromResult(0);
        }
        public async Task ShutdownAsync()
        {
            Calls.Add("shutdown");
            if (ShutdownHold != null) await ShutdownHold.Task;
            if (Failure != null) throw Failure;
            KeeperAlive = Running = false;
        }
        public Task<bool> IsUbuntuRunningAsync() { Calls.Add("list"); return Task.FromResult(Running); }
        public Task<SshProbeResult> SshDiagnosticAsync(int timeoutMs)
        {
            SshCalls++;
            if (Probe != null) return Probe(timeoutMs);
            return SshHold == null ? Task.FromResult(SshResult) : SshHold.Task;
        }
        public void Dispose() { }
    }

    internal static class Tests
    {
        private static void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAILED: " + name);
            Console.WriteLine("PASS: " + name);
        }

        [STAThread]
        public static void Main(string[] args)
        {
            try { Run(args); }
            catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
        }

        private static void Run(string[] args)
        {
            string folder = args[0];
            Directory.CreateDirectory(folder);
            if (Array.IndexOf(args, "--live-check") >= 0)
            {
                LiveCheck(folder).GetAwaiter().GetResult();
                return;
            }
            string group = args.Length > 1 ? args[1] : "all";
            if (group != "all" && group != "core" && group != "process" && group != "ui")
                throw new ArgumentException("Unknown test group: " + group);
            if (group == "all" || group == "core")
            {
                RunCore(folder).GetAwaiter().GetResult();
                StartupFailureStopsUbuntu(folder).GetAwaiter().GetResult();
                StartupChecks(folder).GetAwaiter().GetResult();
                ReadinessChecks(folder).GetAwaiter().GetResult();
                StoppedUbuntuChecks(folder).GetAwaiter().GetResult();
                LogChecks(folder);
                LogFormatChecks(folder);
                LoggingChecks(folder).GetAwaiter().GetResult();
                ExternalFailureChecks(folder).GetAwaiter().GetResult();
            }
            // Run async process tests before WinForms installs its synchronization context.
            if (group == "all" || group == "process") ProcessChecks(folder).GetAwaiter().GetResult();
            if (group == "all" || group == "ui")
            {
                UiChecks(folder, Array.IndexOf(args, "--save-preview") >= 0);
                StartupDialogChecks(folder);
                RestartDialogChecks(folder);
            }
        }

        private static async Task ProcessChecks(string folder)
        {
            ProcessStartInfo info = new ProcessStartInfo(Path.Combine(folder, "ProcessTestHelper.exe"), "echo") {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            ProcessResult result = await ProcessRunner.RunAsync(info, 5000, "test input");
            Check(result.Output == "test input" && result.Error == "helper diagnostic",
                "process receives stdin and captures both output streams");
            info.Arguments = "error";
            IOException failure = null;
            try { await ProcessRunner.RunAsync(info, 5000, "failure output"); }
            catch (IOException ex) { failure = ex; }
            Check(failure != null && failure.Message.Contains("Exit code: 7") &&
                failure.Message.Contains("failure output") && failure.Message.Contains("helper diagnostic"),
                "nonzero exit retains exit code, stdout and stderr");
            await ProcessTimeoutCheck(folder, "blocked-input");
            await ProcessTimeoutCheck(folder, "hang");
            await ProcessTimeoutCheck(folder, "pipes");
        }

        private static async Task ProcessTimeoutCheck(string folder, string mode)
        {
            string signal = "Local\\Wubuntu-test-" + Guid.NewGuid().ToString("N");
            string pidFile = Path.Combine(folder, mode + ".pid");
            using (EventWaitHandle ready = new EventWaitHandle(false, EventResetMode.ManualReset, signal))
            using (EventWaitHandle release = new EventWaitHandle(false, EventResetMode.ManualReset, signal + "-release"))
            using (EventWaitHandle probe = new EventWaitHandle(false, EventResetMode.ManualReset, signal + "-probe"))
            using (EventWaitHandle closed = new EventWaitHandle(false, EventResetMode.ManualReset, signal + "-closed"))
            {
                ProcessStartInfo info = new ProcessStartInfo(Path.Combine(folder, "ProcessTestHelper.exe"),
                    mode + " " + signal + " \"" + pidFile + "\"") {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
                };
                Task operation = ProcessRunner.RunAsync(info, 2000, mode == "blocked-input" ? new string('x', 1024 * 1024) : null);
                Process helper = null;
                try
                {
                    Check(await Task.Run(() => ready.WaitOne(5000)), mode + " helper reports ready");
                    helper = Process.GetProcessById(Int32.Parse(File.ReadAllText(pidFile)));
                    Check(await Task.WhenAny(operation, Task.Delay(6000)) == operation, mode + " is bounded");
                    bool timedOut = false;
                    try { await operation; } catch (TimeoutException) { timedOut = true; }
                    Check(timedOut && (mode == "pipes" || helper.HasExited),
                        mode == "pipes" ? "inherited open pipes cannot hold the operation forever" : mode + " times out and stops its process");
                    if (mode == "pipes")
                    {
                        probe.Set();
                        Check(await Task.Run(() => closed.WaitOne(2000)) && !helper.HasExited,
                            "timeout closes the read pipe while its writer is still alive");
                    }
                }
                finally
                {
                    release.Set();
                    if (helper != null)
                    {
                        if (!helper.HasExited) helper.Kill();
                        helper.WaitForExit(1000);
                        helper.Dispose();
                    }
                    ProcessRunner.Observe(operation);
                }
            }
        }

        private static async Task StartupFailureStopsUbuntu(string folder)
        {
            using (SessionLog log = new SessionLog(Path.Combine(folder, "startup-failure.log")))
            {
                FakeBackend backend = new FakeBackend { SshResult = new SshProbeResult(false, diagnostic: "SSH server is not reachable.") };
                long now = 0;
                Controller controller = new Controller(backend, log, () => now, ms => { now += ms; return Task.FromResult(0); });
                Check(!await controller.StartAsync() && !backend.Running,
                    "failed startup stops Ubuntu started by Wubuntu");
            }
        }

        private static async Task RunCore(string folder)
        {
            using (SessionLog log = new SessionLog(Path.Combine(folder, "controller-test.log")))
            {
                FakeBackend backend = new FakeBackend();
                Controller controller = new Controller(backend, log);
                await controller.StartAsync();
                Check(controller.State == RunState.Running, "startup reaches Running");
                backend.ShutdownHold = new TaskCompletionSource<bool>();
                Task<bool> restart = controller.RestartAsync();
                Check(controller.State == RunState.Restarting && controller.Busy, "restart status is immediate and busy");
                Check(!await controller.RestartAsync() && !await controller.ExitAsync(), "duplicate restart and conflicting exit are rejected");
                backend.ShutdownHold.SetResult(true);
                await restart;
                Check(String.Join(",", backend.Calls.ToArray()) == "start,list,shutdown,start,list", "selected distribution stops before restarting");
                Check(controller.State == RunState.Running && !controller.Busy, "restart finishes and unlocks commands");
                Check(File.ReadAllText(log.Path).Contains("Starting") && File.ReadAllText(log.Path).Contains("Restarting"), "restart retains earlier session records");
                backend.KeeperAlive = false;
                int calls = backend.Calls.Count;
                await controller.CheckAsync();
                Check(controller.State == RunState.Error && backend.Calls.Count == calls, "lost keeper becomes Error without starting Ubuntu");
                await controller.CheckAsync();
                Check(backend.Calls.Count == calls, "Error does not trigger an automatic restart or repeated polling");
                backend.Failure = new TimeoutException("simulated WSL shutdown timeout");
                await controller.RestartAsync();
                Check(controller.State == RunState.Error && !controller.Busy && !controller.ExitReady, "timeout remains recoverable and does not pretend to exit");
                backend.Failure = null;
                await controller.RestartAsync();
                Check(controller.State == RunState.Running, "manual restart recovers from Error");
                await controller.ExitAsync();
                Check(controller.ExitReady && controller.State == RunState.Stopping, "successful Exit shuts down and requests application closure");
                Check(File.Exists(log.Path), "Exit retains the log");
            }
        }

        private static async Task StartupChecks(string folder)
        {
            using (SessionLog log = new SessionLog(Path.Combine(folder, "startup-checks.log")))
            {
                long now = 0;
                Func<int, Task> advance = ms => { now += ms; return Task.FromResult(0); };
                FakeBackend existing = new FakeBackend { Running = true, SshResult = new SshProbeResult(false, diagnostic: "SSH server is not reachable.") };
                Controller controller = new Controller(existing, log, () => now, advance);
                Check(!await controller.StartAsync() && existing.Running, "failed startup preserves previously running Ubuntu");
                Check(now == 15000 && controller.LastError == "SSH server is not reachable.", "unavailable SSH exhausts the 15 second startup budget");

                foreach (string message in new[] { "WSL is not installed.", "No WSL distribution is installed." })
                {
                    FakeBackend missing = new FakeBackend { PreparationFailure = new IOException(message) };
                    controller = new Controller(missing, log);
                    Check(!await controller.StartAsync() && !missing.Running && controller.LastError == message,
                        "prerequisite failure prevents Linux startup: " + message);
                }
                FakeBackend unsupported = new FakeBackend { StartFailure = new IOException("The default WSL distribution is not Ubuntu.") };
                controller = new Controller(unsupported, log);
                Check(!await controller.StartAsync() && !unsupported.Running, "unsupported distribution started for inspection is stopped");

                now = 0;
                FakeBackend delayed = new FakeBackend();
                delayed.Probe = ms => Task.FromResult(new SshProbeResult(now >= 14500, diagnostic: "SSH server is not reachable."));
                controller = new Controller(delayed, log, () => now, advance);
                Check(await controller.StartAsync() && now == 14500, "SSH becoming ready within the deadline succeeds");

                now = 0;
                FakeBackend late = new FakeBackend();
                late.Probe = ms => { now = 15001; return Task.FromResult(new SshProbeResult(true)); };
                controller = new Controller(late, log, () => now, advance);
                Check(!await controller.StartAsync() && !late.Running, "SSH response after the deadline cannot produce Running");
            }
        }

        private static void LogFormatChecks(string folder)
        {
            using (SessionLog log = new SessionLog(Path.Combine(folder, "log-format.log")))
            {
                DateTime before = DateTime.Now.AddSeconds(-1);
                log.Write("DETAIL", "First line\r\nSecond line\nThird line");
                string[] lines = File.ReadAllLines(log.Path);
                Check(lines.Length == 3, "multiline diagnostics retain separate records");
                foreach (string line in lines)
                {
                    DateTime timestamp;
                    Check(line.Length > 29 && line.Substring(19, 10) == " [DETAIL] " &&
                        DateTime.TryParseExact(line.Substring(0, 19), "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out timestamp) && timestamp >= before && timestamp <= DateTime.Now,
                        "each diagnostic line has local Windows time to seconds and its level");
                }
            }
        }

        private static async Task LoggingChecks(string folder)
        {
            using (SessionLog log = new SessionLog(Path.Combine(folder, "logging-events.log")))
            {
                long now = 0;
                FakeBackend backend = new FakeBackend();
                backend.Probe = ms => { throw new IOException("SSH server is not reachable.", new IOException("Connection timed out\nExit code: 124")); };
                Controller controller = new Controller(backend, log, () => now, ms => { now += ms; return Task.FromResult(0); });
                await controller.StartAsync();
                string[] lines = File.ReadAllLines(log.Path);
                Check(Array.FindAll(lines, line => line.Contains(" [ERROR] ")).Length == 1,
                    "SSH retries produce one final error");
                Check(Array.Exists(lines, line => line.Contains(" [DETAIL] Exit code: 124")),
                    "final SSH error retains native diagnostic details");
                Check(Array.Exists(lines, line => line.Contains("Stopping Ubuntu because startup failed")),
                    "startup cleanup explains why Ubuntu is being stopped");
            }
            using (SessionLog log = new SessionLog(Path.Combine(folder, "existing-ubuntu.log")))
            {
                long now = 0;
                FakeBackend backend = new FakeBackend { Running = true, SshResult = new SshProbeResult(false, diagnostic: "SSH server is not reachable.") };
                Controller controller = new Controller(backend, log, () => now, ms => { now += ms; return Task.FromResult(0); });
                await controller.StartAsync();
                Check(File.ReadAllText(log.Path).Contains("Leaving previously running Ubuntu unchanged"),
                    "startup failure explains why existing Ubuntu is preserved");
            }
            using (SessionLog log = new SessionLog(Path.Combine(folder, "healthy-events.log")))
            {
                long now = 0;
                Controller controller = new Controller(new FakeBackend(), log, () => now);
                await controller.StartAsync();
                now = 10000;
                await controller.CheckAsync();
                Check(!File.ReadAllText(log.Path).Contains("Health check passed"), "light process checks stay silent");
                now = 60000;
                await controller.CheckAsync();
                Check(File.ReadAllText(log.Path).Contains("Health check passed. Ubuntu and SSH are available"),
                    "successful full health checks are visible");
                await controller.RestartAsync();
                await controller.ExitAsync();
                string text = File.ReadAllText(log.Path);
                Check(text.IndexOf("Restart requested") < text.IndexOf("[STATE] Restarting") &&
                    text.IndexOf("Exit requested") < text.IndexOf("[STATE] Stopping"),
                    "user requests precede their state changes");
            }
        }

        private static void LogChecks(string folder)
        {
            string path = Path.Combine(folder, "bounded-test.log");
            File.WriteAllText(path, "PREVIOUS_SESSION");
            using (SessionLog log = new SessionLog(path))
            {
                Check(new FileInfo(path).Length == 0, "new session clears the previous log");
                log.Write("INFO", "OLDEST_RECORD");
                for (int i = 0; i < 230; i++) log.Write("INFO", i + " " + new string('x', 8100));
                log.Write("INFO", "LATEST_RECORD unicode: \u2713");
                string text = File.ReadAllText(path);
                Check(new FileInfo(path).Length <= SessionLog.Limit, "log never exceeds 1 MiB");
                Check(!text.Contains("OLDEST_RECORD") && text.Contains("LATEST_RECORD"), "oldest records trimmed and latest records kept");
                Check(!text.Contains("\uFFFD"), "trim retains complete UTF-8 records");
                using (FileStream locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    log.Write("INFO", "file lock test");
                    Check(log.LastFailure != null, "locked log records a failure without throwing an application exception");
                }
                log.Write("INFO", "LATEST_RECORD after lock released");
                Check(log.LastFailure == null, "logging resumes after the file lock is released");
            }
            Check(File.ReadAllText(path).Contains("LATEST_RECORD"), "disposing logger preserves saved records");
            string blockedDirectory = Path.Combine(folder, "blocked-logs");
            File.WriteAllText(blockedDirectory, "A file occupies the requested log directory.");
            using (SessionLog unavailable = new SessionLog(Path.Combine(blockedDirectory, "session.log")))
            {
                Controller controller = new Controller(new FakeBackend(), unavailable);
                Check(controller.StartAsync().GetAwaiter().GetResult() && controller.State == RunState.Running,
                    "unwritable logs do not prevent Ubuntu startup or change its state");
            }
        }

        private static async Task ReadinessChecks(string folder)
        {
            using (SessionLog log = new SessionLog(Path.Combine(folder, "readiness-test.log")))
            {
                long now = 0;
                FakeBackend backend = new FakeBackend { SshHold = new TaskCompletionSource<SshProbeResult>() };
                Controller controller = new Controller(backend, log, () => now, ms => { now += ms; return Task.FromResult(0); });
                Task<bool> startup = controller.StartAsync();
                Check(controller.State == RunState.Starting && controller.Busy, "Starting remains until SSH is ready");
                backend.SshHold.SetResult(new SshProbeResult(true));
                await startup;
                backend.SshHold = null;
                Check(controller.State == RunState.Running, "Running requires successful SSH readiness");
                int lists = backend.Calls.FindAll(x => x == "list").Count;
                int sshCalls = backend.SshCalls;
                for (now = 10000; now < 60000; now += 10000) await controller.CheckAsync();
                Check(backend.Calls.FindAll(x => x == "list").Count == lists && backend.SshCalls == sshCalls, "light checks launch no WSL commands or SSH connections before one minute");
                await controller.CheckAsync();
                Check(backend.Calls.FindAll(x => x == "list").Count == lists + 1 && backend.SshCalls == sshCalls + 1, "one minute triggers exactly one WSL and SSH check");
                await controller.CheckAsync();
                Check(backend.SshCalls == sshCalls + 1, "repeated check cannot repeat the full query before the next minute");
                now = 120000;
                backend.SshResult = new SshProbeResult(false, diagnostic: "SSH server is not reachable.");
                await controller.CheckAsync();
                Check(controller.State == RunState.Error && controller.LastError.Contains("SSH"), "SSH loss during a full check changes Running to Error");
                sshCalls = backend.SshCalls;
                string failureLog = File.ReadAllText(log.Path);
                for (now = 130000; now < 180000; now += 10000) await controller.CheckAsync();
                Check(backend.SshCalls == sshCalls, "SSH failure keeps the one minute probe interval");
                backend.Probe = ms => { throw new IOException("SSH server is not reachable."); };
                await controller.CheckAsync();
                Check(backend.SshCalls == sshCalls + 1 && File.ReadAllText(log.Path) == failureLog,
                    "failed SSH is retried without repeating the same log error");
                backend.Probe = null;
                backend.SshResult = new SshProbeResult(true);
                now = 240000;
                await controller.CheckAsync();
                Check(controller.State == RunState.Running && controller.LastError == null &&
                    backend.Calls.FindAll(x => x == "start").Count == 1 && !backend.Calls.Contains("shutdown"),
                    "SSH recovery clears Error without restarting Ubuntu");
                Check(File.ReadAllText(log.Path).Contains("SSH recovered"), "SSH recovery is recorded in the log");
                backend.KeeperAlive = false;
                now += 10000;
                lists = backend.Calls.Count;
                await controller.CheckAsync();
                Check(controller.State == RunState.Error && backend.Calls.Count == lists, "keeper failure is detected without waiting for the minute query");
            }
        }

        private static async Task StoppedUbuntuChecks(string folder)
        {
            foreach (string failure in new[] { "Ubuntu stopped", "keeper stopped", "keeper stopped during probe" })
            using (SessionLog log = new SessionLog(Path.Combine(folder, "stopped-ubuntu.log")))
            {
                long now = 0;
                FakeBackend backend = new FakeBackend();
                Controller controller = new Controller(backend, log, () => now);
                await controller.StartAsync();
                backend.SshResult = new SshProbeResult(false, diagnostic: "SSH server is not reachable.");
                now = 60000;
                await controller.CheckAsync();
                int probes = backend.SshCalls;
                now = failure == "keeper stopped" ? 70000 : 120000;
                if (failure == "Ubuntu stopped") backend.Running = false;
                else if (failure == "keeper stopped") backend.KeeperAlive = false;
                else backend.Probe = ms => { backend.KeeperAlive = false; return Task.FromResult(new SshProbeResult(true)); };
                await controller.CheckAsync();
                Check(controller.State == RunState.Error && controller.LastError.Contains("stopped") &&
                    backend.SshCalls == probes + (failure == "keeper stopped during probe" ? 1 : 0),
                    failure + " prevents SSH recovery");
                int calls = backend.Calls.Count;
                probes = backend.SshCalls;
                backend.Running = backend.KeeperAlive = true;
                backend.Probe = null;
                backend.SshResult = new SshProbeResult(true);
                now += 60000;
                await controller.CheckAsync();
                Check(controller.State == RunState.Error && backend.Calls.Count == calls && backend.SshCalls == probes,
                    failure + " leaves monitoring stopped until manual Restart");
            }
        }

        private static async Task ExternalFailureChecks(string folder)
        {
            foreach (bool existing in new[] { false, true })
            using (SessionLog log = new SessionLog(Path.Combine(folder, "interrupted-start.log")))
            {
                FakeBackend backend = new FakeBackend { Running = existing, SshHold = new TaskCompletionSource<SshProbeResult>() };
                Controller controller = new Controller(backend, log);
                Task<bool> startup = controller.StartAsync();
                controller.ReportFailure(new Exception("external failure"));
                backend.SshHold.SetResult(new SshProbeResult(true));
                Check(!await startup && controller.State == RunState.Error && controller.LastError == "external failure" &&
                    !controller.Busy && backend.Running == existing,
                    "reported startup error survives SSH completion and preserves ownership: " + existing);
            }
            foreach (bool exit in new[] { false, true })
            using (SessionLog log = new SessionLog(Path.Combine(folder, "interrupted-stop.log")))
            {
                FakeBackend backend = new FakeBackend();
                Controller controller = new Controller(backend, log);
                await controller.StartAsync();
                backend.ShutdownHold = new TaskCompletionSource<bool>();
                Task<bool> operation = exit ? controller.ExitAsync() : controller.RestartAsync();
                controller.ReportFailure(new Exception("external stop failure"));
                backend.ShutdownHold.SetResult(true);
                Check(!await operation && controller.State == RunState.Error && !controller.ExitReady && !controller.Busy &&
                    !backend.Running && backend.Calls.FindAll(x => x == "start").Count == 1 &&
                    backend.Calls.FindAll(x => x == "shutdown").Count == 1,
                    "reported stop error prevents restart or successful exit without extra cleanup: " + exit);
                backend.ShutdownHold = null;
                Check(await controller.RestartAsync(), "a new explicit operation can recover after a reported failure");
            }
            using (SessionLog log = new SessionLog(Path.Combine(folder, "interrupted-health.log")))
            {
                long now = 0;
                FakeBackend backend = new FakeBackend();
                Controller controller = new Controller(backend, log, () => now);
                await controller.StartAsync();
                now = 60000;
                backend.SshHold = new TaskCompletionSource<SshProbeResult>();
                Task check = controller.CheckAsync();
                controller.ReportFailure(new Exception("external health failure"));
                backend.SshHold.SetResult(new SshProbeResult(true));
                await check;
                Check(controller.State == RunState.Error && controller.LastError == "external health failure",
                    "pending health success cannot overwrite a reported failure");
                int calls = backend.SshCalls;
                now += 60000;
                await controller.CheckAsync();
                Check(backend.SshCalls == calls, "reported failure keeps health monitoring disabled");
            }
            using (SessionLog log = new SessionLog(Path.Combine(folder, "queued-recovery.log")))
            {
                long now = 0;
                FakeBackend backend = new FakeBackend();
                Controller controller = new Controller(backend, log, () => now);
                await controller.StartAsync();
                now = 60000;
                backend.SshHold = new TaskCompletionSource<SshProbeResult>();
                Task check = controller.CheckAsync();
                controller.ReportFailure(new Exception("queued failure"));
                backend.ShutdownHold = new TaskCompletionSource<bool>();
                bool staleRunning = false;
                controller.Changed += () => { if (controller.State == RunState.Running) staleRunning = true; };
                Task<bool> restart = controller.RestartAsync();
                backend.SshHold.SetResult(new SshProbeResult(true));
                await check;
                Check(!staleRunning, "queued restart does not erase the active health check failure");
                backend.ShutdownHold.SetResult(true);
                Check(await restart, "queued restart can recover after the failed health check ends");
            }
        }

        private static void UiChecks(string folder, bool savePreview)
        {
            Application.EnableVisualStyles();
            using (SessionLog log = new SessionLog(Path.Combine(folder, "ui-test.log")))
            {
                FakeBackend backend = new FakeBackend();
                Controller controller = new Controller(backend, log);
                using (TrayContext context = new TrayContext(controller, log, false))
                {
                    Check(context.IdentityItem.Text == "My Ubuntu", "unknown Linux user leaves only the distribution in the menu");
                    backend.UserName = "kostya";
                    controller.StartAsync().GetAwaiter().GetResult();
                    Check(context.IdentityItem.Text == "My Ubuntu \u00b7 kostya", "menu identifies the distribution and its Linux user");
                    Check(context.StatusItem.Text == "Ready" && context.StatusItem.Tag is System.Drawing.Image, "menu displays inline state artwork and English label");
                    context.Menu.PerformLayout();
                    context.Menu.Show(new System.Drawing.Point(100, 100));
                    Application.DoEvents();
                    if (savePreview)
                    using (System.Drawing.Bitmap preview = new System.Drawing.Bitmap(context.Menu.Width, context.Menu.Height))
                    {
                        context.Menu.DrawToBitmap(preview, new System.Drawing.Rectangle(System.Drawing.Point.Empty, context.Menu.Size));
                        preview.Save(Path.Combine(folder, "menu-preview.png"), System.Drawing.Imaging.ImageFormat.Png);
                    }
                    context.Menu.Hide();
                    backend.UserName = "another-user";
                    backend.ShutdownHold = new TaskCompletionSource<bool>();
                    Task pending = context.RestartAsync();
                    Check(!context.RestartItem.Enabled && !context.ExitItem.Enabled, "actual menu disables both actions during restart");
                    backend.ShutdownHold.SetResult(true);
                    if (!pending.IsCompleted)
                    {
                        using (ApplicationContext pump = new ApplicationContext())
                        {
                            pending.ContinueWith(t => pump.ExitThread(), TaskScheduler.FromCurrentSynchronizationContext());
                            Application.Run(pump);
                        }
                    }
                    pending.GetAwaiter().GetResult();
                    Check(context.IdentityItem.Text == "My Ubuntu \u00b7 another-user", "restart refreshes the displayed Linux user");
                    Check(context.RestartItem.Enabled && context.ExitItem.Enabled, "actual menu re-enables actions after restart");
                }
            }
        }

        private static void StartupDialogChecks(string folder)
        {
            using (SessionLog log = new SessionLog(Path.Combine(folder, "startup-ui.log")))
            {
                long now = 0;
                FakeBackend backend = new FakeBackend { SshResult = new SshProbeResult(false, diagnostic: "SSH server is not reachable.") };
                Controller controller = new Controller(backend, log, () => now, ms => { now += ms; return Task.FromResult(0); });
                using (TrayContext context = new TrayContext(controller, log, false))
                {
                    int dialogs = 0;
                    bool closed = false;
                    context.ThreadExit += delegate { closed = true; };
                    context.StartAsync(message => {
                        dialogs++;
                        Check(message == "SSH server is not reachable." && !backend.Running,
                            "startup dialog shows the English error after cleanup");
                    }).GetAwaiter().GetResult();
                    Check(dialogs == 1 && closed, "startup failure shows one dialog and closes the application");
                }
                backend = new FakeBackend();
                controller = new Controller(backend, log);
                using (TrayContext context = new TrayContext(controller, log, false))
                {
                    int dialogs = 0;
                    bool closed = false;
                    context.ThreadExit += delegate { closed = true; };
                    context.StartAsync(message => dialogs++).GetAwaiter().GetResult();
                    backend.KeeperAlive = false;
                    controller.CheckAsync().GetAwaiter().GetResult();
                    Check(dialogs == 0 && !closed && controller.State == RunState.Error,
                        "runtime failure stays in the tray without a startup dialog");
                }
            }
        }

        private static void RestartDialogChecks(string folder)
        {
            foreach (bool stopFails in new[] { false, true })
            using (SessionLog log = new SessionLog(Path.Combine(folder, "restart-ui.log")))
            {
                long now = 0;
                FakeBackend backend = new FakeBackend();
                Controller controller = new Controller(backend, log, () => now, ms => { now += ms; return Task.FromResult(0); });
                using (TrayContext context = new TrayContext(controller, log, false))
                {
                    context.StartAsync().GetAwaiter().GetResult();
                    backend.SshResult = new SshProbeResult(false, diagnostic: "SSH server is not reachable.");
                    if (stopFails) backend.Failure = new IOException("Ubuntu could not be stopped.");
                    int dialogs = 0;
                    bool closed = false;
                    context.ThreadExit += delegate { closed = true; };
                    context.RestartAsync(message => {
                        dialogs++;
                        Check(!closed && !context.Menu.Enabled && message == (stopFails ?
                            "Ubuntu could not be stopped." : "SSH server is not reachable."),
                            "restart failure shows its error and waits for OK with the menu disabled");
                        Check(stopFails || now == 15000, "restart waits up to 15 seconds for SSH");
                        int probes = backend.SshCalls;
                        backend.SshResult = new SshProbeResult(true);
                        now += 60000;
                        controller.CheckAsync().GetAwaiter().GetResult();
                        Check(backend.SshCalls == probes && controller.State == RunState.Error,
                            "failed restart cannot resume monitoring while awaiting OK");
                    }).GetAwaiter().GetResult();
                    Check(dialogs == 1 && closed && backend.Running &&
                        backend.Calls.FindAll(x => x == "shutdown").Count == 1,
                        "OK closes after one restart error without an extra Ubuntu stop");
                }
            }
        }

        // Deliberately no ShutdownAsync call: safe against the user's existing Linux workloads.
        private static async Task LiveCheck(string folder)
        {
            using (SessionLog log = new SessionLog(Path.Combine(folder, "live-check.log")))
            using (WslBackend backend = new WslBackend(log))
            {
                await backend.PrepareAsync();
                await backend.StartAsync();
                Check(backend.KeeperAlive && await backend.IsUbuntuRunningAsync(), "real Ubuntu handshake and non-starting state query");
                Check(!String.IsNullOrWhiteSpace(backend.UserName), "real Ubuntu handshake identifies the default Linux user");
                SshProbeResult ssh = await backend.SshDiagnosticAsync(3000);
                Check(ssh.Ready, "real SSH server responds inside the selected Ubuntu");
                Check(backend.KeeperAlive, "hidden keeper remains alive without a terminal");
            }
        }
    }
}
