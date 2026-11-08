using System.Diagnostics;
using System.Text.RegularExpressions;
using AgentRelay.Core;

namespace AgentRelay.Agent;

public sealed class WorktreeManager
{
    private static readonly Regex SafeTaskId = new(@"\A[A-Za-z0-9][A-Za-z0-9._-]*\z", RegexOptions.CultureInvariant);
    private readonly IReadOnlyList<string> _repositories;
    public Action<string>? BeforeWorktreeAdd { get; set; }

    public WorktreeManager(IEnumerable<string> approvedRepositories)
    {
        _repositories = approvedRepositories.Select(Path.GetFullPath).ToArray();
        if (_repositories.Count == 0) throw new ArgumentException("At least one repository must be approved.", nameof(approvedRepositories));
    }

    public string ValidateRepository(string repository)
    {
        var full = Path.GetFullPath(repository);
        RejectReparsePointAncestors(full);
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
        if (Directory.Exists(actual) || File.Exists(actual))
        {
            var resolved = ResolveExistingPath(actual);
            if (!SamePath(resolved, actual)) throw new InvalidOperationException("Workspace resolves outside its registered lexical path.");
        }
        if (!SamePath(expected, actual) || !IsContained(actual, Path.Combine(repository, ".agentrelay-worktrees")))
            throw new InvalidOperationException("Workspace must be the task-specific child of the approved repository.");
        return actual;
    }

    public async Task<string> CreateAsync(TaskRequest request, CancellationToken cancellationToken = default)
    {
        var repository = ValidateRepository(request.Repository);
        var workspaceRoot = Path.Combine(repository, ".agentrelay-worktrees");
        if (Directory.Exists(workspaceRoot) && (File.GetAttributes(workspaceRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Worktree root cannot be a symbolic link or junction.");
        Directory.CreateDirectory(workspaceRoot);
        var lockDirectory = Path.Combine(workspaceRoot, ".locks");
        Directory.CreateDirectory(lockDirectory);
        if ((File.GetAttributes(workspaceRoot) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(lockDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Worktree root or lock directory cannot be a symbolic link or junction.");
        var lockPath = Path.Combine(lockDirectory, "root.lock");
        using var lease = AcquireWorkspaceLock(lockPath, "worktree root");
        repository = ValidateRepository(request.Repository);
        ValidateWorkspaceRoot(workspaceRoot);
        var workspace = ValidateWorkspace(request);
        Directory.CreateDirectory(Path.GetDirectoryName(workspace)!);
        if (Directory.Exists(workspace) || File.Exists(workspace)) throw new InvalidOperationException("Task workspace already exists.");
        if (BranchExists(repository, request.Branch)) throw new InvalidOperationException("Task branch already exists.");
        BeforeWorktreeAdd?.Invoke(workspaceRoot);
        ValidateWorkspaceRoot(workspaceRoot);
        if (Directory.Exists(workspace) || File.Exists(workspace)) throw new InvalidOperationException("Task workspace was created concurrently.");
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
        var resolvedWorkspace = ResolveExistingPath(workspace);
        if (!SamePath(resolvedWorkspace, workspace))
            throw new InvalidOperationException("Created worktree resolves outside its task-specific workspace path.");
        ValidateRepository(repository);
        return workspace;
    }

    public Task<string> CreateOrValidateAsync(TaskRequest request, CancellationToken cancellationToken = default)
    {
        var repository = ValidateRepository(request.Repository);
        var workspace = ValidateWorkspace(request);
        if (Directory.Exists(workspace))
        {
            var resolved = ResolveExistingPath(workspace);
            if (!SamePath(resolved, workspace) || !File.Exists(Path.Combine(workspace, ".git")))
                throw new InvalidOperationException("Existing task workspace is not the registered Git worktree.");
            return Task.FromResult(workspace);
        }
        return CreateAsync(request, cancellationToken);
    }

    private static void ValidateWorkspaceRoot(string root)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Worktree root is missing.");
        var locks = Path.Combine(root, ".locks");
        if (!Directory.Exists(locks)) throw new DirectoryNotFoundException("Worktree lock directory is missing.");
        foreach (var path in new[] { root, locks })
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Worktree root or lock directory cannot be a symbolic link or junction.");
    }

    private static string ResolveExistingPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? throw new ArgumentException("Path must be rooted.", nameof(path));
        var resolved = root;
        foreach (var part in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(resolved, part);
            if (!Directory.Exists(next) && !File.Exists(next)) throw new DirectoryNotFoundException("Approved repository path does not exist.");
            FileSystemInfo item = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            var target = item.ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null) throw new InvalidOperationException("Path contains a symbolic link or junction.");
            resolved = item.FullName;
        }
        return Path.GetFullPath(resolved);
    }

    private static void RejectReparsePointAncestors(string path)
    {
        var full = Path.GetFullPath(path);
        for (var current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current)!)
        {
            if (!Directory.Exists(current)) continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Repository path cannot contain a symbolic link or junction.");
            var parent = Path.GetDirectoryName(current);
            if (parent == current) break;
        }
    }

    private static FileStream AcquireWorkspaceLock(string lockPath, string taskId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException exception)
            {
                if (DateTime.UtcNow >= deadline) throw new TimeoutException($"Timed out acquiring worktree lock for task {taskId}.", exception);
                Thread.Sleep(10);
            }
        }
    }

    private static bool BranchExists(string repository, string branch)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = repository, UseShellExecute = false, RedirectStandardOutput = true };
        start.ArgumentList.Add("show-ref"); start.ArgumentList.Add("--verify"); start.ArgumentList.Add("--quiet"); start.ArgumentList.Add("refs/heads/" + branch);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to inspect task branch.");
        process.WaitForExit();
        return process.ExitCode == 0;
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
