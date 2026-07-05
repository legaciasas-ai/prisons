namespace Prison.Shared.Utilities;

/// <summary>
/// Locates the data-driven <c>content/</c> root (PLAN §6). Resolution order:
/// explicit argument, <c>PRISON_CONTENT_ROOT</c> env var, then walking up from the
/// application base directory (covers running from bin/ folders and the repo root).
/// </summary>
public static class ContentPaths
{
    public const string EnvVar = "PRISON_CONTENT_ROOT";

    public static string Resolve(string? explicitPath = null)
    {
        if (!string.IsNullOrEmpty(explicitPath))
            return Validate(explicitPath);

        var fromEnv = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrEmpty(fromEnv))
            return Validate(fromEnv);

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "content");
            if (Directory.Exists(Path.Combine(candidate, "tiles")))
                return candidate;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the content/ directory. Set {EnvVar} or pass an explicit path.");
    }

    private static string Validate(string path) =>
        Directory.Exists(path)
            ? path
            : throw new DirectoryNotFoundException($"Content root does not exist: {path}");
}
