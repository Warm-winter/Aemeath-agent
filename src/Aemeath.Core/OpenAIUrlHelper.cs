namespace Aemeath.Core;

/// <summary>
/// OpenAI 兼容端点 URL 规范化工具。
/// 把用户填写的 endpoint 截取/补全到 /v1 末尾形式。
/// </summary>
public static class OpenAIUrlHelper
{
    private const string DefaultBaseUrl = "https://api.openai.com/v1";

    /// <summary>
    /// 规范化 endpoint 到 /v1 末尾。endpoint 为 null/空白时返回 null。
    /// 兼容用户填了完整路径（如 .../v1/chat/completions）的情况：截到 /v1 末尾。
    /// </summary>
    public static string? NormalizeBaseUrl(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return null;
        }

        var url = endpoint.Trim();
        var idx = url.IndexOf("/v1", StringComparison.OrdinalIgnoreCase);
        if (idx > 0)
        {
            url = url[..(idx + 3)];
        }
        else if (!url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            url = url.TrimEnd('/') + "/v1";
        }

        return url;
    }

    /// <summary>
    /// 同 <see cref="NormalizeBaseUrl"/>，但 endpoint 为 null/空白时返回 OpenAI 默认地址。
    /// </summary>
    public static string NormalizeBaseUrlWithDefault(string? endpoint)
        => NormalizeBaseUrl(endpoint) ?? DefaultBaseUrl;
}
