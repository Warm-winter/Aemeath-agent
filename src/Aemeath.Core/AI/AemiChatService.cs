using Aemeath.Core.AI.Prompts;
using Aemeath.Core.Configuration;
using Aemeath.Core.Tools;
using Aemeath.Core.MCP;
using Aemeath.Core.Knowledge;
using Aemeath.Core.Skills;
using System.Diagnostics;
using Microsoft.SemanticKernel;
using System.Text;
using System.Text.RegularExpressions;

namespace Aemeath.Core.AI;

public class AemiChatService : IChatService, IAsyncDisposable
{
    private readonly SettingsService _settingsService;
    private KernelMixinBase? _currentKernel;
    private string _currentProvider = "OpenAI";
    private readonly KnowledgeBaseService _knowledgeBase = new();
    private readonly SkillService _skillService = new();
    private VisionPlugin? _visionPlugin;

    /// <summary>Skill 服务（供 UI 面板管理复用同一实例）。</summary>
    public SkillService SkillService => _skillService;
    public ToolConfirmationService ToolConfirmationService { get; } = new();

    /// <summary>
    /// 根据当前 Provider 配置构造 Mem0 连接配置。
    /// Mem0 的 LLM/embedding 指向当前 Provider（OpenAI 兼容）；
    /// 视觉/嵌入模型可在设置里单独覆盖。
    /// 供桌面层注入到 <see cref="Memory.MemoryOrchestrator"/>。
    /// </summary>
    public Memory.Mem0ConnectionConfig? BuildMem0Config()
    {
        var s = _settingsService.Current;
        if (!s.Mem0Enabled)
        {
            return null;
        }

        var keyInfo = _settingsService.GetApiKeyInfo(_settingsService.Current.CurrentProvider);
        var apiKey = keyInfo?.Key;
        var endpoint = keyInfo?.Endpoint;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var llmModel = !string.IsNullOrWhiteSpace(keyInfo?.ModelId) ? keyInfo.ModelId! : s.DefaultModel;
        if (string.IsNullOrWhiteSpace(llmModel))
        {
            return null;
        }

        // 嵌入模型默认复用主 Provider；embedding 走同源 OpenAI 兼容端点
        var embedModel = !string.IsNullOrWhiteSpace(s.Mem0EmbedModel) ? s.Mem0EmbedModel! : "text-embedding-3-small";

        return new Memory.Mem0ConnectionConfig(
            LlmModel: llmModel,
            LlmBaseUrl: OpenAIUrlHelper.NormalizeBaseUrl(endpoint) ?? string.Empty,
            LlmApiKey: apiKey,
            EmbedModel: embedModel,
            EmbedBaseUrl: OpenAIUrlHelper.NormalizeBaseUrl(endpoint),
            EmbedApiKey: apiKey,
            EmbedDims: s.Mem0EmbedDims > 0 ? s.Mem0EmbedDims : 1536,
            VectorProvider: "qdrant");
    }

    /// <summary>构造视觉辅助配置（VisionPlugin 用）。</summary>
    public (string Model, string Endpoint, string ApiKey)? BuildVisionConfig()
    {
        var s = _settingsService.Current;
        // 视觉模型可指定独立提供商（与对话 Provider 打通：从已配置 Provider 中选）；
        // 未指定则复用当前对话 Provider。
        var provider = !string.IsNullOrWhiteSpace(s.VisionProvider) ? s.VisionProvider! : s.CurrentProvider;
        var keyInfo = _settingsService.GetApiKeyInfo(provider);
        var apiKey = string.IsNullOrWhiteSpace(s.VisionApiKey) ? keyInfo?.Key : s.VisionApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var model = !string.IsNullOrWhiteSpace(s.VisionModel) ? s.VisionModel! : keyInfo?.ModelId ?? s.DefaultModel;
        // endpoint：优先独立视觉端点，否则用所选 Provider 的端点
        var endpointRaw = !string.IsNullOrWhiteSpace(s.VisionEndpoint) ? s.VisionEndpoint : keyInfo?.Endpoint;
        var endpoint = OpenAIUrlHelper.NormalizeBaseUrl(endpointRaw);
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        return (model, endpoint ?? string.Empty, apiKey);
    }

    private readonly Dictionary<string, Func<string, Task<string>>> _customTools = new();
    private readonly Dictionary<string, string> _customToolDescriptions = new();
    private readonly McpRuntimeService _mcpRuntime = new();
    private readonly SemaphoreSlim _mcpReloadLock = new(1, 1);
    private CancellationTokenSource? _mcpReloadCts;
    // 持有 ReminderPlugin 引用以便订阅其 ReminderTriggered 事件并转发给上层
    private ReminderPlugin? _reminderPlugin;
    // 总预算调高到 200s，确保不小于单服务最长超时（HTTP 后台 150s + 余量）。
    // 即使总预算耗尽，BuildEnabledPluginAsync 内部每个服务有独立超时，已成功的工具仍会注册。
    private const int McpReloadTimeoutSeconds = 200;
    private Action<Action>? _uiThreadInvoker;

    public string CurrentAssistantName => "小爱";
    public bool IsProcessing { get; private set; }
    public bool IsReady => _currentKernel is not null;
    public string? LastInitializationError { get; private set; }
    public string McpStatus { get; private set; } = "未加载";
    public event EventHandler<string>? McpStatusChanged;

    /// <summary>
    /// 提醒触发事件（由 ReminderPlugin 转发）。UI 层订阅后可弹桌宠气泡/通知。
    /// 回调可能在 ThreadPool 线程触发，订阅者需自行切换到 UI 线程。
    /// </summary>
    public event EventHandler<string>? ReminderTriggered;

    public void SetUiThreadInvoker(Action<Action> invoker)
    {
        _uiThreadInvoker = invoker;
    }

    public AemiChatService(SettingsService settingsService)
    {
        _settingsService = settingsService;
        TryReloadFromSettings(out _);
    }

    public void ReloadFromSettings()
    {
        TryReloadFromSettings(out _);
    }

    public bool TryReloadFromSettings(out string? error)
    {
        try
        {
            InitializeFromSettings();
        }
        catch (Exception ex)
        {
            _currentKernel = null;
            LastInitializationError = BuildInitializationError(_currentProvider, ex.Message);
            Debug.WriteLine($"聊天服务初始化失败: {ex}");
        }

        error = LastInitializationError;
        return _currentKernel is not null;
    }

    private void InitializeFromSettings()
    {
        _currentProvider = _settingsService.Current.CurrentProvider;
        LastInitializationError = null;

        // 加载 Skill（人格定义 + 知识库），并把 skill 知识并入本地知识库
        _skillService.LoadAll();
        var persona = _skillService.GetPersonaPrompt();
        if (string.IsNullOrWhiteSpace(persona))
        {
            persona = AemiSystemPrompt.FallbackPersona;
        }
        var systemPrompt = persona + "\n\n" + AemiSystemPrompt.CapabilityBase;

        // skill 提供的知识条目并入知识库（与现有内置知识库互补）
        // 先清空旧的，避免禁用/删除的 skill 条目残留
        var skillEntries = _skillService.GetKnowledgeEntries();
        _knowledgeBase.ClearExtraEntries();
        if (skillEntries.Count > 0)
        {
            _knowledgeBase.AddEntries(skillEntries);
        }

        var defaultModel = _settingsService.Current.DefaultModel;
        var keyInfo = _settingsService.GetApiKeyInfo(_currentProvider);
        var apiKey = keyInfo?.Key;
        var endpoint = keyInfo?.Endpoint;
        var modelId = keyInfo?.ModelId;

        _currentProvider = InitializeOpenAI(
            providerName: _currentProvider,
            systemPrompt: systemPrompt,
            defaultModel: string.IsNullOrWhiteSpace(modelId) ? defaultModel : modelId,
            apiKey: apiKey,
            endpoint: endpoint);

        // 根据模型配置设置视觉能力：决定图片以 ImageContent 直接发送还是走 vision_analyze 工具
        if (_currentKernel is not null)
        {
            var activeModelId = string.IsNullOrWhiteSpace(modelId) ? defaultModel : modelId;
            _currentKernel.SupportsVision = ResolveVisionCapability(keyInfo?.Models, activeModelId);
        }

        RegisterTools();
    }

    private string InitializeOpenAI(string providerName, string systemPrompt, string? defaultModel, string? apiKey, string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _currentKernel = null;
            LastInitializationError = $"当前提供商 {providerName} 还没有配置 API Key。";
            return providerName;
        }

        if (string.IsNullOrWhiteSpace(defaultModel))
        {
            _currentKernel = null;
            LastInitializationError = $"当前提供商 {providerName} 还没有选择模型。";
            return providerName;
        }

        var kernel = new OpenAIKernelMixin(systemPrompt);
        kernel.SetModel(defaultModel);
        kernel.InitializeAsync(apiKey, endpoint).GetAwaiter().GetResult();
        _currentKernel = kernel;
        return providerName;
    }

    /// <summary>
    /// 判断当前模型是否支持图片输入（视觉能力）。
    /// 1. 优先查 ProviderModel.SupportsImageInput（由 /models API 探测）
    /// 2. 若为 null（未探测或 API 不返回），用模型名模式匹配兜底
    /// 3. 默认 true（现代模型大多支持视觉，且向后兼容）
    /// </summary>
    private static bool ResolveVisionCapability(List<ProviderModel>? models, string? modelId)
    {
        if (!string.IsNullOrWhiteSpace(modelId) && models is { Count: > 0 })
        {
            var match = models.FirstOrDefault(m =>
                string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));
            if (match?.SupportsImageInput is { } explicitValue)
            {
                return explicitValue;
            }
        }

        // 模型名兜底匹配：已知不支持视觉的模型
        if (!string.IsNullOrWhiteSpace(modelId))
        {
            var name = modelId.ToLowerInvariant();

            // 明确不支持视觉的模型
            if (name.Contains("gpt-3.5") || name.Contains("text-davinci") ||
                name.Contains("deepseek-r1") || name.Contains("o1-mini") ||
                name.Contains("o3-mini") || name.Contains("llama-3.1") ||
                name.Contains("qwen2.5-") || name.Contains("mistral") ||
                name.Contains("yi-") || name.Contains("baichuan"))
            {
                // 但要排除这些系列的 VL 版本
                if (!name.Contains("vl") && !name.Contains("vision") && !name.Contains("omni"))
                {
                    return false;
                }
            }

            // 明确支持视觉的模型
            if (name.Contains("gpt-4o") || name.Contains("gpt-4-vision") ||
                name.Contains("gpt-4-turbo") || name.Contains("claude-3") ||
                name.Contains("gemini") || name.Contains("qwen-vl") ||
                name.Contains("qwen2-vl") || name.Contains("qwen2.5-vl") ||
                name.Contains("glm-4v") || name.Contains("internvl") ||
                name.Contains("minicpm-v") || name.Contains("yi-vl") ||
                name.Contains("-vl") || name.Contains("vision") ||
                name.Contains("-omni") || name.Contains("llava") ||
                name.Contains("multimodal") || name.Contains("gemini-2"))
            {
                return true;
            }
        }

        // 默认 true：大多数现代模型支持视觉
        return true;
    }

    private void RegisterTools()
    {
        if (_currentKernel is null) return;

        TryRegisterPlugin(new FileSystemPlugin(ToolConfirmationService), "filesystem");
        TryRegisterPlugin(new ScreenshotPlugin(), "screenshot");
        TryRegisterPlugin(new BrowserPlugin(ToolConfirmationService), "browser");
        _reminderPlugin = new ReminderPlugin();
        _reminderPlugin.ReminderTriggered += (s, msg) => ReminderTriggered?.Invoke(s, msg);
        TryRegisterPlugin(_reminderPlugin, "reminder");
        TryRegisterPlugin(new KnowledgeBasePlugin(_knowledgeBase), "knowledge");
        _visionPlugin = new VisionPlugin(BuildVisionConfig);
        TryRegisterPlugin(_visionPlugin, "vision");
        TryRegisterPlugin(new ComputerControl.ComputerControlPlugin(
            BuildVisionConfig,
            ToolConfirmationService,
            () => _settingsService.Current.ComputerControlBackend,
            () => _settingsService.Current.UfoPythonPath), "computer_control");
        TryRegisterPlugin(new McpChatPlugin(), "mcp_local");
    }

    private void TryRegisterPlugin(object plugin, string name)
    {
        try
        {
            _currentKernel?.RegisterPlugin(plugin);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"工具插件注册失败({name}): {ex}");
        }
    }

    /// <summary>
    /// 预处理图片附件：当主模型不支持视觉时，自动调用 vision_analyze 获取描述并注入消息文本，
    /// 避免主模型幻觉式编造图片内容。已分析的图片附件从列表中移除。
    /// </summary>
    private async Task<(string processedMessage, IReadOnlyList<ChatAttachment> remainingAttachments)> PreprocessImageAttachmentsAsync(
        string message,
        IReadOnlyList<ChatAttachment>? attachments,
        CancellationToken cancellationToken)
    {
        // 视觉模型直接发送 ImageContent，无需预处理
        // 无视觉插件、无附件、或无图片附件时也跳过
        if (_currentKernel?.SupportsVision != false || _visionPlugin is null || attachments is null || attachments.Count == 0)
        {
            return (message, attachments ?? Array.Empty<ChatAttachment>());
        }

        var imageAttachments = attachments.Where(a => a.Kind == ChatAttachmentKind.Image).ToList();
        if (imageAttachments.Count == 0)
        {
            return (message, attachments);
        }

        // 视觉辅助未配置：回退到原有提示路径，附加说明
        if (BuildVisionConfig() is null)
        {
            var hint = message + "\n\n[系统提示] 视觉辅助模型未配置，无法自动分析图片。请配置视觉辅助模型后再发送图片。";
            return (hint, attachments);
        }

        var sb = new StringBuilder(message);
        var analyzedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var img in imageAttachments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Debug.WriteLine($"[vision-auto] 自动分析图片: {img.Name} ({img.Path})");
                var description = await _visionPlugin.AnalyzeImageAsync(img.Path, message);
                sb.AppendLine();
                sb.AppendLine($"[图片自动分析结果] {img.Name}:");
                sb.AppendLine(description);
                analyzedPaths.Add(img.Path);
            }
            catch (Exception ex)
            {
                // 单张图片分析失败不阻断其他图片处理
                Debug.WriteLine($"[vision-auto] 图片分析失败 {img.Name}: {ex.Message}");
                sb.AppendLine();
                sb.AppendLine($"[图片分析失败] {img.Name}: {ex.Message}");
                // 失败的图片仍从附件列表移除（已有失败提示），避免 KernelMixinBase 再生成重复提示
                analyzedPaths.Add(img.Path);
            }
        }

        // 从附件列表移除已分析的图片附件，保留非图片附件
        var remaining = attachments.Where(a => a.Kind != ChatAttachmentKind.Image || !analyzedPaths.Contains(a.Path)).ToList();
        return (sb.ToString(), remaining);
    }

    public Task<string> SendMessageAsync(string message, CancellationToken cancellationToken = default)
        => SendMessageAsync(message, null, cancellationToken);

    public async Task<string> SendMessageAsync(
        string message,
        IReadOnlyList<ChatAttachment>? attachments,
        CancellationToken cancellationToken = default)
    {
        if (_currentKernel is null)
        {
            throw new InvalidOperationException(GetUnavailableMessage());
        }

        IsProcessing = true;
        try
        {
            var (processedMessage, remainingAttachments) = await PreprocessImageAttachmentsAsync(message, attachments, cancellationToken);
            var response = await _currentKernel.SendMessageAsync(
                EnrichMessageWithKnowledge(processedMessage),
                remainingAttachments,
                cancellationToken);
            return FormatAemiResponse(response);
        }
        finally
        {
            IsProcessing = false;
        }
    }


    public IAsyncEnumerable<string> SendMessageStreamingAsync(
        string message,
        CancellationToken cancellationToken = default)
        => SendMessageStreamingAsync(message, null, cancellationToken);

    public async IAsyncEnumerable<string> SendMessageStreamingAsync(
        string message,
        IReadOnlyList<ChatAttachment>? attachments,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_currentKernel is null)
        {
            throw new InvalidOperationException(GetUnavailableMessage());
        }

        IsProcessing = true;
        try
        {
            var (processedMessage, remainingAttachments) = await PreprocessImageAttachmentsAsync(message, attachments, cancellationToken);
            var cleaner = new StreamingThinkCleaner();
            await foreach (var chunk in _currentKernel.SendMessageStreamingAsync(
                               EnrichMessageWithKnowledge(processedMessage),
                               remainingAttachments,
                               cancellationToken))
            {
                var safe = cleaner.Feed(chunk);
                if (!string.IsNullOrEmpty(safe))
                {
                    yield return safe;
                }
            }
            var tail = cleaner.Finish();
            if (!string.IsNullOrEmpty(tail))
            {
                yield return tail;
            }
        }
        finally
        {
            IsProcessing = false;
        }
    }

    public void ReloadMcpTools()
    {
        _ = ReloadMcpToolsAsync();
    }

    /// <summary>
    /// Skill 变更后调用：重新加载 skill（人格 + 知识库），重建 Kernel 系统提示词。
    /// 复用 TryReloadFromSettings 的重建流程（它会重新 LoadAll + 拼 persona + 重建 kernel + 重注入知识库）。
    /// </summary>
    public void ReloadSkills()
    {
        _skillService.Reload();
        TryReloadFromSettings(out _);
    }

    public async Task ReloadMcpToolsAsync(CancellationToken cancellationToken = default)
    {
        if (!await _mcpReloadLock.WaitAsync(0, cancellationToken))
        {
            SetMcpStatus("MCP 工具正在加载中");
            return;
        }

        _mcpReloadCts?.Cancel();
        try
        {
            await Task.Delay(100, CancellationToken.None);
        }
        catch
        {
        }
        _mcpReloadCts?.Dispose();
        _mcpReloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _mcpReloadCts.CancelAfter(TimeSpan.FromSeconds(McpReloadTimeoutSeconds));
        var token = _mcpReloadCts.Token;

        try
        {
            SetMcpStatus("MCP 工具正在后台加载");
            var plugin = await _mcpRuntime.BuildEnabledPluginAsync(token);
            // 不再因为 token 超时就丢弃已加载的 plugin。
            // BuildEnabledPluginAsync 内部每个服务有独立超时，即使整体超时，
            // 已成功加载的工具仍会被收集到 plugin 中并注册，保障其他工具正常运行。

            if (plugin is null)
            {
                SetMcpStatus("没有可用的外部 MCP 工具");
                if (_uiThreadInvoker is not null)
                {
                    _uiThreadInvoker(() => _currentKernel?.ReplacePlugin(null));
                }
                else
                {
                    _currentKernel?.ReplacePlugin(null);
                }
                return;
            }

            if (_uiThreadInvoker is not null)
            {
                _uiThreadInvoker(() => _currentKernel?.ReplacePlugin(plugin));
            }
            else
            {
                _currentKernel?.ReplacePlugin(plugin);
            }
            var failedCount = _mcpRuntime.ListServers().Count(s => s.Enabled && string.Equals(s.LastStatus, "error", StringComparison.OrdinalIgnoreCase));
            var loadedCount = _mcpRuntime.GetLoadedToolSummary().Count;
            SetMcpStatus(failedCount > 0
                ? $"MCP 工具已加载 {loadedCount} 个工具，{failedCount} 个服务失败"
                : "MCP 工具已加载");
        }
        catch (OperationCanceledException)
        {
            // 即使整体取消，也尝试注册已收集的工具（降级处理）
            SetMcpStatus("MCP 工具加载超时（部分工具可能仍可用）");
        }
        catch (Exception ex)
        {
            SetMcpStatus("MCP 工具加载失败：" + ex.Message);
            Debug.WriteLine($"MCP 工具后台加载失败: {ex}");
        }
        finally
        {
            _mcpReloadLock.Release();
        }
    }

    private void SetMcpStatus(string status)
    {
        McpStatus = status;
        McpStatusChanged?.Invoke(this, status);
    }

    public void ClearHistory()
    {
        _currentKernel?.ClearHistory();
    }

    /// <summary>释放持有的 Kernel、MCP 运行时、信号量等资源（RES-002）。</summary>
    public async ValueTask DisposeAsync()
    {
        _mcpReloadCts?.Cancel();
        _mcpReloadCts?.Dispose();
        _mcpReloadCts = null;

        try
        {
            await _mcpRuntime.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // 释放阶段忽略 MCP 运行时异常
        }

        (_currentKernel as IDisposable)?.Dispose();
        _currentKernel = null;

        _mcpReloadLock.Dispose();
    }

    public Task<bool> SwitchProviderAsync(string providerName, string apiKey, string? endpoint = null)
    {
        try
        {
            _settingsService.UpdateApiKey(providerName, apiKey, endpoint, _settingsService.Current.DefaultModel);
            _settingsService.Current.CurrentProvider = providerName;
            _settingsService.Save();
            
            return Task.FromResult(TryReloadFromSettings(out _));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"切换 Provider 失败: {ex.Message}");
            return Task.FromResult(false);
        }
    }

    public void RegisterTool(string toolName, string description, Func<string, Task<string>> handler)
    {
        _customTools[toolName] = handler;
        _customToolDescriptions[toolName] = description;
        if (_currentKernel is not null)
        {
            _currentKernel.RegisterPlugin(new DynamicToolPlugin(toolName, description, handler));
        }
    }

    private string FormatAemiResponse(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return response;
        }

        var cleaned = response;
        var endThink = cleaned.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (endThink >= 0)
        {
            cleaned = cleaned[(endThink + "</think>".Length)..];
        }

        cleaned = Regex.Replace(cleaned, "<think>[\\s\\S]*?</think>", string.Empty, RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, "(^|\\n)\\s*/think[\\s\\S]*?(\\n\\s*/endthink|$)", "$1", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, "(^|\\n)```think[\\s\\S]*?```", "$1", RegexOptions.IgnoreCase);
        return cleaned.Trim();
    }

    private string GetUnavailableMessage()
    {
        return string.IsNullOrWhiteSpace(LastInitializationError)
            ? "当前 AI 服务还没准备好，请先检查提供商、API Key、Endpoint 和模型配置。"
            : LastInitializationError;
    }

    private static string BuildInitializationError(string provider, string detail)
    {
        return $"当前提供商 {provider} 暂时不可用，请检查 API Key、Endpoint 和模型配置。详情：{detail}";
    }

    private string EnrichMessageWithKnowledge(string message)
    {
        var hits = _knowledgeBase.Search(message);
        var sb = new StringBuilder();

        sb.AppendLine("【本地知识库规则】");
        sb.AppendLine("如果用户问题涉及鸣潮世界观、爱弥斯、角色背景、剧情事实，必须优先依据本地知识库。");
        sb.AppendLine("如果下方没有自动命中资料，但你判断问题可能隐晦涉及这些内容，请静默调用 knowledge_search 检索后再回答。");
        sb.AppendLine("若本地资料未覆盖具体事实，请明确说明资料不足，不要编造。");
        sb.AppendLine();

        if (hits.Count > 0)
        {
            sb.AppendLine("【本地知识库自动命中】");
            sb.AppendLine(_knowledgeBase.FormatForPrompt(hits));
            sb.AppendLine();
        }

        // 动态注入已加载的 MCP 工具清单，让模型知道可以调用哪些外部工具
        var mcpTools = _mcpRuntime.GetLoadedToolSummary();
        if (mcpTools.Count > 0)
        {
            sb.AppendLine("【外部 MCP 工具】");
            sb.AppendLine("你当前可以调用以下外部工具（这些工具会直接返回结果文本，不需要打开浏览器）：");
            foreach (var (functionName, description) in mcpTools)
            {
                sb.AppendLine($"- {functionName}：{description}");
            }
            sb.AppendLine();
            sb.AppendLine("【强制规则】");
            sb.AppendLine("1. 在说「不知道」「没有收录」「资料不足」之前，必须先调用上述 MCP 工具尝试查询。");
            sb.AppendLine("2. 需要搜索信息时，必须使用上述 MCP 搜索类工具，禁止使用 search_web 打开浏览器。");
            sb.AppendLine("3. MCP 工具会直接把搜索结果返回给你，你可以基于结果直接回答用户，无需让用户自己去浏览器查看。");
            sb.AppendLine("4. 只有当 MCP 工具也查不到时，才能回答资料不足。直接说不知道而不调用工具是错误的。");
            sb.AppendLine();
        }

        sb.AppendLine("【用户请求】");
        sb.Append(message);
        return sb.ToString();
    }
}

internal sealed class DynamicToolPlugin
{
    private readonly Func<string, Task<string>> _handler;
    public string Name { get; }
    public string Description { get; }

    public DynamicToolPlugin(string name, string description, Func<string, Task<string>> handler)
    {
        Name = name;
        Description = description;
        _handler = handler;
    }

    [Microsoft.SemanticKernel.KernelFunction]
    public Task<string> Invoke(string input)
    {
        return _handler(input);
    }
}

