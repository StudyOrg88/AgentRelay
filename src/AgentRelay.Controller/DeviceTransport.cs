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
        _agentExecutable = RequireExecutable(agentExecutable);
        _sshExecutable = RequireExecutable(sshExecutable);
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
        else if (device.Connection == "ssh" && device.Host is not null && device.User is not null && device.Port is > 0 and <= 65535)
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

    private static string RequireExecutable(string value) => !string.IsNullOrWhiteSpace(value) && !value.Contains('\0')
        ? value : throw new ArgumentException("Executable is required.");

    private sealed class SystemProcessInvoker : IProcessInvoker
    {
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
            try
            {
                if (!process.Start()) throw new DeviceTransportException("Could not start agent process.");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(invocation.Timeout);
                var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.StandardInput.WriteAsync(invocation.StandardInput.AsMemory(), timeout.Token).ConfigureAwait(false);
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw new DeviceTransportException("Agent invocation timed out.");
            }
            catch (System.ComponentModel.Win32Exception)
            { throw new DeviceTransportException("Could not start agent process."); }
        }
    }
}

public sealed class DeviceTransportException(string message) : Exception(message);
