namespace Proctoring.Api.Configuration;

/// <summary>
/// Minimal .env loader for local development: reads KEY=VALUE lines from the nearest .env file
/// (searching upwards from the working directory) into configuration. Real environment variables
/// always win, so containers and CI are not affected. Values are never logged.
/// </summary>
public static class DotEnv
{
    public static string? Load(IConfigurationBuilder config, string startDirectory, int maxLevels = 5)
    {
        var dir = new DirectoryInfo(startDirectory);
        for (var i = 0; i < maxLevels && dir is not null; i++, dir = dir.Parent)
        {
            var file = Path.Combine(dir.FullName, ".env");
            if (!File.Exists(file)) continue;

            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in File.ReadAllLines(file))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq].Trim();
                if (Environment.GetEnvironmentVariable(key) is not null) continue; // real env wins
                values[key] = line[(eq + 1)..].Trim().Trim('"');
            }
            // Insert before environment variables so they keep precedence.
            config.Sources.Insert(0, new Microsoft.Extensions.Configuration.Memory.MemoryConfigurationSource { InitialData = values });
            return file;
        }
        return null;
    }
}
