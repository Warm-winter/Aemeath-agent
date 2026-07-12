using Aemeath.Core.AI;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Aemeath.Desktop.Tests;

public sealed class OpenAIResponseNormalizationHandlerTests
{
    [Fact]
    public async Task SseIntermediateNullFinishReason_RemainsNullUntilTerminalChunk()
    {
        const string body = "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"},\"finish_reason\":null}]}\n" +
                            "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"},\"finish_reason\":null}]}\n" +
                            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n" +
                            "data: [DONE]\n";

        var lines = await NormalizeSseAsync(body);

        using var first = ParseDataLine(lines[0]);
        using var second = ParseDataLine(lines[1]);
        using var terminal = ParseDataLine(lines[2]);
        Assert.Equal(JsonValueKind.Null, GetFinishReason(first).ValueKind);
        Assert.Equal(JsonValueKind.Null, GetFinishReason(second).ValueKind);
        Assert.Equal("stop", GetFinishReason(terminal).GetString());
    }

    [Fact]
    public async Task SseEmptyFinishReason_WithPayload_BecomesNullWithoutStoppingStream()
    {
        const string body = "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"},\"finish_reason\":\"\"}]}\n";

        var lines = await NormalizeSseAsync(body);

        using var document = ParseDataLine(lines[0]);
        Assert.Equal(JsonValueKind.Null, GetFinishReason(document).ValueKind);
    }

    [Fact]
    public async Task SseEmptyFinishReason_EmptyDelta_BecomesNullWithoutStoppingStream()
    {
        const string body = "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"\"}]}\n";

        var lines = await NormalizeSseAsync(body);

        using var document = ParseDataLine(lines[0]);
        Assert.Equal(JsonValueKind.Null, GetFinishReason(document).ValueKind);
    }

    [Fact]
    public async Task SseStreamingToolCalls_PreservesPartialIdentityNameAndArguments()
    {
        const string body =
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_123\",\"type\":\"function\",\"function\":{\"name\":\"computer_control\",\"arguments\":\"\"}}]},\"finish_reason\":null}]}\n" +
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"{\\\"task\\\":\\\"open WeChat\"}}]},\"finish_reason\":null}]}\n" +
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\" and send hello\\\"}\"}}]},\"finish_reason\":\"tool_calls\"}]}\n" +
            "data: [DONE]\n";

        var lines = await NormalizeSseAsync(body);

        using var first = ParseDataLine(lines[0]);
        using var second = ParseDataLine(lines[1]);
        using var terminal = ParseDataLine(lines[2]);
        var firstCall = GetToolCall(first);
        var secondCall = GetToolCall(second);
        var terminalCall = GetToolCall(terminal);

        Assert.Equal("call_123", firstCall.GetProperty("id").GetString());
        Assert.Equal("computer_control", firstCall.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal(string.Empty, firstCall.GetProperty("function").GetProperty("arguments").GetString());
        Assert.False(secondCall.TryGetProperty("id", out _));
        Assert.False(secondCall.GetProperty("function").TryGetProperty("name", out _));
        Assert.Equal("{\"task\":\"open WeChat", secondCall.GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal(" and send hello\"}", terminalCall.GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal("tool_calls", GetFinishReason(terminal).GetString());

        var reconstructedArguments = string.Concat(
            firstCall.GetProperty("function").GetProperty("arguments").GetString(),
            secondCall.GetProperty("function").GetProperty("arguments").GetString(),
            terminalCall.GetProperty("function").GetProperty("arguments").GetString());
        using var arguments = JsonDocument.Parse(reconstructedArguments);
        Assert.Equal("open WeChat and send hello", arguments.RootElement.GetProperty("task").GetString());
    }

    [Fact]
    public async Task SseReasoningOnlyChunk_DoesNotSynthesizeVisibleContent()
    {
        const string body = "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"private reasoning\"},\"finish_reason\":null}]}\n";

        var lines = await NormalizeSseAsync(body);

        using var document = ParseDataLine(lines[0]);
        var delta = document.RootElement.GetProperty("choices")[0].GetProperty("delta");
        Assert.Equal("private reasoning", delta.GetProperty("reasoning_content").GetString());
        Assert.False(delta.TryGetProperty("content", out _));
    }

    [Fact]
    public async Task JsonNullFinishReason_BecomesStop()
    {
        const string body = "{\"choices\":[{\"message\":{\"content\":\"hello\"},\"finish_reason\":null}]}";
        using var client = CreateClient(body, "application/json");
        var cancellationToken = TestContext.Current.CancellationToken;
        using var response = await client.PostAsync(
            "https://example.test/v1/chat/completions",
            new StringContent("{}"),
            cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

        Assert.Equal("stop", GetFinishReason(document).GetString());
    }

    private static async Task<string[]> NormalizeSseAsync(string body)
    {
        using var client = CreateClient(body, "text/event-stream");
        var cancellationToken = TestContext.Current.CancellationToken;
        using var response = await client.PostAsync(
            "https://example.test/v1/chat/completions",
            new StringContent("{}"),
            cancellationToken);
        return (await response.Content.ReadAsStringAsync(cancellationToken))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static HttpClient CreateClient(string body, string mediaType)
        => new(new OpenAIResponseNormalizationHandler(new StaticResponseHandler(body, mediaType)));

    private static JsonDocument ParseDataLine(string line)
        => JsonDocument.Parse(line["data: ".Length..]);

    private static JsonElement GetFinishReason(JsonDocument document)
        => document.RootElement.GetProperty("choices")[0].GetProperty("finish_reason");

    private static JsonElement GetToolCall(JsonDocument document)
        => document.RootElement
            .GetProperty("choices")[0]
            .GetProperty("delta")
            .GetProperty("tool_calls")[0];

    private sealed class StaticResponseHandler(string body, string mediaType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StringContent(body, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue(mediaType) { CharSet = "utf-8" };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
