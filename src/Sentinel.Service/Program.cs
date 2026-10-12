using Sentinel.Core.Config;
using Sentinel.Service;

// Three small modes that reuse this executable rather than scripting them elsewhere, so the
// installer and the scheduled task both invoke exactly the same code.
if (args.Contains("--watchdog"))
    return Watchdog.RunAsWatchdog(args);

if (args.Contains("--ensure-watchdog"))
    return Watchdog.Ensure(ConfigPaths.Resolve(args));

if (args.Contains("--remove-watchdog"))
    return Watchdog.Remove();

// Applied by install.ps1 after the service is created.
if (args.Contains("--harden"))
    return Watchdog.Harden();

// Applied by uninstall.ps1 before it tries to stop the service.
if (args.Contains("--relax"))
    return Watchdog.Relax();

// Applied by uninstall.ps1 before it removes anything. Done by the uninstaller rather than left to
// the watchdog noticing, because "the removal you asked for actually happened" is worth being
// deterministic about.
if (args.Contains("--seal-vault"))
    return Watchdog.SealVault(ConfigPaths.Resolve(args), Argument(args, "--reason") ?? "卸载程序请求封印保险箱");

// Anything else with arguments is a caller expecting a different tool. Falling through to the host
// would start a service in their console and hang it — say so instead.
if (args.Length > 0 && !args[0].Equals("--config", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"未知参数：{string.Join(' ', args)}");
    Console.Error.WriteLine("可用：--config <path> / --watchdog / --ensure-watchdog / --remove-watchdog / --harden / --relax / --seal-vault");
    return 2;
}

static string? Argument(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }

    return null;
}

// Runs as a Windows service under the SCM, and as a plain console app when started by hand — which
// is how the pipe, the config authority and the session launcher can be exercised without
// installing anything or holding administrator rights.
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options => options.ServiceName = "Sentinel");
builder.Services.AddSingleton<ConfigAuthority>();
builder.Services.AddHostedService<SentinelWorker>();

builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});

var host = builder.Build();
host.Run();
return 0;
