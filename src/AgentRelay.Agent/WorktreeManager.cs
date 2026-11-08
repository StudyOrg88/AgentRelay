using System.Diagnostics;
using System.Text.RegularExpressions;
using AgentRelay.Core;

namespace AgentRelay.Agent;

public sealed class WorktreeManager
{
    private static readonly Regex SafeTaskId = new(@"\A[A-Za-z0-9][A-Za-z0-9._-]*\z", RegexOptions.CultureInvariant);
    private readonly IReadOnlyList<string> _repositories;

    public WorktreeManager(IEnumerable<string> approvedRepositories)
    {
        _repositories = approvedRepositories.Select(Path.GetFullPath).ToArray();
        if (_repositories.Count == 0) throw new ArgumentException("At least one repository must be approved.", nameof(approvedRepositories));
    }

    public string ValidateRepository(string repository)
    {
        var full = Path.GetFullPath(repository);
        if (!_repositories.Any(root => SamePath(root, full)) || !Directory.Exists(full) ||
            !Directory.Exists(Path.Combine(full, ".git")) && !File.Exists(Path.Combine(full, ".git")))
            throw new InvalidOperationException("Repository is not allowlisted or is not a Git repository.");
        return full;
    }

    public string ValidateWorkspace(TaskRequest request)
    {
        var repository = ValidateRepository(request.Repository);
        if (!SafeTaskId.IsMatch(request.TaskId) || request.TaskId.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid task identifier.");
        var worktreeRoot = Path.Combine(repository, ".agentrelay-worktrees");
        if (Directory.Exists(worktreeRoot) && (File.GetAttributes(worktreeRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Worktree root cannot be a symbolic link or junction.");
        var expected = Path.GetFullPath(Path.Combine(worktreeRoot, request.TaskId));
        var actual = Path.GetFullPath(request.Workspace);
        if (!SamePath(expected, actual) || !IsContained(actual, Path.Combine(repository, ".agentrelay-worktrees")))
            throw new InvalidOperationException("Workspace must be the task-specific child of the approved repository.");
        return actual;
    }

    public async Task<string> CreateAsync(TaskRequest request, CancellationToken cancellationToken = default)
    {
        var repository = ValidateRepository(request.Repository);
        var workspace = ValidateWorkspace(request);
        Directory.CreateDirectory(Path.GetDirectoryName(workspace)!);
        if (Directory.Exists(workspace) || File.Exists(workspace)) throw new InvalidOperationException("Task workspace already exists.");
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = repository,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        start.ArgumentList.Add("worktree");
        start.ArgumentList.Add("add");
        start.ArgumentList.Add("-b");
        start.ArgumentList.Add(request.Branch);
        start.ArgumentList.Add(workspace);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start git.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var detail = await stderr.ConfigureAwait(false);
            throw new InvalidOperationException("git worktree add failed: " + Sanitize(detail));
        }
        _ = await stdout.ConfigureAwait(false);
        return workspace;
    }

    private static bool IsContained(string candidate, string root)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullCandidate = Path.GetFullPath(candidate);
        return fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static bool SamePath(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string Sanitize(string value) => value.Length > 500 ? value[..500] : value;
}
