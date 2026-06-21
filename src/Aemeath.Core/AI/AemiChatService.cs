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

    /// <summary>Skill 服务（供 UI 面板管理复用同一实例）。</summary>
    public SkillService SkillService => _skillService;
    public ToolConfirmationService ToolConfirmationService { get; } = new();
    private readonly Dictionary<string, Func<string, Task<string>>> _customTools = new();
    private readonly Dictionary<string, string> _customToolDescriptions = new();
    private readonly McpRuntimeService _mcpRuntime = new();
    private readonly SemaphoreSlim _mcpReloadLock = new(1, 1);
    private CancellationTokenSource? _mcpReloadCts;
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

    private void RegisterTools()
    {
        if (_currentKernel is null) return;

        TryRegisterPlugin(new FileSystemPlugin(ToolConfirmationService), "filesystem");
        TryRegisterPlugin(new ScreenshotPlugin(), "screenshot");
        TryRegisterPlugin(new BrowserPlugin(ToolConfirmationService), "browser");
        TryRegisterPlugin(new ReminderPlugin(), "reminder");
        TryRegisterPlugin(new KnowledgeBasePlugin(_knowledgeBase), "knowledge");
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
            var response = await _currentKernel.SendMessageAsync(
                EnrichMessageWithKnowledge(message),
                attachments,
                cancellationToken);
            return FormatAemiResponse(response);
        }
        finally
        {
            IsProcessing = false;
        }
    }

    public async Task<string> SummarizeAsync(string message, CancellationToken cancellationToken = default)
    {
        var keyInfo = _settingsService.GetApiKeyInfo(_settingsService.Current.CurrentProvider);
        var apiKey = keyInfo?.Key;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(GetUnavailableMessage());
        }

        var model = string.IsNullOrWhiteSpace(keyInfo?.ModelId)
            ? _settingsService.Current.DefaultModel
            : keyInfo.ModelId;
        var kernel = new OpenAIKernelMixin("""
你是 Aemeath 的本地长期记忆整理器。
只负责把对话压缩成简洁、准确、可复用的长期记忆。
必须忠于原文，不要编造，不要加入项目外知识。
如果要求输出 JSON，只输出 JSON，不要 Markdown。
""");
        if (!string.IsNullOrWhiteSpace(model))
        {
            kernel.SetModel(model);
        }

        await kernel.InitializeAsync(apiKey, keyInfo?.Endpoint);
        try
        {
            var response = await kernel.SendMessageAsync(message, cancellationToken);
            return FormatAemiResponse(response);
        }
        finally
        {
            // 用完即释放临时 Kernel 的 HttpClient（RES-003）。
            kernel.Dispose();
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
            await foreach (var chunk in _currentKernel.SendMessageStreamingAsync(
                               EnrichMessageWithKnowledge(message),
                               attachments,
                               cancellationToken))
            {
                yield return chunk;
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

