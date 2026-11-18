namespace AgentRelay.Gate;

public static class Program
{
    public static Task<int> Main(string[] args)
        => GateCli.RunAsync(args, new GateDependencies(new FailClosedGitHubApi(), Console.Error), Console.Out, Console.Error, CancellationToken.None);
}
