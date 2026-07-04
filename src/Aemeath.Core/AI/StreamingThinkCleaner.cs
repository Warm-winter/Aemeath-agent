using System.Text;
using System.Text.RegularExpressions;

namespace Aemeath.Core.AI;

/// <summary>
/// 流式输出时增量清洗 think 标签。
///
/// 处理跨分片的 <see cref="ThinkOpenTag"/>...<see cref="ThinkCloseTag"/> 块：
/// - 维护内部缓冲区与 <see cref="_insideThink"/> 状态
/// - <see cref="Feed"/> 每来一片就尝试提取安全文本返回，未闭合的部分留在缓冲区
/// - 处理分片边界上的部分标签前缀（如末尾 "<thi"），保留到下一片补全
/// - <see cref="Finish"/> 对剩余缓冲做一次完整清洗（覆盖 &lt;think&gt;、/think、```think 等所有格式）
///
/// 与 <see cref="AemiChatService.FormatAemiResponse"/> 行为对齐：流式输出不应向用户暴露原始 think 块。
/// </summary>
internal sealed class StreamingThinkCleaner
{
    private const string ThinkOpenTag = "<think>";
    private const string ThinkCloseTag = "</think>";
    private static readonly Regex ThinkBlockRegex = new("<think>[\\s\\S]*?</think>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SlashThinkBlockRegex = new("(^|\\n)\\s*/think[\\s\\S]*?(\\n\\s*/endthink|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CodeThinkBlockRegex = new("(^|\\n)```think[\\s\\S]*?```", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly StringBuilder _buffer = new();
    private bool _insideThink;

    /// <summary>
    /// 喂入一片流式输出，返回本次可安全向 UI 输出的文本（可能为空）。
    /// 不安全的文本（处于未闭合 think 块内、或可能是部分标签前缀）会保留在内部缓冲区等待后续分片。
    /// </summary>
    public string Feed(string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
        {
            return string.Empty;
        }

        _buffer.Append(chunk);
        return ExtractSafeText();
    }

    /// <summary>
    /// 流结束时调用：对剩余缓冲做一次完整清洗，覆盖所有 think 格式。
    /// 若仍处于未闭合的 think 块内，丢弃全部剩余内容（避免泄漏 think 文本）。
    /// </summary>
    public string Finish()
    {
        if (_insideThink)
        {
            // 未闭合的 think 块：丢弃全部内容（与非流式 FormatAemiResponse 处理未闭合块的行为一致）
            _buffer.Clear();
            return string.Empty;
        }

        var remaining = _buffer.ToString();
        _buffer.Clear();
        return FormatFinal(remaining);
    }

    private string ExtractSafeText()
    {
        var result = new StringBuilder();
        var input = _buffer.ToString();
        var pos = 0;

        while (pos < input.Length)
        {
            if (!_insideThink)
            {
                var thinkStart = input.IndexOf(ThinkOpenTag, pos, StringComparison.OrdinalIgnoreCase);
                if (thinkStart < 0)
                {
                    // 没有完整的 <think>，检查末尾是否有部分标签前缀（如 "<thi"）
                    var partialLen = FindPartialTagPrefix(input, pos, ThinkOpenTag);
                    if (partialLen > 0)
                    {
                        // 输出部分前缀之前的内容，保留部分前缀在缓冲区
                        var safeEnd = input.Length - partialLen;
                        if (safeEnd > pos)
                        {
                            result.Append(input.AsSpan(pos, safeEnd - pos));
                        }
                        _buffer.Clear();
                        _buffer.Append(input.AsSpan(safeEnd, partialLen));
                        return result.ToString();
                    }

                    // 没有部分前缀，输出全部剩余
                    result.Append(input.AsSpan(pos));
                    _buffer.Clear();
                    return result.ToString();
                }

                // 输出 <think> 之前的内容
                if (thinkStart > pos)
                {
                    result.Append(input.AsSpan(pos, thinkStart - pos));
                }

                pos = thinkStart + ThinkOpenTag.Length;
                _insideThink = true;
            }
            else
            {
                var endIdx = input.IndexOf(ThinkCloseTag, pos, StringComparison.OrdinalIgnoreCase);
                if (endIdx < 0)
                {
                    // </think> 未出现，检查末尾是否有部分标签前缀
                    var partialLen = FindPartialTagPrefix(input, pos, ThinkCloseTag);
                    if (partialLen > 0)
                    {
                        // 保留部分前缀在缓冲区（think 块内的内容丢弃）
                        _buffer.Clear();
                        _buffer.Append(input.AsSpan(input.Length - partialLen, partialLen));
                        return result.ToString();
                    }

                    // 没有部分前缀，缓冲全部剩余（仍在 think 内，不输出）
                    _buffer.Clear();
                    return result.ToString();
                }

                // 跳过 </think>，切回非 think 态
                pos = endIdx + ThinkCloseTag.Length;
                _insideThink = false;
            }
        }

        // 整个 input 已处理完且未留下尾巴
        _buffer.Clear();
        return result.ToString();
    }

    /// <summary>
    /// 检查 input 末尾（从 from 位置之后）是否包含 tag 的部分前缀。
    /// 例如 input 末尾是 "<thi"，tag 是 "<think>"，则返回 4（保留 "<thi" 在缓冲区）。
    /// </summary>
    private static int FindPartialTagPrefix(string input, int from, string tag)
    {
        var maxLen = Math.Min(tag.Length - 1, input.Length - from);
        for (var len = maxLen; len > 0; len--)
        {
            var candidate = input.AsSpan(input.Length - len, len);
            if (tag.AsSpan(0, len).Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return len;
            }
        }

        return 0;
    }

    /// <summary>
    /// 对最终剩余文本做完整清洗，逻辑与 <see cref="AemiChatService.FormatAemiResponse"/> 等价。
    /// 覆盖 &lt;think&gt;...&lt;/think&gt;、/think.../endthink、```think...``` 三种格式。
    /// </summary>
    private static string FormatFinal(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        // 处理残留的 </think>（无配对 <think>）：丢弃 </think> 及其之前的全部内容
        var endThink = text.IndexOf(ThinkCloseTag, StringComparison.OrdinalIgnoreCase);
        if (endThink >= 0)
        {
            text = text[(endThink + ThinkCloseTag.Length)..];
        }

        text = ThinkBlockRegex.Replace(text, string.Empty);
        text = SlashThinkBlockRegex.Replace(text, "$1");
        text = CodeThinkBlockRegex.Replace(text, "$1");
        return text;
    }
}
