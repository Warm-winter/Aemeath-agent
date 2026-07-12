using System.Net;
using System.Net.Http.Headers;
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
        // 请求方向规范化：移除 assistant 消息中 function.name 为空的 tool_call，
        // 避免历史记录里的畸形 tool_call 在下一轮被发回 API 时触发 HTTP 400
        if (request.Method == HttpMethod.Post && IsChatCompletionRequest(request))
        {
            await NormalizeRequestBodyAsync(request, cancellationToken);
        }

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

        // SSE 流式响应：包装为规范化 Content，逐行规范化 finish_reason / content / tool_calls
        if (mediaType?.Contains("event-stream", StringComparison.OrdinalIgnoreCase) == true)
        {
            response.Content = new SseNormalizingContent(response.Content);
            return response;
        }

        return response;
    }

    private static bool IsChatCompletionRequest(HttpRequestMessage request)
        => request.RequestUri?.AbsolutePath.Contains("chat/completions", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// 请求方向规范化：扫描 messages 数组中 assistant 消息的 tool_calls，
    /// 移除 function.name 为空的 tool_call，避免历史记录里的畸形 tool_call
    /// 在下一轮被发回 API 时触发 HTTP 400 "invalid tool_call function, function/name/arguments cannot be empty"。
    /// 仅在确实发生变更时替换请求体，避免无谓的内存分配。
    /// </summary>
    private static async Task NormalizeRequestBodyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is null)
        {
            return;
        }

        var body = await request.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        try
        {
            var node = JsonNode.Parse(body);
            if (node is not JsonObject root)
            {
                return;
            }
            if (root["messages"] is not JsonArray messages)
            {
                return;
            }

            var anyChanged = false;
            foreach (var msgNode in messages)
            {
                if (msgNode is not JsonObject msg)
                {
                    continue;
                }

                // 仅处理 assistant 消息（tool_calls 只出现在 assistant 消息中）
                if (msg["role"] is not JsonValue roleVal ||
                    !roleVal.TryGetValue<JsonElement>(out var roleEl) ||
                    roleEl.ValueKind != JsonValueKind.String ||
                    roleEl.GetString() != "assistant")
                {
                    continue;
                }

                if (msg["tool_calls"] is not JsonArray toolCalls)
                {
                    continue;
                }
                if (toolCalls.Count == 0)
                {
                    continue;
                }

                // 收集需要移除的索引（function.name 为空）
                var indicesToRemove = new List<int>();
                for (var i = 0; i < toolCalls.Count; i++)
                {
                    if (toolCalls[i] is not JsonObject toolCall)
                    {
                        continue;
                    }
                    if (toolCall["function"] is not JsonObject funcObj)
                    {
                        continue;
                    }
                    if (IsStringEmpty(funcObj["name"]))
                    {
                        indicesToRemove.Add(i);
                    }
                }

                // 从后往前移除，避免索引错位
                for (var j = indicesToRemove.Count - 1; j >= 0; j--)
                {
                    toolCalls.RemoveAt(indicesToRemove[j]);
                    anyChanged = true;
                }
            }

            // 仅在发生变更时替换请求体
            if (anyChanged)
            {
                var normalized = root.ToJsonString();
                var mediaType = request.Content.Headers.ContentType?.MediaType ?? "application/json";
                request.Content = new StringContent(normalized, Encoding.UTF8, mediaType);
            }
        }
        catch (JsonException)
        {
            // JSON 解析失败，保持请求体原样
        }
    }

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

            if (!NormalizeChatCompletionObject(root, isStreaming: false))
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

    private static bool NormalizeChatCompletionObject(JsonObject root, bool isStreaming)
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

            changed |= NormalizeChoiceObject(choice, isStreaming);
        }

        return changed;
    }

    private static bool NormalizeChoiceObject(JsonObject choice, bool isStreaming)
    {
        var changed = false;

        // Non-stream responses require a terminal reason. In SSE, null or missing means
        // the stream is still active and must not be rewritten to stop.
        var finishReason = choice["finish_reason"];
        var shouldNormalizeFinishReason = isStreaming
            ? IsExplicitEmptyFinishReason(finishReason) && !HasStreamingPayload(choice)
            : ShouldTreatAsEmptyFinishReason(finishReason);
        if (shouldNormalizeFinishReason)
        {
            choice["finish_reason"] = "stop";
            changed = true;
        }

        if (choice["message"] is JsonObject message)
        {
            changed |= NormalizeMessageObject(message);
        }

        if (choice["delta"] is JsonObject delta)
        {
            changed |= NormalizeMessageObject(delta);
        }

        return changed;
    }

    private static bool IsExplicitEmptyFinishReason(JsonNode? node)
    {
        return node is JsonValue value &&
               value.TryGetValue<JsonElement>(out var element) &&
               element.ValueKind == JsonValueKind.String &&
               string.IsNullOrWhiteSpace(element.GetString());
    }

    private static bool HasStreamingPayload(JsonObject choice)
    {
        return choice["delta"] is JsonObject { Count: > 0 } ||
               choice["message"] is JsonObject { Count: > 0 };
    }

    private static bool ShouldTreatAsEmptyFinishReason(JsonNode? node)
    {
        if (IsJsonNull(node))
        {
            return true;
        }

        if (node is JsonValue value &&
            value.TryGetValue<JsonElement>(out var element) &&
            element.ValueKind == JsonValueKind.String)
        {
            return string.IsNullOrWhiteSpace(element.GetString());
        }

        return false;
    }

    private static bool NormalizeMessageObject(JsonObject message)
    {
        var changed = false;
        if (message.TryGetPropertyValue("tool_calls", out var toolCalls) && IsJsonNull(toolCalls))
        {
            message["tool_calls"] = new JsonArray();
            changed = true;
        }
        else if (toolCalls is JsonArray toolCallsArray)
        {
            // 规范化 tool_calls：补全 id、移除 function.name 为空的项、补全空 arguments
            changed |= NormalizeToolCallsArray(toolCallsArray);
        }

        if (message.TryGetPropertyValue("content", out var content) && IsJsonNull(content))
        {
            message["content"] = string.Empty;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// 规范化 tool_calls 数组：
    /// - 空/缺失的 id 替换为 call_<8位hex>，保证唯一性（避免 SK 抛 ArgumentException）
    /// - function.name 为空时整体移除该 tool_call（API 会以 400 拒绝）
    /// - function.arguments 为空字符串时替换为 "{}"（API 要求非空）
    /// </summary>
    private static bool NormalizeToolCallsArray(JsonArray toolCalls)
    {
        var changed = false;
        // 收集需要移除的索引（function.name 为空），后续从后往前删除避免索引错位
        var indicesToRemove = new List<int>();

        for (var i = 0; i < toolCalls.Count; i++)
        {
            if (toolCalls[i] is not JsonObject toolCall)
            {
                continue;
            }

            var idNode = toolCall["id"];
            if (IsJsonNull(idNode))
            {
                toolCall["id"] = "call_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                changed = true;
            }
            else if (idNode is JsonValue value &&
                value.TryGetValue<JsonElement>(out var element) &&
                element.ValueKind == JsonValueKind.String)
            {
                var idStr = element.GetString();
                if (string.IsNullOrWhiteSpace(idStr))
                {
                    toolCall["id"] = "call_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    changed = true;
                }
            }

            // 规范化 function 字段
            if (toolCall["function"] is JsonObject funcObj)
            {
                // function.name 为空 → 标记移除整个 tool_call（无法仅靠补全 name 修复）
                if (IsStringEmpty(funcObj["name"]))
                {
                    indicesToRemove.Add(i);
                    changed = true;
                    continue;
                }

                // function.arguments 为空 → 替换为 "{}"（空 JSON 对象，避免 API 报 arguments cannot be empty）
                if (IsStringEmpty(funcObj["arguments"]))
                {
                    funcObj["arguments"] = "{}";
                    changed = true;
                }
            }
        }

        // 从后往前移除被标记的 tool_call，避免索引错位
        for (var j = indicesToRemove.Count - 1; j >= 0; j--)
        {
            toolCalls.RemoveAt(indicesToRemove[j]);
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

    /// <summary>
    /// 判断 JsonNode 是否为空字符串：null/缺失、或字符串类型的空白值。
    /// 非字符串类型（数字、对象、数组等）返回 false，不视为空。
    /// </summary>
    private static bool IsStringEmpty(JsonNode? node)
    {
        if (node is null)
        {
            return true;
        }

        if (node is JsonValue value &&
            value.TryGetValue<JsonElement>(out var element) &&
            element.ValueKind == JsonValueKind.String)
        {
            return string.IsNullOrWhiteSpace(element.GetString());
        }

        return false;
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

    /// <summary>
    /// 处理一行 SSE 数据。如果是 <c>data:</c> 前缀的 JSON，尝试规范化；其他行原样返回。
    /// </summary>
    /// <remarks>
    /// SSE 行格式兼容：
    /// - <c>data:</c> 与 <c>data: </c>（带或不带空格）
    /// - 注释行（<c>:</c> 开头）、<c>event:</c> / <c>id:</c> / <c>retry:</c> 等非 data 行原样透传
    /// - <c>data: [DONE]</c> 原样保留
    /// - JSON 解析失败时原样透传该行（不阻断流）
    /// </remarks>
    private static bool TryNormalizeDataLine(string line, out string normalized)
    {
        normalized = line;

        // 空行 / 注释行（: 开头）原样透传
        if (string.IsNullOrEmpty(line) || line[0] == ':')
        {
            return false;
        }

        // 兼容 data: 与 data: （带或不带空格）
        const string dataPrefix = "data:";
        if (line.Length < dataPrefix.Length ||
            !line.AsSpan(0, dataPrefix.Length).Equals(dataPrefix.AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var payloadStart = dataPrefix.Length;
        while (payloadStart < line.Length && line[payloadStart] == ' ')
        {
            payloadStart++;
        }

        var payload = line.AsSpan(payloadStart);

        // data: [DONE] 原样保留
        if (payload.SequenceEqual("[DONE]".AsSpan()))
        {
            return false;
        }

        // 空载荷原样保留
        if (payload.IsEmpty || payload.IsWhiteSpace())
        {
            return false;
        }

        try
        {
            var node = JsonNode.Parse(payload.ToString());
            if (node is not JsonObject root)
            {
                return false;
            }

            if (!NormalizeChatCompletionObject(root, isStreaming: true))
            {
                return false;
            }

            normalized = "data: " + root.ToJsonString();
            return true;
        }
        catch (JsonException)
        {
            // JSON 解析失败时原样透传，不阻断流
            return false;
        }
    }

    /// <summary>
    /// SSE 流式响应规范化 Content：逐行解析 <c>data:</c> 前缀的 JSON，
    /// 规范化 <c>finish_reason</c> / <c>content</c> / <c>tool_calls</c> 字段。
    /// </summary>
    private sealed class SseNormalizingContent : HttpContent
    {
        private readonly HttpContent _original;

        public SseNormalizingContent(HttpContent original)
        {
            _original = original;
            CopyOriginalHeaders(original);
        }

        private void CopyOriginalHeaders(HttpContent original)
        {
            foreach (var header in original.Headers)
            {
                if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(header.Key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            // 保留原始 Content-Type（含 charset），默认 text/event-stream; charset=utf-8
            var origCt = original.Headers.ContentType;
            Headers.ContentType = new MediaTypeHeaderValue(origCt?.MediaType ?? "text/event-stream")
            {
                CharSet = origCt?.CharSet ?? "utf-8"
            };
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            using var originalStream = await _original.ReadAsStreamAsync();
            using var reader = new StreamReader(originalStream, Encoding.UTF8, leaveOpen: false);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
            {
                NewLine = "\n",
                AutoFlush = true
            };

            while (true)
            {
                var line = await reader.ReadLineAsync();
                if (line is null)
                {
                    break;
                }

                if (TryNormalizeDataLine(line, out var normalized))
                {
                    await writer.WriteLineAsync(normalized);
                }
                else
                {
                    await writer.WriteLineAsync(line);
                }
            }

            await writer.FlushAsync();
        }

        protected override async Task<Stream> CreateContentReadStreamAsync()
        {
            var originalStream = await _original.ReadAsStreamAsync();
            return new SseNormalizingStream(originalStream);
        }

        protected override bool TryComputeLength(out long length)
        {
            // 流式长度未知
            length = 0;
            return false;
        }
    }

    /// <summary>
    /// 包装原始 SSE 流，逐行读取并规范化后输出。
    /// 保持流式特性：消费者按需 <see cref="Read"/> 时才从原始流读取下一行。
    /// </summary>
    private sealed class SseNormalizingStream : Stream
    {
        private readonly Stream _originalStream;
        private readonly StreamReader _reader;
        private readonly MemoryStream _lineBuffer = new();
        private bool _done;

        public SseNormalizingStream(Stream originalStream)
        {
            _originalStream = originalStream;
            _reader = new StreamReader(originalStream, Encoding.UTF8, leaveOpen: false);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            while (true)
            {
                // 缓冲区还有数据，直接返回
                if (_lineBuffer.Position < _lineBuffer.Length)
                {
                    return _lineBuffer.Read(buffer, offset, count);
                }

                if (_done)
                {
                    return 0;
                }

                // 缓冲区已空，读取下一行
                _lineBuffer.SetLength(0);
                _lineBuffer.Position = 0;

                var line = await _reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    _done = true;
                    continue;
                }

                var normalized = TryNormalizeDataLine(line, out var n) ? n : line;
                var bytes = Encoding.UTF8.GetBytes(normalized + "\n");
                await _lineBuffer.WriteAsync(bytes, cancellationToken);
                _lineBuffer.Position = 0;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            while (true)
            {
                if (_lineBuffer.Position < _lineBuffer.Length)
                {
                    return _lineBuffer.Read(buffer, offset, count);
                }

                if (_done)
                {
                    return 0;
                }

                _lineBuffer.SetLength(0);
                _lineBuffer.Position = 0;

                var line = _reader.ReadLine();
                if (line is null)
                {
                    _done = true;
                    continue;
                }

                var normalized = TryNormalizeDataLine(line, out var n) ? n : line;
                var bytes = Encoding.UTF8.GetBytes(normalized + "\n");
                _lineBuffer.Write(bytes, 0, bytes.Length);
                _lineBuffer.Position = 0;
            }
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _reader.Dispose();
                _lineBuffer.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
