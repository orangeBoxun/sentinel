using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Sentinel.Core.Models;
using Sentinel.Platform;

// Sentinel Spike 0 — prove UIA can read title / URL / body from a target window.
// Answers: does an Electron app's embedded browser expose a UIA tree? Can we read URL & body?
//
// IMPORTANT: this resolves the window by its HWND (the same way UiaContentReader does).
// Application.GetMainWindow picks a different element for some apps and returns a tree with none
// of the page content — which produced a false "this app can't be read" answer for 豆包工作 while
// the production reader was reading it fine.

return Run(args);

static int Run(string[] args)
{
    if (args.Length == 0 || args[0] is "-h" or "--help")
    {
        Console.Error.WriteLine("Sentinel UIA Spike 0");
        Console.Error.WriteLine("  inspect <pid|name>       list EVERY descendant node with pattern support");
        Console.Error.WriteLine("  bench <pid|name>         time each step of a read, to find where the cost is");
        Console.Error.WriteLine("  select <pid|name> <label> [<label2>…]");
        Console.Error.WriteLine("                           activate UIA elements by name, then dump the result");
        Console.Error.WriteLine("  list [--all]             list top-level windows (--all: no filtering)");
        Console.Error.WriteLine("  <pid|processNameSubstr>  dump title + URL (+ optional body) for that window");
        Console.Error.WriteLine("    --body                 also read Document text (body)");
        Console.Error.WriteLine("    --tree <file>          dump full UIA tree to a file");
        Console.Error.WriteLine("    --depth <n>            max tree depth (default 8)");
        return 1;
    }

    using var automation = new UIA3Automation();

    if (args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        return ListWindows(automation, HasFlag(args, "--all"));

    if (args[0].Equals("inspect", StringComparison.OrdinalIgnoreCase))
        return Inspect(automation, args[1]);

    if (args[0].Equals("bench", StringComparison.OrdinalIgnoreCase))
        return Bench(automation, args[1]);

    if (args[0].Equals("select", StringComparison.OrdinalIgnoreCase))
        return SelectAndDump(automation, args);

    string target = args[0];
    bool wantBody = HasFlag(args, "--body");
    string? treeFile = ArgValue(args, "--tree");
    int depth = int.TryParse(ArgValue(args, "--depth") ?? "8", out var d) ? d : 8;

    var window = ResolveWindow(target);
    if (window is null)
    {
        Console.Error.WriteLine($"No window found for '{target}'. Run with 'list' first.");
        return 2;
    }

    var root = WindowElement(automation, window);
    if (root is null)
    {
        Console.Error.WriteLine($"Could not attach to {window.ProcessName} ({window.ProcessId}).");
        return 3;
    }

    DumpWindow(root, wantBody);

    if (treeFile is not null)
    {
        using var writer = new StreamWriter(treeFile);
        DumpTree(root, writer, depth);
        Console.WriteLine($"Tree written to {treeFile}");
    }

    return 0;
}

/// <summary>
/// Breaks a read into its steps and times each. Optimising without this is guesswork — the whole
/// reason the body read was slow stayed hidden until the parts were measured separately.
/// </summary>
static int Bench(UIA3Automation automation, string target)
{
    var window = ResolveWindow(target);
    if (window is null)
    {
        Console.Error.WriteLine($"No window found for '{target}'.");
        return 2;
    }

    var root = WindowElement(automation, window);
    if (root is null)
    {
        Console.Error.WriteLine("Could not attach.");
        return 3;
    }

    Console.WriteLine($"Target: {window.ProcessName} ({window.ProcessId})  \"{window.MainWindowTitle}\"");
    Console.WriteLine($"{"step",-36} {"median",8} {"min",7} {"max",7}");

    Measure("A. FromHandle 定位窗口", () => WindowElement(automation, window));
    Measure("B. FindAllDescendants() 全量", () => _ = root.FindAllDescendants());
    Measure("C. FindAllDescendants(Document)", () => _ = root.FindAllDescendants(cf => cf.ByControlType(ControlType.Document)));
    Measure("D. FindAllDescendants(Edit)", () => _ = root.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit)));

    AutomationElement[] docs = [];
    Measure("E. = C，然后逐个读 Name", () =>
    {
        docs = root.FindAllDescendants(cf => cf.ByControlType(ControlType.Document));
        foreach (var d in docs)
        {
            try { _ = d.Properties.Name.Value; } catch { }
        }
    });

    Measure("F. 再逐个读 Document 的 ValuePattern", () =>
    {
        foreach (var d in docs)
        {
            try { if (d.Patterns.Value.IsSupported) _ = d.Patterns.Value.Pattern.Value.Value; } catch { }
        }
    });

    Measure("G. 再逐个读 Document 的 TextPattern", () =>
    {
        foreach (var d in docs)
        {
            try
            {
                if (d.Patterns.Text.IsSupported)
                    _ = d.Patterns.Text.Pattern.DocumentRange.GetText(-1);
            }
            catch { }
        }
    });

    AutomationElement[] everything = [];
    Measure("H. 全量遍历 + 逐个读 ControlType", () =>
    {
        everything = root.FindAllDescendants();
        foreach (var e in everything)
        {
            try { _ = e.ControlType; } catch { }
        }
    });
    Console.WriteLine($"全量节点数: {everything.Length}");

    Console.WriteLine($"Document 节点数: {docs.Length}");
    return 0;
}

static void Measure(string label, Action action, int iterations = 8)
{
    try
    {
        action();   // warm up
    }
    catch (Exception ex)
    {
        Console.WriteLine($"{label,-36} failed: {ex.Message}");
        return;
    }

    var samples = new List<double>();
    for (var i = 0; i < iterations; i++)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            action();
        }
        catch
        {
            break;
        }
        stopwatch.Stop();
        samples.Add(stopwatch.Elapsed.TotalMilliseconds);
    }

    if (samples.Count == 0)
    {
        Console.WriteLine($"{label,-36} (no samples)");
        return;
    }

    samples.Sort();
    Console.WriteLine($"{label,-36} {samples[samples.Count / 2],8:F0} {samples[0],7:F0} {samples[^1],7:F0}");
}

/// <summary>
/// Drives a window through UIA: activates elements by their visible label, then prints what the
/// window now contains. Lets a UI be checked without a person clicking through it.
/// </summary>
static int SelectAndDump(UIA3Automation automation, string[] args)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("select needs a target and at least one label.");
        return 2;
    }

    var window = ResolveWindow(args[1]);
    if (window is null)
    {
        Console.Error.WriteLine($"No window found for '{args[1]}'.");
        return 2;
    }

    var root = WindowElement(automation, window);
    if (root is null)
    {
        Console.Error.WriteLine("Could not attach.");
        return 3;
    }

    for (var i = 2; i < args.Length; i++)
    {
        var label = args[i];
        var element = FindByName(root, label);
        if (element is null)
        {
            Console.Error.WriteLine($"-- 找不到元素: {label}");
            return 4;
        }

        var action = Activate(element);
        Console.WriteLine($"-- {label}: {action}");
        Thread.Sleep(600);

        // Re-resolve: activating something can rebuild the tree underneath us.
        root = WindowElement(automation, window);
        if (root is null)
            return 3;
    }

    Console.WriteLine();
    Console.WriteLine($"{"ControlType",-16} {"Val",-30}  Name");
    foreach (var element in root.FindAllDescendants())
    {
        var type = SafeStr(() => element.ControlType.ToString());
        if (type is not ("ComboBox" or "TabItem" or "Button" or "ListItem" or "Text" or "Edit" or "CheckBox"))
            continue;

        var name = SafeStr(() => element.Properties.Name.Value).Replace('\n', ' ').Replace('\r', ' ');
        if (name.Length > 60)
            name = name[..60] + "…";

        string value = "";
        try
        {
            if (element.Patterns.Value.IsSupported)
                value = element.Patterns.Value.Pattern.Value.Value ?? "";
        }
        catch { }

        // A WPF ComboBox exposes no ValuePattern; its content lives in the selection.
        if (value.Length == 0 && type == "ComboBox")
        {
            try
            {
                if (element.Patterns.Selection.IsSupported)
                {
                    value = string.Join(" | ", element.Patterns.Selection.Pattern.Selection.Value
                        .Select(e => SafeStr(() => e.Properties.Name.Value)));
                }
            }
            catch { }
        }

        if (value.Length > 28)
            value = value[..28] + "…";

        Console.WriteLine($"{Trunc(type, 16),-16} {value,-30}  {name}");
    }

    return 0;
}

static AutomationElement? FindByName(AutomationElement root, string label)
{
    // Exact match first, then a prefix match — labels in the tree are sometimes suffixed.
    foreach (var element in root.FindAllDescendants())
    {
        var name = SafeStr(() => element.Properties.Name.Value);
        if (string.Equals(name, label, StringComparison.Ordinal))
            return element;
    }

    foreach (var element in root.FindAllDescendants())
    {
        var name = SafeStr(() => element.Properties.Name.Value);
        if (name.Contains(label, StringComparison.Ordinal))
            return element;
    }

    return null;
}

static string Activate(AutomationElement element)
{
    try
    {
        if (element.Patterns.Toggle.IsSupported)
        {
            element.Patterns.Toggle.Pattern.Toggle();
            return "切换";
        }
    }
    catch { }

    try
    {
        if (element.Patterns.SelectionItem.IsSupported)
        {
            element.Patterns.SelectionItem.Pattern.Select();
            return "选中";
        }
    }
    catch { }

    try
    {
        if (element.Patterns.Invoke.IsSupported)
        {
            element.Patterns.Invoke.Pattern.Invoke();
            return "点击";
        }
    }
    catch { }

    try
    {
        if (element.Patterns.ExpandCollapse.IsSupported)
        {
            element.Patterns.ExpandCollapse.Pattern.Expand();
            return "展开";
        }
    }
    catch { }

    return "没有可用的操作（只读）";
}

static int Inspect(UIA3Automation automation, string target)
{
    var window = ResolveWindow(target);
    if (window is null)
    {
        Console.Error.WriteLine($"No window found for '{target}'.");
        return 2;
    }

    var root = WindowElement(automation, window);
    if (root is null)
    {
        Console.Error.WriteLine($"Could not attach to {window.ProcessName} ({window.ProcessId}).");
        return 3;
    }

    AutomationElement[] all;
    try
    {
        all = root.FindAllDescendants();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"FindAllDescendants failed: {ex.Message}");
        return 3;
    }

    Console.WriteLine($"Descendants: {all.Length}");
    Console.WriteLine($"{"ControlType",-12} {"Class",-26} {"Val",6} {"Txt",6}  Name / Value preview");

    foreach (var element in all)
    {
        var type = SafeStr(() => element.ControlType.ToString());
        var cls = SafeStr(() => element.Properties.ClassName.Value);

        int valLen = -1, textLen = -1;
        string? valuePreview = null;
        try
        {
            if (element.Patterns.Value.IsSupported)
            {
                var value = element.Patterns.Value.Pattern.Value.Value ?? "";
                valLen = value.Length;
                // Only interesting for the controls we actually read (address bars, inputs).
                if (type is "Edit" or "Document" && value.Length > 0)
                    valuePreview = Preview(value);
            }
        }
        catch { /* pattern raced away */ }

        try
        {
            if (element.Patterns.Text.IsSupported)
                textLen = (element.Patterns.Text.Pattern.DocumentRange.GetText(-1) ?? "").Length;
        }
        catch { /* not a text document */ }

        var name = SafeStr(() => element.Properties.Name.Value).Replace('\n', ' ').Replace('\r', ' ');
        if (name.Length > 60)
            name = name[..60] + "…";

        var detail = valuePreview is null ? name : $"{name}  ⟪{valuePreview}⟫";
        Console.WriteLine($"{Trunc(type, 12),-12} {Trunc(cls, 26),-26} {valLen,6} {textLen,6}  {detail}");
    }

    return 0;
}

static string Preview(string value)
{
    var flat = value.Replace('\n', ' ').Replace('\r', ' ');
    return flat.Length > 90 ? flat[..90] + "…" : flat;
}

static string Trunc(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

static int ListWindows(UIA3Automation automation, bool all)
{
    var desktop = automation.GetDesktop();

    // Default: only real top-level windows with a title. --all: every desktop child, unfiltered,
    // which is what reveals windows we fail to see (empty title, odd control type, off-desktop).
    var windows = all
        ? desktop.FindAllChildren()
        : desktop.FindAllChildren(cf => cf.ByControlType(ControlType.Window));

    Console.WriteLine($"{"PID",8}  {"Process",-20}  {"ControlType",-16}  Class / Name");
    foreach (var w in windows.OrderBy(x => SafeInt(() => x.Properties.ProcessId.Value)))
    {
        var name = SafeStr(() => w.Properties.Name.Value);
        if (!all && string.IsNullOrWhiteSpace(name))
            continue;

        var pid = SafeInt(() => w.Properties.ProcessId.Value);
        var type = SafeStr(() => w.ControlType.ToString());
        var cls = SafeStr(() => w.Properties.ClassName.Value);
        Console.WriteLine($"{pid,8}  {SafeProcName(pid),-20}  {type,-16}  {cls} / {name}");
    }

    return 0;
}

static void DumpWindow(AutomationElement window, bool wantBody)
{
    Console.WriteLine($"Window Title : {SafeStr(() => window.Properties.Name.Value)}");
    Console.WriteLine($"ControlType  : {SafeStr(() => window.ControlType.ToString())}");

    var edits = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit));
    Console.WriteLine($"Edit controls: {edits.Length}");
    foreach (var edit in edits)
    {
        if (!edit.Patterns.Value.IsSupported)
            continue;
        var value = SafeStr(() => edit.Patterns.Value.Pattern.Value.Value);
        if (string.IsNullOrWhiteSpace(value))
            continue;
        Console.WriteLine($"  [{(LooksLikeUrl(value) ? "URL" : "val")}] {value}");
    }

    if (wantBody)
    {
        var docs = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Document));
        Console.WriteLine($"Document nodes: {docs.Length}");
        foreach (var doc in docs)
        {
            if (!doc.Patterns.Text.IsSupported)
                continue;
            var text = SafeStr(() => doc.Patterns.Text.Pattern.DocumentRange.GetText(4096));
            Console.WriteLine("--- body begin ---");
            Console.WriteLine(text);
            Console.WriteLine("--- body end ---");
        }
    }
}

static void DumpTree(AutomationElement element, TextWriter writer, int maxDepth, int depth = 0)
{
    if (depth > maxDepth)
        return;

    var indent = new string(' ', depth * 2);
    var name = SafeStr(() => element.Properties.Name.Value);
    var type = SafeStr(() => element.ControlType.ToString());
    var aid = SafeStr(() => element.Properties.AutomationId.Value);
    var cls = SafeStr(() => element.Properties.ClassName.Value);
    writer.WriteLine($"{indent}<{type}> name='{name}' aid='{aid}' class='{cls}'");

    AutomationElement[] children;
    try
    {
        children = element.FindAllChildren();
    }
    catch
    {
        return;
    }

    foreach (var child in children)
        DumpTree(child, writer, maxDepth, depth + 1);
}

static WindowRef? ResolveWindow(string target)
{
    using var enumerator = new WindowEnumerator();
    return enumerator.Enumerate().FirstOrDefault(w =>
        w.ProcessId.ToString() == target
        || w.ProcessName.Contains(target, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Resolve by HWND, exactly as UiaContentReader does.</summary>
static AutomationElement? WindowElement(UIA3Automation automation, WindowRef window)
{
    if (window.NativeHandle != 0)
    {
        try
        {
            var element = automation.FromHandle((nint)window.NativeHandle);
            if (element is not null)
                return element;
        }
        catch
        {
            // stale handle; fall back below
        }
    }

    try
    {
        using var app = FlaUI.Core.Application.Attach(window.ProcessId);
        return app.GetMainWindow(automation, TimeSpan.FromMilliseconds(500));
    }
    catch
    {
        return null;
    }
}

static bool LooksLikeUrl(string s) =>
    s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
    || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
    || s.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
    || s.Contains("://", StringComparison.Ordinal);

static bool HasFlag(string[] args, string flag) => args.Contains(flag, StringComparer.OrdinalIgnoreCase);

static string? ArgValue(string[] args, string flag)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }
    return null;
}

static string SafeStr(Func<string?> getter)
{
    try { return getter() ?? ""; }
    catch { return ""; }
}

static int SafeInt(Func<int> getter)
{
    try { return getter(); }
    catch { return -1; }
}

static string SafeProcName(int pid)
{
    if (pid < 0) return "?";
    try { return Process.GetProcessById(pid).ProcessName; }
    catch { return "?"; }
}
