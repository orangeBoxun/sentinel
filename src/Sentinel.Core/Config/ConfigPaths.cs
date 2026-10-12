namespace Sentinel.Core.Config;

/// <summary>
/// Where the config lives. Shared so the Service (which owns the file) and the Agent (which reads it
/// through the Service, and directly when no Service is running) always agree.
/// </summary>
public static class ConfigPaths
{
    /// <summary>
    /// %ProgramData% is the real location — a path standard users cannot write is the foundation the
    /// delay-unlock protection rests on. It needs elevation to create, so a machine where that fails
    /// falls back to the user profile rather than refusing to start.
    /// </summary>
    public static string DefaultConfigPath()
    {
        var shared = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Sentinel");

        try
        {
            Directory.CreateDirectory(shared);
            return Path.Combine(shared, "config.json");
        }
        catch
        {
            var local = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sentinel");
            Directory.CreateDirectory(local);
            return Path.Combine(local, "config.json");
        }
    }

    /// <summary>--config &lt;path&gt;, else SENTINEL_CONFIG, else the default location.</summary>
    public static string Resolve(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--config", StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        var fromEnv = Environment.GetEnvironmentVariable("SENTINEL_CONFIG");
        return string.IsNullOrWhiteSpace(fromEnv) ? DefaultConfigPath() : fromEnv;
    }
}
