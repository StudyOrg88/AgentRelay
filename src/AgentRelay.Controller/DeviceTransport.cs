using System.Diagnostics;
using System.Text.Json;
using AgentRelay.Core;

namespace AgentRelay.Controller;

public sealed record ProcessInvocation(string FileName, IReadOnlyList<string> Arguments, string StandardInput,
    TimeSpan Timeout);

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public interface IProcessInvoker
{
    Task<ProcessResult> InvokeAsync(ProcessInvocation invocation, CancellationToken cancellationToken);
}

public sealed class DeviceTransport
{
    private static readonly HashSet<string> Operations = ["preflight", "submit", "status", "report", "cancel"];
    private readonly IProcessInvoker _processInvoker;
    private readonly Func<RegisteredDevice, string, string, CancellationToken, Task<string>>? _localInvoker;
    private readonly string _agentExecutable;
    private readonly string _sshExecutable;
    private readonly TimeSpan _timeout;

    public DeviceTransport(string agentExecutable = "agentrelay-agent", string sshExecutable = "ssh",
        TimeSpan? timeout = null, IProcessInvoker? processInvoker = null,
        Func<RegisteredDevice, string, string, CancellationToken, Task<string>>? localInvoker = null)
    {
        // Trusted deployment configuration only; keep the remote command token shell-safe and independent of task data.
        _agentExecutable = RequireRemoteExecutable(agentExecutable);
        _sshExecutable = RequireProcessExecutable(sshExecutable);
        _timeout = timeout ?? TimeSpan.FromMinutes(2);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be between zero and ten minutes.");
        _processInvoker = processInvoker ?? new SystemProcessInvoker();
        _localInvoker = localInvoker;
    }

    public async Task<JsonElement> InvokeAsync(RegisteredDevice device, string operation, string payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!Operations.Contains(operation)) throw new ArgumentException("Unsupported agent operation.", nameof(operation));
        try { using var _ = JsonDocument.Parse(payload); }
        catch (JsonException) { throw new DeviceTransportException("Request payload is not valid JSON."); }

        string output;
        if (device.Connection == "local")
        {
            if (_localInvoker is null)
            {
                var result = await InvokeProcessAsync(new ProcessInvocation(_agentExecutable,
                    [operation], payload, _timeout), cancellationToken).ConfigureAwait(false);
                EnsureSuccess(result);
                output = result.StandardOutput;
            }
            else
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_timeout);
                try { output = await _localInvoker(device, operation, payload, timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { throw new DeviceTransportException("Agent invocation timed out."); }
            }
        }
        else if (device.Connection == "ssh" && IsSafeSshHost(device.Host) && IsSafeSshUser(device.User) && device.Port is > 0 and <= 65535)
        {
            var command = $"{_agentExecutable} {operation}";
            var result = await InvokeProcessAsync(new ProcessInvocation(_sshExecutable,
                ["-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "ConnectionAttempts=1",
                    "-o", "ConnectTimeout=15", "-p", device.Port.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    $"{device.User}@{device.Host}", command], payload, _timeout), cancellationToken).ConfigureAwait(false);
            EnsureSuccess(result);
            output = result.StandardOutput;
        }
        else throw new DeviceTransportException("Device connection configuration is invalid.");

        try
        {
            using var document = JsonDocument.Parse(output);
            return document.RootElement.Clone();
        }
        catch (JsonException) { throw new DeviceTransportException("Agent returned invalid JSON."); }
    }

    private static void EnsureSuccess(ProcessResult result)
    {
        if (result.ExitCode != 0)
        {
            if (result.StandardError.Contains("host key verification failed", StringComparison.OrdinalIgnoreCase) ||
                result.StandardError.Contains("remote host identification has changed", StringComparison.OrdinalIgnoreCase))
                throw new DeviceTransportException("SSH host key verification failed; host identity is not trusted.");
            throw new DeviceTransportException("Agent invocation failed with a non-zero exit code.");
        }
    }

    private async Task<ProcessResult> InvokeProcessAsync(ProcessInvocation invocation, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(invocation.Timeout);
        try { return await _processInvoker.InvokeAsync(invocation, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new DeviceTransportException("Agent invocation timed out."); }
    }

    private static string RequireRemoteExecutable(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        System.Text.RegularExpressions.Regex.IsMatch(value, @"\A[A-Za-z0-9][A-Za-z0-9._-]*\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            ? value : throw new ArgumentException("Remote agent executable must be a trusted, shell-safe executable basename.", nameof(value));

    private static string RequireProcessExecutable(string value) => !string.IsNullOrWhiteSpace(value) && !value.Contains('\0')
        ? value : throw new ArgumentException("Process executable is required.", nameof(value));

    private static bool IsSafeSshHost(string? value) => value is not null &&
        System.Text.RegularExpressions.Regex.IsMatch(value, @"\A(?=.{1,253}\z)[A-Za-z0-9](?:[A-Za-z0-9_-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9_-]{0,61}[A-Za-z0-9])?)*\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static bool IsSafeSshUser(string? value) => value is not null &&
        System.Text.RegularExpressions.Regex.IsMatch(value, @"\A[A-Za-z0-9_][A-Za-z0-9._-]*\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public sealed class SystemProcessInvoker(int maximumStandardOutputBytes = 1024 * 1024,
        int maximumStandardErrorBytes = 64 * 1024) : IProcessInvoker
    {
        private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(3);

        public async Task<ProcessResult> InvokeAsync(ProcessInvocation invocation, CancellationToken cancellationToken)
        {
            using var process = new Process { StartInfo = new ProcessStartInfo
            {
                FileName = invocation.FileName,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }};
            foreach (var argument in invocation.Arguments) process.StartInfo.ArgumentList.Add(argument);
            if (maximumStandardOutputBytes < 1 || maximumStandardErrorBytes < 1)
                throw new ArgumentOutOfRangeException(nameof(maximumStandardOutputBytes));
            using var timeout = new CancellationTokenSource(invocation.Timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            Task<string>? stdout = null;
            Task<string>? stderr = null;
            try
            {
                if (!process.Start()) throw new DeviceTransportException("Could not start agent process.");
                stdout = ReadBoundedAsync(process.StandardOutput, maximumStandardOutputBytes, linked.Token);
                stderr = ReadBoundedAsync(process.StandardError, maximumStandardErrorBytes, linked.Token);
                await process.StandardInput.WriteAsync(invocation.StandardInput.AsMemory(), linked.Token).ConfigureAwait(false);
                process.StandardInput.Close();
                var pending = new List<Task>
                {
                    process.WaitForExitAsync(linked.Token), stdout, stderr
                };
                while (pending.Count > 0)
                {
                    var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                    pending.Remove(completed);
                    await completed.ConfigureAwait(false);
                }
                return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
            }
            catch (OutputLimitExceededException)
            {
                await StopAndWaitAsync(process).ConfigureAwait(false);
                await ObserveAsync(stdout, stderr).ConfigureAwait(false);
                throw new DeviceTransportException("Agent output exceeded the configured limit.");
            }
            catch (OperationCanceledException)
            {
                await StopAndWaitAsync(process).ConfigureAwait(false);
                await ObserveAsync(stdout, stderr).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested) throw;
                throw new DeviceTransportException("Agent invocation timed out.");
            }
            catch (System.ComponentModel.Win32Exception)
            { throw new DeviceTransportException("Could not start agent process."); }
        }

        private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximumBytes, CancellationToken cancellationToken)
        {
            var buffer = new char[4096];
            var output = new System.Text.StringBuilder();
            var bytes = 0;
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (count == 0) return output.ToString();
                bytes += System.Text.Encoding.UTF8.GetByteCount(buffer.AsSpan(0, count));
                if (bytes > maximumBytes) throw new OutputLimitExceededException();
                output.Append(buffer, 0, count);
            }
        }

        private static async Task StopAndWaitAsync(Process process)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // Preserve the original timeout/cancellation/output-limit failure.
            }
            try
            {
                using var cleanup = new CancellationTokenSource(CleanupTimeout);
                await process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Cleanup is bounded and must not replace the triggering failure.
            }
        }

        private static async Task ObserveAsync(params Task<string>?[] readers)
        {
            foreach (var reader in readers)
            {
                if (reader is null) continue;
                try { await reader.ConfigureAwait(false); }
                catch (Exception exception) when (exception is OperationCanceledException or OutputLimitExceededException or System.IO.IOException or ObjectDisposedException)
                {
                    // Reader failures are already represented by the triggering process failure.
                }
            }
        }

        private sealed class OutputLimitExceededException : Exception;
    }
}

public sealed class DeviceTransportException(string message) : Exception(message);
