using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aemeath.Core.AI;

public sealed class OpenAIResponseNormalizationHandler : DelegatingHandler
{
    public OpenAIResponseNormalizationHandler(HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (response.Content is null || !IsChatCompletionRequest(request))
        {
            return response;
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (TryNormalizeJson(body, out var normalized))
            {
                response.Content = CreateReplacementContent(normalized, response.Content, "application/json");
            }

            return response;
        }

        return response;
    }

    private static bool IsChatCompletionRequest(HttpRequestMessage request)
        => request.RequestUri?.AbsolutePath.Contains("chat/completions", StringComparison.OrdinalIgnoreCase) == true;

    private static bool TryNormalizeJson(string body, out string normalized)
    {
        normalized = body;
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            var node = JsonNode.Parse(body);
            if (node is not JsonObject root)
            {
                return false;
            }

            if (!NormalizeChatCompletionObject(root))
            {
                return false;
            }

            normalized = root.ToJsonString();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool NormalizeChatCompletionObject(JsonObject root)
    {
        var changed = false;
        if (root["choices"] is not JsonArray choices)
        {
            return false;
        }

        foreach (var choiceNode in choices)
        {
            if (choiceNode is not JsonObject choice)
            {
                continue;
            }

            if (choice["message"] is JsonObject message)
            {
                changed |= NormalizeMessageObject(message);
            }

            if (choice["delta"] is JsonObject delta)
            {
                changed |= NormalizeMessageObject(delta);
            }
        }

        return changed;
    }

    private static bool NormalizeMessageObject(JsonObject message)
    {
        var changed = false;
        if (message.TryGetPropertyValue("tool_calls", out var toolCalls) && IsJsonNull(toolCalls))
        {
            message["tool_calls"] = new JsonArray();
            changed = true;
        }

        if (message.TryGetPropertyValue("content", out var content) && IsJsonNull(content))
        {
            message["content"] = string.Empty;
            changed = true;
        }

        return changed;
    }

    private static bool IsJsonNull(JsonNode? node)
    {
        if (node is null)
        {
            return true;
        }

        return node is JsonValue value &&
               value.TryGetValue<JsonElement>(out var element) &&
               element.ValueKind == JsonValueKind.Null;
    }

    private static HttpContent CreateReplacementContent(string body, HttpContent original, string defaultMediaType)
    {
        var mediaType = original.Headers.ContentType?.MediaType ?? defaultMediaType;
        var replacement = new StringContent(body, Encoding.UTF8, mediaType);
        if (!string.IsNullOrWhiteSpace(original.Headers.ContentType?.CharSet))
        {
            replacement.Headers.ContentType!.CharSet = original.Headers.ContentType!.CharSet;
        }

        foreach (var header in original.Headers)
        {
            if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return replacement;
    }
}
