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
        private Task<string> keeperErrors;
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
                using (Process process = new Process { StartInfo = Info(arguments, script == null ? Encoding.Unicode : Encoding.UTF8) })
                {
                    if (!process.Start()) throw new IOException("Could not start wsl.exe.");
                    Task<string> output = process.StandardOutput.ReadToEndAsync();
                    Task<string> error = process.StandardError.ReadToEndAsync();
                    if (script != null) await process.StandardInput.WriteAsync(script.Replace("\r", ""));
                    process.StandardInput.Close();
                    if (!await Task.Run(() => process.WaitForExit(timeoutMs)))
                    {
                        try { process.Kill(); } catch (InvalidOperationException) { }
                        throw new TimeoutException("WSL command timed out: " + arguments);
                    }
                    Task pipes = Task.WhenAll(output, error);
                    if (await Task.WhenAny(pipes, Task.Delay(1000)) != pipes)
                        throw new TimeoutException("WSL output pipes did not close.");
                    string stdout = await output;
                    string stderr = await error;
                    if (process.ExitCode != 0)
                        throw new IOException("WSL command: " + arguments + "\nExit code: " + process.ExitCode + "\n" + stdout + "\n" + stderr);
                    if (!String.IsNullOrWhiteSpace(stderr))
                    {
                        if (captureDiagnostics != null) captureDiagnostics(stderr);
                        else log.Write("DETAIL", stderr);
                    }
                    return stdout;
                }
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
                StartedByApp = !WasAlreadyRunning;
                keeperErrors = keeper.StandardError.ReadToEndAsync();
                await keeper.StandardInput.WriteAsync(script);
                await keeper.StandardInput.FlushAsync();
                Task<string> ready = keeper.StandardOutput.ReadLineAsync();
                if (await Task.WhenAny(ready, Task.Delay(30000)) != ready)
                {
                    startupFailure = "Ubuntu did not become ready within 30 seconds.";
                    throw new IOException(startupFailure);
                }
                string marker = await ready;
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
                await ReleaseKeeperAsync();
                throw new IOException(startupFailure, startupError);
            }
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

        public async Task<string> SshDiagnosticAsync(int timeoutMs)
        {
            if (!KeeperAlive) return "Ubuntu's keep-alive connection is not running.";
            // Bound Linux children too, even if the Windows WSL client gets killed.
            string seconds = (Math.Max(100, timeoutMs - 250) / 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            string details = null;
            string result = await CommandAsync(Target + " --user root --exec /usr/bin/timeout -k 0.1 " + seconds + " /bin/sh -s",
                Math.Max(1, timeoutMs), "SSH server is not reachable.", SshProbe, text => details = text);
            string diagnostic = String.IsNullOrWhiteSpace(result) ? "SSH server is not reachable." : result.Trim();
            if (!String.IsNullOrWhiteSpace(details))
            {
                if (!diagnostic.StartsWith("SSH-", StringComparison.Ordinal))
                    throw new IOException(diagnostic, new IOException(details));
                log.Write("DETAIL", details);
            }
            return diagnostic;
        }

        private async Task ReleaseKeeperAsync()
        {
            Process previous = keeper;
            keeper = null;
            if (previous == null) return;
            try
            {
                if (!previous.HasExited)
                {
                    try { previous.StandardInput.Close(); } catch (IOException) { }
                    if (!await Task.Run(() => previous.WaitForExit(2000)))
                    {
                        previous.Kill();
                        await Task.Run(() => previous.WaitForExit(1000));
                    }
                }
                if (keeperErrors != null && keeperErrors.Status == TaskStatus.RanToCompletion && !String.IsNullOrWhiteSpace(keeperErrors.Result))
                    log.Write("DETAIL", keeperErrors.Result);
            }
            catch (InvalidOperationException) { }
            finally { previous.Dispose(); keeperErrors = null; }
        }

        public void Dispose()
        {
            // Release our pipe only; do not terminate an already running distribution.
            if (keeper != null)
            {
                try { keeper.StandardInput.Close(); } catch (Exception) { }
                keeper.Dispose();
                keeper = null;
            }
        }
    }
}
