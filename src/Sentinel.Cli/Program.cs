using System.Diagnostics;
using System.Text;
using Sentinel.Core.Abstractions;
using Sentinel.Core.Config;
using Sentinel.Core.Engine;
using Sentinel.Core.Ipc;
using Sentinel.Core.Logging;
using Sentinel.Core.Models;
using Sentinel.Core.Monitoring;
using Sentinel.Platform;
using Sentinel.Platform.Actions;

// Window titles and page text are routinely non-ASCII; without this the console mangles them.
Console.OutputEncoding = Encoding.UTF8;

// Sentinel CLI — configure, inspect and run the monitor without the tray UI.
// Usage:
//   init-config [--config <path>]        write a starter config
//   list-windows                         list monitorable windows
//   diagnose <pid|processName> [--body]  dump title/URL/body for one window
//   run [--config <path>] [--dry-run] [--once] [--interval <ms>]

return Run(args);

static int Run(string[] args)
{
    if (args.Length == 0)
        return Usage(1);

    var configPath = ArgValue(args, "--config") ?? DefaultConfigPath();

    switch (args[0].ToLowerInvariant())
    {
        case "-h":
        case "--help":
            return Usage(0);
        case "init-config":
            return InitConfig(configPath);
        case "config-validate":
            return ConfigValidate(configPath);
        case "add-target":
            return AddTarget(configPath, args);
        case "export-config":
            return ExportConfig(configPath, args);
        case "import-config":
            return ImportConfig(configPath, args);
        case "list-windows":
            return ListWindows(HasFlag(args, "--paths"));
        case "diagnose":
            return Diagnose(args);
        case "state":
            return State(args, restore: false);
        case "service-status":
            return ServiceStatus();
        case "bench-enum":
            return BenchEnum(args);
        case "bench-read":
            return BenchRead(args);
        case "restore":
            return State(args, restore: true);
        case "run":
            return RunMonitor(configPath, args);
        default:
            Console.Error.WriteLine($"Unknown command: {args[0]}");
            return Usage(1);
    }
}

static int Usage(int exitCode)
{
    Console.WriteLine("""
        Sentinel CLI

          init-config [--config <path>]        write a starter config
          config-validate [--config <path>]    check a config for mistakes that fail silently
          add-target [--from-window <pid|name>] [--process <name>] [--name <display>]
                     [--title <regex>] [--body] [--interval <ms>] [--config <path>]
          export-config [--config <path>] --out <file>
          import-config <file> [--config <path>] [--force]
          list-windows [--paths]               list monitorable windows
          diagnose <pid|name> [--body]         dump title/URL/body for one window
          state <pid|name>                     show a window's live/minimized state
          service-status                       ask the Sentinel service over its named pipe
          restore <pid|name>                   un-minimize a window
          bench-enum [--iterations N]          time window enumeration (in-process, no startup noise)
          bench-read <pid|name> [--body] [--iterations N]
                                               time UIA reads of one window
          run [--config <path>] [--dry-run] [--once] [--interval <ms>] [--verbose]

        Default config path: %ProgramData%\Sentinel\config.json
        """);
    return exitCode;
}

static string DefaultConfigPath() =>
    Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Sentinel",
        "config.json");

static int InitConfig(string path)
{
    if (File.Exists(path))
    {
        Console.Error.WriteLine($"Config already exists: {path}");
        return 2;
    }

    ConfigStore.Save(ConfigStore.Default(), path);
    Console.WriteLine($"Wrote starter config to {path}");
    return 0;
}

static int ConfigValidate(string path)
{
    if (!ConfigStore.TryLoad(path, out var config, out var error))
    {
        Console.Error.WriteLine($"无法读取配置: {error}");
        return 2;
    }

    Console.WriteLine($"校验 {path}");
    var errors = PrintIssues(ConfigValidator.Validate(config));
    return errors == 0 ? 0 : 1;
}

static int PrintIssues(IReadOnlyList<ConfigIssue> issues)
{
    if (issues.Count == 0)
    {
        Console.WriteLine("  没有问题。");
        return 0;
    }

    foreach (var issue in issues)
        Console.WriteLine($"  {issue}");

    var errors = issues.Count(i => i.Severity == IssueSeverity.Error);
    Console.WriteLine($"共 {errors} 个错误、{issues.Count - errors} 个警告。");
    return errors;
}

/// <summary>
/// The CLI's target picker: name a running window (or a process directly) and get a Target entry.
/// `list-windows` shows what is available.
/// </summary>
static int AddTarget(string configPath, string[] args)
{
    var config = ConfigStore.LoadOrDefault(configPath);

    var processName = ArgValue(args, "--process");
    var fromWindow = ArgValue(args, "--from-window");

    if (processName is null && fromWindow is not null)
    {
        using var enumerator = new WindowEnumerator();
        var window = enumerator.Enumerate().FirstOrDefault(w =>
            w.ProcessId.ToString() == fromWindow
            || w.ProcessName.Contains(fromWindow, StringComparison.OrdinalIgnoreCase));

        if (window is null)
        {
            Console.Error.WriteLine($"没有找到匹配 '{fromWindow}' 的窗口，先用 list-windows 看看。");
            return 2;
        }

        processName = window.ProcessName;
        Console.WriteLine($"从窗口解析到进程名: {processName}   （标题: {window.MainWindowTitle}）");
    }

    if (string.IsNullOrWhiteSpace(processName))
    {
        Console.Error.WriteLine("需要 --process <进程名>，或 --from-window <pid|进程名> 从运行中的窗口解析。");
        return 2;
    }

    var display = ArgValue(args, "--name") ?? processName;
    var id = UniqueId(config, Slug(display));

    var target = new Target
    {
        Id = id,
        DisplayName = display,
        ProcessNames = { processName },
        WindowTitlePattern = ArgValue(args, "--title"),
        Reader = new ReaderOptions
        {
            ScanBody = HasFlag(args, "--body"),
            IntervalMs = int.TryParse(ArgValue(args, "--interval"), out var ms) ? ms : 1000,
        },
    };

    config.Targets.Add(target);
    ConfigStore.Save(config, configPath);

    Console.WriteLine($"已添加目标 '{id}' 到 {configPath}");
    Console.WriteLine();
    Console.WriteLine("提醒：目标还没有规则，加上规则才会拦。例如：");
    Console.WriteLine($"  \"Rules\": [{{ \"Id\": \"r1\", \"Name\": \"拦 bilibili\", \"Enabled\": true,");
    Console.WriteLine($"    \"PolicyId\": \"notify-only\", \"Match\": {{ \"Type\": \"Keyword\",");
    Console.WriteLine($"    \"Fields\": [\"Url\"], \"Patterns\": [\"bilibili\"] }} }}]");
    Console.WriteLine();
    Console.WriteLine("改完用 config-validate 检查一遍（能抓出引用了不存在的策略之类的错误）。");
    return 0;
}

static int ExportConfig(string configPath, string[] args)
{
    var outPath = ArgValue(args, "--out");
    if (string.IsNullOrWhiteSpace(outPath))
    {
        Console.Error.WriteLine("需要 --out <文件路径>。");
        return 2;
    }

    if (!ConfigStore.TryLoad(configPath, out var config, out var error))
    {
        Console.Error.WriteLine($"无法读取配置: {error}");
        return 2;
    }

    Console.WriteLine($"校验 {configPath}");
    if (PrintIssues(ConfigValidator.Validate(config)) > 0)
    {
        Console.Error.WriteLine("配置有错误，未导出。");
        return 3;
    }

    ConfigStore.Save(config, outPath);
    Console.WriteLine($"已导出到 {outPath}");
    return 0;
}

static int ImportConfig(string configPath, string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("需要源文件: import-config <file> [--config <path>] [--force]");
        return 2;
    }

    var source = args[1];

    if (File.Exists(configPath) && !HasFlag(args, "--force"))
    {
        Console.Error.WriteLine($"目标配置已存在: {configPath}（加 --force 覆盖）");
        return 3;
    }

    // Strict load: silently falling back to an empty config here would overwrite the destination
    // with nothing.
    if (!ConfigStore.TryLoad(source, out var config, out var error))
    {
        Console.Error.WriteLine($"无法读取 {source}: {error}");
        return 2;
    }

    Console.WriteLine($"校验 {source}");
    var errors = PrintIssues(ConfigValidator.Validate(config));
    if (errors > 0)
    {
        Console.Error.WriteLine("配置有错误，未导入。");
        return 3;
    }

    ConfigStore.Save(config, configPath);
    Console.WriteLine($"已导入到 {configPath}（{config.Targets.Count} 个目标、{config.Policies.Count} 个策略）");
    return 0;
}

static string Slug(string value)
{
    var chars = value.Trim().ToLowerInvariant()
        .Select(c => char.IsLetterOrDigit(c) ? c : '-')
        .ToArray();

    var slug = new string(chars).Trim('-');
    while (slug.Contains("--", StringComparison.Ordinal))
        slug = slug.Replace("--", "-", StringComparison.Ordinal);

    return string.IsNullOrEmpty(slug) ? "target" : slug;
}

static string UniqueId(AppConfig config, string baseId)
{
    if (config.Targets.All(t => t.Id != baseId))
        return baseId;

    for (var i = 2; ; i++)
    {
        var candidate = $"{baseId}-{i}";
        if (config.Targets.All(t => t.Id != candidate))
            return candidate;
    }
}

static int ListWindows(bool withPaths)
{
    using var enumerator = new WindowEnumerator(includeExecutablePath: withPaths);
    Console.WriteLine(withPaths
        ? $"{"PID",8}  {"Process",-24}  {"Executable",-52}  Title"
        : $"{"PID",8}  {"Process",-24}  Title");

    foreach (var window in enumerator.Enumerate().OrderBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase))
    {
        var marker = window.IsForeground ? "  ← 前台" : "";
        Console.WriteLine(withPaths
            ? $"{window.ProcessId,8}  {window.ProcessName,-24}  {window.ExecutablePath,-52}  {window.MainWindowTitle}{marker}"
            : $"{window.ProcessId,8}  {window.ProcessName,-24}  {window.MainWindowTitle}{marker}");
    }

    return 0;
}

static int Diagnose(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("diagnose needs a target: diagnose <pid|processName> [--body]");
        return 2;
    }

    var target = args[1];
    bool wantBody = HasFlag(args, "--body");

    using var enumerator = new WindowEnumerator();
    var window = enumerator.Enumerate().FirstOrDefault(w =>
        w.ProcessId.ToString() == target
        || w.ProcessName.Contains(target, StringComparison.OrdinalIgnoreCase));

    if (window is null)
    {
        Console.Error.WriteLine($"No window found for '{target}'. Run 'list-windows' first.");
        return 2;
    }

    using var reader = new UiaContentReader(new ReaderOptions { ScanBody = wantBody });
    var snapshot = reader.Read(window);

    if (snapshot is null)
    {
        Console.Error.WriteLine("UIA returned no content for this window.");
        return 3;
    }

    Console.WriteLine($"Process  : {window.ProcessName} ({window.ProcessId})");
    Console.WriteLine($"Title    : {snapshot.Title}");
    Console.WriteLine($"URL      : {snapshot.Url ?? "(not found)"}");
    Console.WriteLine($"Body     : {(snapshot.BodyText is null ? "(not scanned — pass --body)" : $"{snapshot.BodyText.Length} chars")}");

    if (wantBody && !string.IsNullOrEmpty(snapshot.BodyText))
    {
        Console.WriteLine("--- body begin ---");
        Console.WriteLine(snapshot.BodyText);
        Console.WriteLine("--- body end ---");
    }

    return 0;
}

/// <summary>
/// Timing has to happen inside one process. Measuring across `dotnet run` / exe launches mixes in
/// startup cost and JIT, and the run-to-run variance then swamps the thing being measured — which
/// is exactly how a wrong conclusion gets drawn from real numbers.
/// </summary>
static int BenchEnum(string[] args)
{
    var iterations = int.TryParse(ArgValue(args, "--iterations"), out var n) ? n : 10;

    using var enumerator = new WindowEnumerator();
    enumerator.Enumerate();   // warm up: first call pays UIA/COM initialization

    var samples = new List<double>();
    var windowCount = 0;

    for (var i = 0; i < iterations; i++)
    {
        var stopwatch = Stopwatch.StartNew();
        windowCount = enumerator.Enumerate().Count;
        stopwatch.Stop();
        samples.Add(stopwatch.Elapsed.TotalMilliseconds);
    }

    Report($"Enumerate() — {windowCount} windows", samples);
    return 0;
}

static int BenchRead(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("bench-read needs a target: bench-read <pid|processName> [--body]");
        return 2;
    }

    var iterations = int.TryParse(ArgValue(args, "--iterations"), out var n) ? n : 10;
    var options = new ReaderOptions { ScanBody = HasFlag(args, "--body") };

    using var enumerator = new WindowEnumerator();
    var window = enumerator.Enumerate().FirstOrDefault(w =>
        w.ProcessId.ToString() == args[1]
        || w.ProcessName.Contains(args[1], StringComparison.OrdinalIgnoreCase));

    if (window is null)
    {
        Console.Error.WriteLine($"No window found for '{args[1]}'.");
        return 1;
    }

    using var reader = new UiaContentReader();
    reader.Read(window, options);   // warm up

    var samples = new List<double>();
    var lastLength = 0;

    for (var i = 0; i < iterations; i++)
    {
        var stopwatch = Stopwatch.StartNew();
        var snapshot = reader.Read(window, options);
        stopwatch.Stop();
        samples.Add(stopwatch.Elapsed.TotalMilliseconds);
        lastLength = snapshot?.BodyText?.Length ?? -1;
    }

    var what = options.ScanBody ? $"Read({window.ProcessName}, body={lastLength} chars)" : $"Read({window.ProcessName}, URL+title)";
    Report(what, samples);
    return 0;
}

static void Report(string label, List<double> samples)
{
    samples.Sort();
    var median = samples[samples.Count / 2];
    var p90 = samples[Math.Min(samples.Count - 1, (int)(samples.Count * 0.9))];

    Console.WriteLine($"{label}  (n={samples.Count})");
    Console.WriteLine($"  min {samples[0]:F0} ms | median {median:F0} ms | p90 {p90:F0} ms | max {samples[^1]:F0} ms");
}

static int ServiceStatus()
{
    var ping = IpcChannel.SendAsync(Ipc.PipeName, new IpcRequest(Ipc.Ping, "0"), CancellationToken.None)
        .GetAwaiter().GetResult();

    if (!ping.Ok)
    {
        Console.WriteLine($"服务未响应：{ping.Error}");
        Console.WriteLine("（没装服务、或服务没在跑时，Agent 会直接读写配置文件。）");
        return 1;
    }

    var envelope = IpcChannel.SendAsync(Ipc.PipeName, new IpcRequest(Ipc.GetConfig), CancellationToken.None)
        .GetAwaiter().GetResult();

    if (!envelope.Ok || envelope.Payload is null)
    {
        Console.WriteLine($"服务在线，但读取配置失败：{envelope.Error}");
        return 2;
    }

    var config = Ipc.Deserialize<ConfigEnvelope>(envelope.Payload);
    if (config is null)
    {
        Console.WriteLine("服务返回的配置无法解析。");
        return 2;
    }

    Console.WriteLine("服务在线。");
    Console.WriteLine($"配置版本 : {config.Version}");
    Console.WriteLine($"目标数   : {config.Config.Targets.Count}");
    Console.WriteLine($"策略数   : {config.Config.Policies.Count}");
    return 0;
}

static int State(string[] args, bool restore)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine($"{args[0]} needs a target: {args[0]} <pid|processName>");
        return 2;
    }

    using var enumerator = new WindowEnumerator();
    var window = enumerator.Enumerate().FirstOrDefault(w =>
        w.ProcessId.ToString() == args[1]
        || w.ProcessName.Contains(args[1], StringComparison.OrdinalIgnoreCase));

    if (window is null)
    {
        Console.WriteLine($"No window found for '{args[1]}'.");
        return 1;
    }

    if (restore)
    {
        WindowState.Restore(window.NativeHandle);
        Thread.Sleep(300);
        Console.WriteLine($"Restored {window.ProcessName} ({window.ProcessId}).");
    }

    Console.WriteLine($"Process   : {window.ProcessName} ({window.ProcessId})");
    Console.WriteLine($"Title     : {window.MainWindowTitle}");
    Console.WriteLine($"HWND      : 0x{window.NativeHandle:X}");
    Console.WriteLine($"Exists    : {WindowState.Exists(window.NativeHandle)}");
    Console.WriteLine($"Visible   : {WindowState.IsVisible(window.NativeHandle)}");
    Console.WriteLine($"Minimized : {WindowState.IsMinimized(window.NativeHandle)}");
    Console.WriteLine($"Maximized : {WindowState.IsMaximized(window.NativeHandle)}");
    Console.WriteLine($"Foreground: {window.IsForeground}");
    return 0;
}

static int RunMonitor(string configPath, string[] args)
{
    var config = ConfigStore.LoadOrDefault(configPath);
    bool dryRun = HasFlag(args, "--dry-run") || config.Ui.DryRun;
    bool once = HasFlag(args, "--once");
    bool verbose = HasFlag(args, "--verbose");
    int interval = int.TryParse(ArgValue(args, "--interval"), out var i) ? i : 1000;

    if (config.Targets.Count == 0)
    {
        Console.Error.WriteLine($"No targets configured in {configPath}. Run 'init-config' then edit it.");
        return 2;
    }

    var logDir = Environment.ExpandEnvironmentVariables(config.Logging.LogDirectory);
    using var audit = new JsonLinesAuditLog(Path.Combine(logDir, $"audit-{DateTime.Now:yyyyMMdd}.jsonl"));
    using var enumerator = new WindowEnumerator();
    using var notify = new NotifyAction();

    using var reader = new UiaContentReader();
    var engine = new RuleEngine(config.Schedules);
    var controller = new ActionController();
    var actions = new Dictionary<ActionType, IAction>
    {
        [ActionType.Notify] = notify,
        [ActionType.Log] = new LogAction(audit),
        [ActionType.Minimize] = new MinimizeWindowAction(),
        [ActionType.Close] = new CloseWindowAction(),
        [ActionType.Kill] = new KillProcessAction(),
    };

    var loop = new MonitorLoop(enumerator, reader, controller, actions, audit, config);

    Console.WriteLine($"Sentinel monitor{(dryRun ? " (dry-run)" : "")} — {config.Targets.Count} target(s), interval {interval}ms");
    Console.WriteLine($"Audit log: {logDir}");

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    do
    {
        var stopwatch = Stopwatch.StartNew();
        var outcomes = loop.Tick(DateTimeOffset.Now);
        stopwatch.Stop();

        if (verbose)
        {
            Console.WriteLine(
                $"[{DateTime.Now:HH:mm:ss}] tick took {stopwatch.ElapsedMilliseconds} ms " +
                $"({outcomes.Count} outcome(s))");
        }

        foreach (var outcome in outcomes)
        {
            Console.WriteLine(
                $"[{outcome.Now:HH:mm:ss}] {outcome.Target.DisplayName} | {outcome.Stage} | " +
                $"{outcome.Verdict.Reason ?? "allow"} | {outcome.Detail} | {outcome.Snapshot.Url}");
        }

        if (once)
            break;

        try
        {
            Task.Delay(interval, cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            break;
        }
    }
    while (!cts.IsCancellationRequested);

    return 0;
}

static bool HasFlag(string[] args, string flag) =>
    args.Contains(flag, StringComparer.OrdinalIgnoreCase);

static string? ArgValue(string[] args, string flag)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }
    return null;
}
