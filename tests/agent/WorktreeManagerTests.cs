using AgentRelay.Agent;
using AgentRelay.Core;
using Xunit;

namespace AgentRelay.Agent.Tests;

public sealed class WorktreeManagerTests
{
    [Fact]
    public void Workspace_is_task_specific_and_contained_in_allowlisted_repository()
    {
        using var temp = new TemporaryDirectory();
        var repo = temp.CreateGitRepository("repo");
        var manager = new WorktreeManager([repo]);
        var task = Request(repo);

        Assert.Equal(Path.GetFullPath(Path.Combine(repo, ".agentrelay-worktrees", "task-one")), manager.ValidateWorkspace(task));
        Assert.Throws<InvalidOperationException>(() => manager.ValidateRepository(Path.Combine(temp.Path, "outside")));
        Assert.Throws<InvalidOperationException>(() => manager.ValidateWorkspace(task with { Workspace = Path.Combine(repo, "..", "outside", "task-one") }));
        Assert.Throws<InvalidOperationException>(() => manager.ValidateWorkspace(task with { TaskId = "..", Workspace = Path.Combine(repo, ".agentrelay-worktrees", "..") }));
        Assert.Throws<InvalidOperationException>(() => manager.ValidateWorkspace(task with { TaskId = "a/b", Workspace = Path.Combine(repo, ".agentrelay-worktrees", "a", "b") }));
        var linked = Path.Combine(repo, ".agentrelay-worktrees");
        Directory.CreateDirectory(linked);
        Directory.Delete(linked);
        var junction = Path.Combine(repo, "linked-worktrees");
        Directory.CreateDirectory(junction);
        Directory.CreateSymbolicLink(linked, junction);
        Assert.Throws<InvalidOperationException>(() => manager.ValidateWorkspace(task));
    }

    [Fact]
    public async Task Create_uses_distinct_worktrees_for_distinct_role_task_ids()
    {
        using var temp = new TemporaryDirectory();
        var repo = temp.CreateGitRepository("repo");
        var manager = new WorktreeManager([repo]);
        var worker = Request(repo);
        var reviewer = worker with { TaskId = "task-two", IdempotencyKey = "key-two", Role = AgentRelayRole.Reviewer,
            Workspace = Path.Combine(repo, ".agentrelay-worktrees", "task-two"), Branch = "task/review-two" };

        var first = await manager.CreateAsync(worker);
        var second = await manager.CreateAsync(reviewer);

        Assert.NotEqual(first, second);
        Assert.True(Directory.Exists(first));
        Assert.True(Directory.Exists(second));
    }

    private static TaskRequest Request(string repo) => new(TaskContract.SchemaVersion, "task-one", "key-one",
        "https://github.com/acme/repo/issues/1", AgentRelayRole.Worker, "device-a", repo,
        Path.Combine(repo, ".agentrelay-worktrees", "task-one"), "task/one", 1);

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        private readonly List<string> _repositories = [];
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public string CreateGitRepository(string name)
        {
            var path = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(path);
            _repositories.Add(path);
            RunGit(path, "init", "--quiet");
            RunGit(path, "-c", "user.name=AgentRelay Test", "-c", "user.email=test@example.invalid", "commit", "--allow-empty", "-m", "initial", "--quiet");
            return path;
        }
        private static void RunGit(string path, params string[] args)
        {
            var info = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = path, UseShellExecute = false };
            foreach (var arg in args) info.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(info)!;
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
        public void Dispose()
        {
            foreach (var repo in _repositories)
            {
                var info = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = repo, UseShellExecute = false, RedirectStandardOutput = true };
                info.ArgumentList.Add("worktree"); info.ArgumentList.Add("list"); info.ArgumentList.Add("--porcelain");
                using var list = System.Diagnostics.Process.Start(info)!;
                var output = list.StandardOutput.ReadToEnd(); list.WaitForExit();
                foreach (var line in output.Split('\n').Where(line => line.StartsWith("worktree ", StringComparison.Ordinal)).Select(line => line[9..].Trim()).Where(p => !string.Equals(p, repo, StringComparison.OrdinalIgnoreCase) && p.StartsWith(Path + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                {
                    var remove = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = repo, UseShellExecute = false };
                    remove.ArgumentList.Add("worktree"); remove.ArgumentList.Add("remove"); remove.ArgumentList.Add("--force"); remove.ArgumentList.Add(line);
                    using var process = System.Diagnostics.Process.Start(remove)!; process.WaitForExit();
                }
            }
            if (Directory.Exists(Path))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(Path, "*", SearchOption.AllDirectories))
                    try { File.SetAttributes(entry, FileAttributes.Normal); } catch { }
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
