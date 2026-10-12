namespace Sentinel.Core.Models;

/// <summary>Which part of a content snapshot a rule matches against.</summary>
public enum MatchField
{
    Url,
    Title,
    Body,
}

/// <summary>
/// How a rule pattern is interpreted.
/// Named MatchKind (not MatchType) to avoid colliding with System.IO.MatchType, which
/// ImplicitUsings pulls in. The JSON property stays <c>Match.Type</c> with the same string values.
/// </summary>
public enum MatchKind
{
    /// <summary>Plain substring match (case sensitivity per rule).</summary>
    Keyword,

    /// <summary>Regular expression match.</summary>
    Regex,

    /// <summary>Whitelist entry: a match yields Allow instead of Block.</summary>
    Whitelist,

    /// <summary>
    /// 交给大模型判。规则的 Patterns 存的是"我要禁止自己看什么"的自然语言描述，
    /// 由模型返回 [是否违规, 原因]。
    ///
    /// <para>
    /// 和关键词/正则并列，不是它们的上级：模型能判"看完这段我是不是在浪费时间"这种说不成
    /// 正则的事，但它慢、要花钱、要联网，而且会把内容发到外面去。三条路各有各的适用面。
    /// </para>
    /// </summary>
    Model,
}

/// <summary>
/// 大模型服务商的接口形状。三家发的请求体长得不一样，统一的只有"发一段话、拿一段话回来"。
///
/// <para>
/// 名字按各家自己叫的写：OpenAI 的 Chat Completions、OpenAI 的 Responses、Anthropic 的 Messages。
/// 混着叫会让人对着文档配不上。
/// </para>
/// </summary>
public enum ApiFormat
{
    /// <summary>OpenAI /chat/completions —— 大多数兼容服务（DeepSeek、通义、本地 llama.cpp 等）都是这个。</summary>
    OpenAi,

    /// <summary>OpenAI /responses —— 新的那个接口。</summary>
    Responses,

    /// <summary>Anthropic /v1/messages。</summary>
    Anthropic,
}

/// <summary>Action a policy can perform when a rule is enforced.</summary>
public enum ActionType
{
    Notify,
    Minimize,
    Close,
    Kill,
    Log,
}

public enum ProtectionMode
{
    None,
    Password,
    DelayUnlock,
}

/// <summary>How a notification reaches the user.</summary>
public enum AlertStyle
{
    /// <summary>A tray balloon: unobtrusive, and easy to ignore or miss entirely.</summary>
    Balloon,

    /// <summary>
    /// A full-screen overlay that covers everything until acknowledged. It interrupts rather than
    /// informs — that is the point when the trouble is not noticing.
    /// </summary>
    Fullscreen,
}

/// <summary>Which part of a policy a tick has reached. Lives here so the action model can carry it.</summary>
public enum ActionStage
{
    /// <summary>Nothing to do this tick.</summary>
    None,

    /// <summary>Only notify — the grace period is running.</summary>
    Notify,

    /// <summary>Run the policy's enforcement actions.</summary>
    Enforce,
}
