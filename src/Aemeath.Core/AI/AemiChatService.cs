using Aemeath.Core.AI.Prompts;
using Aemeath.Core.Configuration;
using Aemeath.Core.Tools;
using Aemeath.Core.MCP;
using Aemeath.Core.Knowledge;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Aemeath.Core.AI;

public class AemiChatService : IChatService
{
    private readonly SettingsService _settingsService;
    private KernelMixinBase? _currentKernel;
    private string _currentProvider = "OpenAI";
    private readonly KnowledgeBaseService _knowledgeBase = new();
    public ToolConfirmationService ToolConfirmationService { get; } = new();
    private readonly Dictionary<string, Func<string, Task<string>>> _customTools = new();
    private readonly Dictionary<string, string> _customToolDescriptions = new();

    public string CurrentAssistantName => "小爱";
    public bool IsProcessing { get; private set; }
    public bool IsReady => _currentKernel is not null;
    public string? LastInitializationError { get; private set; }

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
        var systemPrompt = string.Equals(_settingsService.Current.SystemPrompt, "Professional", StringComparison.OrdinalIgnoreCase)
            ? AemiSystemPrompt.Professional
            : AemiSystemPrompt.Default;

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

        try
        {
            var mcpPlugin = new McpChatPlugin();
            var roots = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            mcpPlugin.SetupBuiltinMcpServers(_settingsService.Current.UvExecutablePath, _settingsService.Current.BunExecutablePath, roots);
            TryRegisterPlugin(mcpPlugin, "mcp");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MCP 工具准备失败: {ex}");
        }
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

    public async Task<string> SendMessageAsync(string message, CancellationToken cancellationToken = default)
    {
        if (_currentKernel is null)
        {
            throw new InvalidOperationException(GetUnavailableMessage());
        }

        IsProcessing = true;
        try
        {
            var response = await _currentKernel.SendMessageAsync(EnrichMessageWithKnowledge(message), cancellationToken);
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
        var response = await kernel.SendMessageAsync(message, cancellationToken);
        return FormatAemiResponse(response);
    }

    public async IAsyncEnumerable<string> SendMessageStreamingAsync(
        string message, 
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_currentKernel is null)
        {
            throw new InvalidOperationException(GetUnavailableMessage());
        }

        IsProcessing = true;
        try
        {
            await foreach (var chunk in _currentKernel.SendMessageStreamingAsync(EnrichMessageWithKnowledge(message), cancellationToken))
            {
                yield return chunk;
            }
        }
        finally
        {
            IsProcessing = false;
        }
    }

    public void ClearHistory()
    {
        _currentKernel?.ClearHistory();
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
