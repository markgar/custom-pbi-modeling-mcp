namespace PbiModelingMcp.Configuration;

/// <summary>
/// Minimal <c>.env</c> loader. Looks for a <c>.env</c> file (current dir,
/// next to the binary, then walking up to the repo root) and sets any
/// <c>KEY=VALUE</c> pairs that aren't already in the environment.
/// Real environment variables always win over the file.
/// </summary>
/// <remarks>
/// Format:
/// <list type="bullet">
///   <item>Lines starting with <c>#</c> and blank lines are ignored.</item>
///   <item>Values may be wrapped in single or double quotes; quotes are stripped.</item>
///   <item>No shell expansion is performed.</item>
/// </list>
/// </remarks>
internal static class DotEnv
{
    public static void Load()
    {
        if (TryFind(out var path))
        {
            Apply(path!);
        }
    }

    private static bool TryFind(out string? path)
    {
        // Probe order:
        //   1. current working directory
        //   2. next to the executable
        //   3. (opt-in) walk up from cwd to find a sibling .env
        //
        // Walk-up is convenient in dev when `dotnet run` puts cwd inside the
        // project folder, but a published binary launched from ~ would happily
        // pick up an unrelated ~/.env. So walk-up is enabled only in Debug
        // builds, or when PBI_MCP_DOTENV=1 is set explicitly.
        var cwd = Directory.GetCurrentDirectory();
        var candidates = new List<string>
        {
            Path.Combine(cwd, ".env"),
            Path.Combine(AppContext.BaseDirectory, ".env"),
        };

        if (ShouldWalkUp())
        {
            var dir = cwd;
            for (var i = 0; i < 6 && dir is not null; i++)
            {
                candidates.Add(Path.Combine(dir, ".env"));
                dir = Path.GetDirectoryName(dir);
            }
        }

        path = candidates.FirstOrDefault(File.Exists);
        return path is not null;
    }

    private static bool ShouldWalkUp()
    {
#if DEBUG
        return true;
#else
        return string.Equals(
            Environment.GetEnvironmentVariable("PBI_MCP_DOTENV"),
            "1",
            StringComparison.Ordinal);
#endif
    }

    private static void Apply(string path)
    {
        foreach (var (key, val) in Parse(File.ReadAllLines(path)))
        {
            // Real env vars always win.
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
            {
                Environment.SetEnvironmentVariable(key, val);
            }
        }
    }

    /// <summary>
    /// Parse <c>KEY=VALUE</c> pairs from raw <c>.env</c> lines. Lines that are
    /// blank, comment-only, or malformed (no <c>=</c>, or starting with
    /// <c>=</c>) are skipped. Quoted values are unwrapped.
    /// </summary>
    /// <remarks>
    /// Pure function; does not touch the environment. Exposed
    /// <c>internal</c> for unit tests.
    /// </remarks>
    internal static IEnumerable<KeyValuePair<string, string>> Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim();

            if (val.Length >= 2 &&
                ((val[0] == '"' && val[^1] == '"') ||
                 (val[0] == '\'' && val[^1] == '\'')))
            {
                val = val[1..^1];
            }

            yield return new KeyValuePair<string, string>(key, val);
        }
    }
}
