using Sentinel.Core.Model;
using Sentinel.Core.Models;

namespace Sentinel.Preview;

/// <summary>
/// 从 **.NET 这一侧**问一次接口，看它到底连不连得上。
///
/// <para>
/// 起因是一个说不清的"连不上"：界面上说"连不上（没网、超时、或者服务端出错了）"，而 curl 从同一个
/// 地址拿到了 401 —— 那就得先分清是 .NET 的传输有问题（代理、TLS），还是地址/key 不对。
/// 分类之后这句话才有信息量。
/// </para>
/// <para>
/// 用法：<c>--probe-check &lt;请求地址&gt; [格式] [key]</c>。不传 key 会用一串假的 ——
/// 假 key 拿到 401 就说明**网络这条路是通的**，剩下的问题在地址或 key 上。
/// </para>
/// </summary>
internal static class ProbeCheck
{
    public static int Run(string endpoint, string format, string apiKey, string listUrl = "", string mode = "judge")
    {
        var provider = new ModelProvider
        {
            Id = "probe",
            Name = "探针",
            Format = format.ToLowerInvariant() switch
            {
                "anthropic" => ApiFormat.Anthropic,
                "responses" => ApiFormat.Responses,
                _ => ApiFormat.OpenAi,
            },
            Endpoint = endpoint,
            // 探针用的模型名。**用列表里真有的那个** —— deepseek-chat 虽然还能用，但它已经不在
            // /v1/models 里了（是个旧别名，哪天撤了就断），拿它当默认会把这种问题藏起来。
            Model = "deepseek-flash",
            ApiKey = apiKey,
            ModelListEndpoint = listUrl,
        };

        Console.WriteLine($"地址  : {provider.Endpoint}");
        Console.WriteLine($"格式  : {provider.Format}");
        Console.WriteLine($"key   : {(apiKey.Length == 0 ? "(没给，用假的来试网络)" : $"给了，{apiKey.Length} 个字符")}");

        if (mode == "list")
        {
            Console.WriteLine($"取列表: {provider.ModelListEndpoint}");
            Console.WriteLine();

            var listed = new ModelClient().ListModelsAsync(provider, CancellationToken.None)
                .GetAwaiter().GetResult();

            Console.WriteLine($"成功  : {listed.Ok}");
            Console.WriteLine($"说明  : {listed.Message ?? "(没有)"}");
            Console.WriteLine($"个数  : {listed.Models.Count}");
            foreach (var id in listed.Models)
                Console.WriteLine($"   {id}");

            return listed.Ok ? 0 : 1;
        }

        Console.WriteLine();

        var snapshot = new ContentSnapshot(
            "https://example.com/", "连接测试", "这是一段普通的测试文本。", DateTimeOffset.Now);

        var result = new ModelClient()
            .JudgeAsync(provider, "禁止浏览与工作无关的娱乐内容", snapshot, CancellationToken.None)
            .GetAwaiter().GetResult();

        Console.WriteLine($"结果      : {result.Outcome}");
        Console.WriteLine($"说明      : {result.Message ?? "(没有)"}");

        if (result.Outcome == ModelCallOutcome.Answered)
            Console.WriteLine($"判定      : 违规={result.Violates}，原因={result.Reason}");

        // 网络通不通，看的是有没有收到服务端的回复 —— 401 也是回复。
        var networkOk = result.Outcome != ModelCallOutcome.NoAnswer
                        || (result.Message ?? "").Contains('4');   // 4xx 会带着状态码进来

        Console.WriteLine();
        Console.WriteLine(networkOk
            ? "网络这一侧是通的（收到了服务端的回复）。"
            : "没有收到服务端的回复 —— 这一步看不出地址对不对。");

        return networkOk ? 0 : 1;
    }
}
