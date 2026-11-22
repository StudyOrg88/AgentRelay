using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentRelay.Gate;

public sealed class LocalGitHubAppJwtSigner : IGitHubAppJwtSigner
{
    private const string Repository = "StudyOrg88/AgentRelay-v2-sandbox";
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(10);
    private readonly IReadOnlyDictionary<(long AppId, long InstallationId), string> _keyPaths;
    private readonly long _repositoryId;

    private LocalGitHubAppJwtSigner(long repositoryId, IReadOnlyDictionary<(long AppId, long InstallationId), string> keyPaths)
    {
        _repositoryId = repositoryId;
        _keyPaths = keyPaths;
    }

    public static LocalGitHubAppJwtSigner? TryCreate(StageOneGitHubConfig config, Func<string, string?> getEnvironmentVariable)
    {
        if (!Program.IsApprovedStageOneConfig(config)) return null;

        (string Name, long AppId, long InstallationId)[] roles =
        [
            ("REVIEWER", config.ReviewerAppId, config.ReviewerInstallationId),
            ("GATE", config.GateAppId, config.GateInstallationId),
            ("MERGE", config.MergeAppId, config.MergeInstallationId),
            ("WORKER", config.WorkerAppId, config.WorkerInstallationId),
            ("ORCHESTRATOR", config.OrchestratorAppId, config.OrchestratorInstallationId)
        ];
        var paths = new Dictionary<(long AppId, long InstallationId), string>();
        foreach (var role in roles)
        {
            var appId = getEnvironmentVariable($"AGENTRELAY_APP_{role.Name}_ID");
            var keyPath = getEnvironmentVariable($"AGENTRELAY_APP_{role.Name}_KEY_PATH");
            if (!long.TryParse(appId, out var parsedAppId) || parsedAppId != role.AppId ||
                string.IsNullOrWhiteSpace(keyPath) || !Path.IsPathFullyQualified(keyPath))
                return null;
            paths.Add((role.AppId, role.InstallationId), keyPath);
        }

        return new LocalGitHubAppJwtSigner(config.RepositoryId, paths);
    }

    public Task<SignedGitHubAppJwt> SignAsync(GitHubAppJwtRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.RepositoryId != _repositoryId || request.RepositoryFullName != Repository ||
            !_keyPaths.TryGetValue((request.AppId, request.InstallationId), out var keyPath))
            throw new GitHubTransportException("GitHub transport failed.");

        var now = DateTimeOffset.UtcNow;
        if (request.ExpiresAt <= now || request.IssuedAt > now.AddMinutes(1) ||
            request.ExpiresAt <= request.IssuedAt || request.ExpiresAt - request.IssuedAt > MaximumLifetime)
            throw new GitHubTransportException("GitHub transport failed.");

        byte[]? keyBytes = null;
        char[]? pem = null;
        try
        {
            keyBytes = File.ReadAllBytes(keyPath);
            pem = Encoding.UTF8.GetChars(keyBytes);
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);

            var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
            var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new
            {
                iat = request.IssuedAt.ToUnixTimeSeconds(),
                exp = request.ExpiresAt.ToUnixTimeSeconds(),
                iss = request.AppId
            }));
            var content = Encoding.ASCII.GetBytes($"{header}.{payload}");
            var signature = rsa.SignData(content, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var jwt = $"{header}.{payload}.{Encode(signature)}";
            CryptographicOperations.ZeroMemory(content);
            CryptographicOperations.ZeroMemory(signature);
            return Task.FromResult(new SignedGitHubAppJwt(request, new EphemeralSecret(jwt)));
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitHubTransportException("GitHub transport failed.");
        }
        finally
        {
            if (keyBytes is not null) CryptographicOperations.ZeroMemory(keyBytes);
            if (pem is not null) Array.Clear(pem);
        }
    }

    private static string Encode(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
