using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Sentinel.Core.Abstractions;
using Sentinel.Core.Models;

namespace Sentinel.Platform;

/// <summary>
/// Reads a window's title, URL and body text through the UIA tree.
/// This is what lets Sentinel see inside an Electron app (Codex, DeepSeek Harness, 豆包工作):
/// Chromium exposes the rendered page — text nodes and the document's URL — to UIA, so no TLS
/// decryption is needed.
///
/// "Document" is ambiguous: a tree usually holds the app shell's document AND the page inside the
/// embedded browser. See <see cref="SelectPage"/> for how the page is chosen.
///
/// Performance note: this deliberately queries by control type instead of walking every node.
/// A full walk plus a per-element <c>ControlType</c> read measured 1189 ms on a 689-node tree —
/// each property read is its own cross-process call. Two filtered queries plus a handful of
/// property reads cost roughly 600 ms and answer everything we need.
/// </summary>
public sealed class UiaContentReader : IContentReader, IDisposable
{
    private readonly UIA3Automation _automation = new();
    private readonly ReaderOptions _options;

    public UiaContentReader(ReaderOptions? options = null)
    {
        _options = options ?? new ReaderOptions();
    }

    public ReaderCapabilities Capabilities => new(CanReadUrl: true, CanReadTitle: true, CanReadBody: true);

    public bool IsAvailable() => true;

    public ContentSnapshot? Read(WindowRef window, ReaderOptions? options = null)
    {
        var opts = options ?? _options;

        var root = ResolveWindow(window);
        if (root is null)
            return null;

        var windowTitle = opts.ScanTitle ? SafeRead(() => root.Properties.Name.Value) : null;
        var addressUrl = opts.ScanUrl ? ReadAddressBarUrl(root) : null;
        var documents = FindDocuments(root, opts.ScanUrl);
        var page = SelectPage(documents, addressUrl);

        var body = opts.ScanBody && page.Element is not null
            ? TryText(page.Element, opts.MaxBodyChars)
            : null;

        // Prefer the document's own URL; the address bar is the fallback when it has none.
        var url = LooksLikeUrl(page.Url) ? page.Url : addressUrl;
        var title = !string.IsNullOrWhiteSpace(page.Name) ? page.Name : windowTitle;

        return new ContentSnapshot(url, title, body, DateTimeOffset.UtcNow);
    }

    private readonly record struct DocumentCandidate(int Index, AutomationElement? Element, string? Name, string? Url);

    private static List<DocumentCandidate> FindDocuments(AutomationElement root, bool scanUrl)
    {
        AutomationElement[] found;
        try
        {
            found = root.FindAllDescendants(cf => cf.ByControlType(ControlType.Document));
        }
        catch
        {
            return new List<DocumentCandidate>();
        }

        var documents = new List<DocumentCandidate>(found.Length);
        for (var i = 0; i < found.Length; i++)
        {
            var name = SafeRead(() => found[i].Properties.Name.Value);
            var url = scanUrl ? TryValue(found[i]) : null;
            documents.Add(new DocumentCandidate(i, found[i], name, url));
        }

        return documents;
    }

    /// <summary>
    /// Chromium exposes the loaded URL on the document itself, which beats the address bar: an
    /// omnibox may show a bare host ("baidu.com") or be empty. The app shell's document carries an
    /// internal chrome:// URL or none, so it is never mistaken for the page.
    /// </summary>
    private static DocumentCandidate SelectPage(List<DocumentCandidate> documents, string? addressUrl)
    {
        if (documents.Count == 0)
            return default;

        if (addressUrl is not null)
        {
            var addressHost = HostOf(addressUrl);
            foreach (var candidate in documents)
            {
                if (LooksLikeUrl(candidate.Url) && addressHost is not null && HostOf(candidate.Url!) == addressHost)
                    return candidate;
            }
        }

        // Otherwise the innermost document that carries an http(s) URL.
        for (var i = documents.Count - 1; i >= 0; i--)
        {
            if (LooksLikeUrl(documents[i].Url))
                return documents[i];
        }

        // No web page at all — an app target such as Codex showing a chat UI. Its document is the
        // content, and its Name is the real title.
        return documents[^1];
    }

    private static string? ReadAddressBarUrl(AutomationElement root)
    {
        AutomationElement[] edits;
        try
        {
            edits = root.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit));
        }
        catch
        {
            return null;
        }

        foreach (var edit in edits)
        {
            var value = TryValue(edit);
            if (LooksLikeUrl(value))
                return value;
        }

        return null;
    }

    private static string? HostOf(string url)
    {
        try
        {
            return new Uri(url).Host;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves the window to read. The enumerator already gave us the HWND, so go straight to it:
    /// Application.GetMainWindow has to search and, when its heuristic finds nothing, blocks for the
    /// entire timeout — which cost ~3 s per read and dominated the whole monitor loop.
    /// </summary>
    private AutomationElement? ResolveWindow(WindowRef window)
    {
        if (window.NativeHandle != 0)
        {
            try
            {
                var element = _automation.FromHandle((nint)window.NativeHandle);
                if (element is not null)
                    return element;
            }
            catch
            {
                // Stale handle: the window closed between enumeration and the read. Fall through.
            }
        }

        try
        {
            using var app = FlaUI.Core.Application.Attach(window.ProcessId);
            return app.GetMainWindow(_automation, TimeSpan.FromMilliseconds(500));
        }
        catch
        {
            return null;
        }
    }

    private static string? TryValue(AutomationElement element)
    {
        try
        {
            if (!element.Patterns.Value.IsSupported)
                return null;

            return element.Patterns.Value.Pattern.Value.Value;
        }
        catch
        {
            return null;   // access denied or control vanished
        }
    }

    private static string? TryText(AutomationElement element, int maxChars)
    {
        try
        {
            if (!element.Patterns.Text.IsSupported)
                return null;

            var text = element.Patterns.Text.Pattern.DocumentRange.GetText(maxChars);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;   // canvas / self-drawn UI has no TextPattern
        }
    }

    private static bool LooksLikeUrl(string? value) =>
        !string.IsNullOrEmpty(value)
        && (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("file://", StringComparison.OrdinalIgnoreCase));

    private static T? SafeRead<T>(Func<T?> getter) where T : class
    {
        try { return getter(); }
        catch { return null; }
    }

    public void Dispose() => _automation.Dispose();
}
