using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Wubuntu
{
    internal sealed class WslBackend : IWslBackend
    {
        private readonly string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
        private readonly SessionLog log;
        private Process keeper;
        private ProcessPipes keeperPipes;
        private Task<string> keeperErrors;
        private Task<string> keeperReady;
        public string Distribution { get; private set; }
        public bool StartedByApp { get; private set; }
        public bool WasAlreadyRunning { get; private set; }
        public bool KeeperAlive { get { return keeper != null && !keeper.HasExited; } }

        internal WslBackend(SessionLog log) { this.log = log; }

        private ProcessStartInfo Info(string arguments, Encoding encoding)
        {
            return new ProcessStartInfo(executable, arguments) {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = encoding, StandardErrorEncoding = encoding,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
            };
        }

        // WSL commands use UTF-16; Linux commands use UTF-8. Scripts go over stdin.
        private async Task<string> CommandAsync(string arguments, int timeoutMs, string failure, string script = null, Action<string> captureDiagnostics = null)
        {
            try
            {
                ProcessResult result = await ProcessRunner.RunAsync(
                    Info(arguments, script == null ? Encoding.Unicode : Encoding.UTF8), timeoutMs,
                    script == null ? null : script.Replace("\r", ""));
                if (!String.IsNullOrWhiteSpace(result.Error))
                {
                    if (captureDiagnostics != null) captureDiagnostics(result.Error);
                    else log.Write("DETAIL", result.Error);
                }
                return result.Output;
            }
            catch (Exception ex)
            {
                // Native diagnostics may be localized; the UI gets only our English summary.
                throw new IOException(failure, ex);
            }
        }

        public async Task PrepareAsync()
        {
            if (!File.Exists(executable)) throw new IOException("WSL is not installed.");
            string installed = await CommandAsync("--list --quiet", 10000, "WSL is not installed or is unavailable.");
            log.Write("INFO", "WSL is available");
            if (String.IsNullOrWhiteSpace(installed.Replace("\0", "").Trim('\uFEFF')))
                throw new IOException("No WSL distribution is installed.");
            // Reading registration does not boot Linux or alter the startup ownership snapshot.
            try
            {
                using (RegistryKey registrations = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss"))
                {
                    string id = registrations == null ? null : registrations.GetValue("DefaultDistribution") as string;
                    using (RegistryKey selected = id == null ? null : registrations.OpenSubKey(id))
                        Distribution = selected == null ? null : selected.GetValue("DistributionName") as string;
                }
            }
            catch (Exception ex) { throw new IOException("The default WSL distribution could not be determined.", ex); }
            if (String.IsNullOrWhiteSpace(Distribution) || !ContainsDistribution(installed))
                throw new IOException("No default WSL distribution is available.");
            log.Write("INFO", "Selected distribution: " + Distribution);
        }

        private string Target
        {
            get
            {
                if (String.IsNullOrEmpty(Distribution)) throw new IOException("No WSL distribution has been selected.");
                return "--distribution " + Quote(Distribution);
            }
        }

        // Windows argument quoting also handles trailing backslashes and embedded quotes.
        private static string Quote(string value)
        {
            if (value.IndexOfAny(new[] { ' ', '\t', '\n', '\r', '"' }) < 0) return value;
            StringBuilder result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                result.Append(c);
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }

        public async Task StartAsync()
        {
            await ReleaseKeeperAsync();
            StartedByApp = false;
            WasAlreadyRunning = await IsUbuntuRunningAsync();
            log.Write("INFO", WasAlreadyRunning ? "Ubuntu is already running" : "Ubuntu is stopped");
            log.Write("INFO", WasAlreadyRunning ? "Keeping Ubuntu running in the background" : "Starting Ubuntu");
            // Validate without sourcing os-release. The default Linux user owns the keeper.
            // read blocks on our stdin and exits when the app closes its pipe.
            string script = "if grep -Eq \"^ID=(ubuntu|'ubuntu'|\\\"ubuntu\\\")$\" /etc/os-release; then printf 'WUBUNTU_READY\\n'; read -r line; else printf 'WUBUNTU_UNSUPPORTED\\n'; fi\n";
            keeper = new Process { StartInfo = Info(Target + " --exec /bin/sh -s", Encoding.UTF8) };
            Exception startupError = null;
            string startupFailure = "Ubuntu could not be started.";
            try
            {
                if (!keeper.Start()) throw new IOException("Could not start the Ubuntu keep-alive process.");
                Task deadline = Task.Delay(30000);
                StartedByApp = !WasAlreadyRunning;
                keeperPipes = new ProcessPipes(keeper);
                keeperErrors = keeperPipes.Error.ReadToEndAsync();
                ProcessRunner.Observe(keeperErrors);
                ProcessPipes startingPipes = keeperPipes;
                keeperReady = Task.Run(() => KeeperHandshakeAsync(startingPipes, script));
                try { await ProcessRunner.WithinAsync(keeperReady, deadline, "Ubuntu did not become ready within 30 seconds."); }
                catch (TimeoutException ex) { startupFailure = ex.Message; throw; }
                string marker = await keeperReady;
                if (marker == "WUBUNTU_UNSUPPORTED")
                {
                    startupFailure = "The default WSL distribution is not Ubuntu.";
                    throw new IOException(startupFailure);
                }
                if (marker != "WUBUNTU_READY") throw new IOException("Unexpected startup response: " + (marker ?? "connection closed"));
                log.Write("INFO", WasAlreadyRunning ? "Ubuntu is ready" : "Ubuntu started");
            }
            catch (Exception ex)
            {
                startupError = ex;
            }
            if (startupError != null)
            {
                try { await ReleaseKeeperAsync(true); }
                catch (Exception cleanup) { startupError = new AggregateException(startupError, cleanup); }
                throw new IOException(startupFailure, startupError);
            }
        }

        private static async Task<string> KeeperHandshakeAsync(ProcessPipes pipes, string script)
        {
            await pipes.Input.WriteAsync(script).ConfigureAwait(false);
            await pipes.Input.FlushAsync().ConfigureAwait(false);
            return await pipes.Output.ReadLineAsync().ConfigureAwait(false);
        }

        private bool ContainsDistribution(string output)
        {
            foreach (string line in output.Replace("\0", "").Split('\n'))
                if (String.Equals(line.Trim('\r', ' ', '\t', '\uFEFF'), Distribution, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public async Task<bool> IsUbuntuRunningAsync()
        {
            return ContainsDistribution(await CommandAsync("--list --running --quiet", 5000, "The WSL running state could not be checked."));
        }

        public async Task ShutdownAsync()
        {
            log.Write("INFO", "Stopping Ubuntu");
            await ReleaseKeeperAsync();
            if (await IsUbuntuRunningAsync())
                await CommandAsync("--terminate " + Quote(Distribution), 30000, "Ubuntu could not be stopped.");
            if (await IsUbuntuRunningAsync()) throw new IOException("Ubuntu is still running after the stop request.");
            StartedByApp = false;
            log.Write("INFO", "Ubuntu stopped");
        }

        // Actual sshd listeners plus Ubuntu's systemd ssh.socket activation. Keyscan checks
        // SSH inside this distribution without credentials or a Windows network address.
        private const string SshProbe = @"
export LC_ALL=C PATH=/usr/sbin:/usr/bin:/sbin:/bin
if ! command -v sshd >/dev/null 2>&1; then
    printf 'SSH server is not installed.\n'; exit 0
fi
if ! command -v ss >/dev/null 2>&1 || ! command -v ssh-keyscan >/dev/null 2>&1; then
    printf 'SSH readiness check requires ss and ssh-keyscan.\n'; exit 0
fi
{
    ss -H -ltnp | awk '/""sshd""/ {print $4}'
    if command -v systemctl >/dev/null 2>&1; then
        systemctl show ssh.socket --property=Listen --value 2>/dev/null |
            awk '{for (i=2; i<=NF; i++) if ($i == ""(Stream)"") print $(i-1)}'
    fi
} | sort -u | while IFS= read -r endpoint; do
    port=${endpoint##*:}
    host=${endpoint%:*}
    host=${host#\[}; host=${host%\]}
    case ""$host"" in '0.0.0.0') host=127.0.0.1 ;; '*'|'::') host=::1 ;; esac
    if ssh-keyscan -T 1 -p ""$port"" ""$host"" 2>/dev/null | grep -q '^[^#].* '; then
        printf 'SSH-ready at %s:%s\n' ""$host"" ""$port""
        break
    fi
done
";

        public async Task<SshProbeResult> SshDiagnosticAsync(int timeoutMs)
        {
            if (!KeeperAlive) return new SshProbeResult(false, diagnostic: "Ubuntu's keep-alive connection is not running.");
            // Bound Linux children too, even if the Windows WSL client gets killed.
            string seconds = (Math.Max(100, timeoutMs - 250) / 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            string details = null;
            string result = await CommandAsync(Target + " --user root --exec /usr/bin/timeout -k 0.1 " + seconds + " /bin/sh -s",
                Math.Max(1, timeoutMs), "SSH server is not reachable.", SshProbe, text => details = text);
            string diagnostic = String.IsNullOrWhiteSpace(result) ? "SSH server is not reachable." : result.Trim();
            const string prefix = "SSH-ready at ";
            bool ready = diagnostic.StartsWith(prefix, StringComparison.Ordinal) && diagnostic.Length > prefix.Length;
            if (!String.IsNullOrWhiteSpace(details))
            {
                if (!ready)
                    throw new IOException(diagnostic, new IOException(details));
                log.Write("DETAIL", details);
            }
            return new SshProbeResult(ready, ready ? diagnostic.Substring(prefix.Length) : null, ready ? null : diagnostic);
        }

        private async Task ReleaseKeeperAsync(bool failedStartup = false)
        {
            Process previous = keeper;
            ProcessPipes pipes = keeperPipes;
            Task<string> errors = keeperErrors;
            Task<string> ready = keeperReady;
            keeper = null;
            keeperPipes = null;
            keeperErrors = keeperReady = null;
            if (previous == null) return;
            Task cleanupDeadline = Task.Delay(failedStartup ? 1000 : 3000);
            Exception failure = null;
            try
            {
                if (!previous.HasExited)
                {
                    // A failed handshake may still be writing: kill before closing its stdin.
                    if (!failedStartup)
                        try { previous.StandardInput.Close(); } catch (IOException) { }
                    if (failedStartup || !await Task.Run(() => previous.WaitForExit(2000)))
                    {
                        previous.Kill();
                        if (!await Task.Run(() => previous.WaitForExit(1000)))
                            throw new IOException("The keep-alive process did not exit after cleanup.");
                    }
                }
            }
            catch (InvalidOperationException) { }
            catch (Exception ex) { failure = ex; }
            Task pending = Task.WhenAll(errors ?? Task.FromResult(""), ready ?? Task.FromResult(""));
            try { if (pipes != null) await pipes.CloseAsync(pending, cleanupDeadline); }
            catch (Exception ex) { failure = failure == null ? ex : new AggregateException(failure, ex); }
            previous.Dispose();
            if (errors != null && errors.Status == TaskStatus.RanToCompletion && !String.IsNullOrWhiteSpace(errors.Result))
                log.Write("DETAIL", errors.Result);
            if (failure != null) throw failure;
        }

        public void Dispose()
        {
            // Release our pipe only; do not terminate an already running distribution.
            if (keeper != null)
            {
                try
                {
                    if (keeperPipes != null)
                        keeperPipes.CloseAsync(Task.WhenAll(keeperErrors ?? Task.FromResult(""),
                            keeperReady ?? Task.FromResult("")), Task.Delay(1000)).GetAwaiter().GetResult();
                }
                catch (Exception ex) { log.Error(ex, "Could not close keep-alive streams"); }
                finally { keeper.Dispose(); keeper = null; keeperPipes = null; keeperErrors = keeperReady = null; }
            }
        }
    }
}
