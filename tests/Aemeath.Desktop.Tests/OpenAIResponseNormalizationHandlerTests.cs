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
    public async Task SseEmptyFinishReason_WithPayload_DoesNotBecomeStop()
    {
        const string body = "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"},\"finish_reason\":\"\"}]}\n";

        var lines = await NormalizeSseAsync(body);

        using var document = ParseDataLine(lines[0]);
        Assert.Equal(string.Empty, GetFinishReason(document).GetString());
    }

    [Fact]
    public async Task SseEmptyFinishReason_EmptyDelta_BecomesStop()
    {
        const string body = "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"\"}]}\n";

        var lines = await NormalizeSseAsync(body);

        using var document = ParseDataLine(lines[0]);
        Assert.Equal("stop", GetFinishReason(document).GetString());
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
