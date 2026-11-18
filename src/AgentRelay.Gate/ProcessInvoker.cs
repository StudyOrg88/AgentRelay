using System.Diagnostics;

namespace AgentRelay.Gate;

public sealed class ProcessInvoker
{
    public async Task<ProcessResult> InvokeAsync(string executable, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string?> environment, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(executable)) throw new ArgumentException("Executable is required.", nameof(executable));
        var startInfo = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        foreach (var variable in environment) startInfo.Environment[variable.Key] = variable.Value;

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, await outputTask.ConfigureAwait(false), await errorTask.ConfigureAwait(false));
    }
}
