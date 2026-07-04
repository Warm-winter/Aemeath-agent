using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace Aemeath.Core.AI;

/// <summary>
/// OpenAI-compatible provider implementation.
/// </summary>
public sealed class OpenAIKernelMixin : KernelMixinBase, IDisposable
{
    private string? _apiKey;
    private string? _endpoint;
    private string _modelId = "gpt-4o";
    // 持有 HttpClient 以便在重新初始化/释放时回收底层 socket（RES-001）。
    private HttpClient? _httpClient;
    private bool _disposed;

    public OpenAIKernelMixin(string systemPrompt) : base(systemPrompt)
    {
    }

    public override Task InitializeAsync(string apiKey, string? endpoint = null)
    {
        _apiKey = apiKey;
        _endpoint = endpoint;
        ProviderName = "OpenAI";

        var builder = Kernel.CreateBuilder();
        _kernel = BuildKernel(builder);

        _chatService = _kernel.Services.GetRequiredService<IChatCompletionService>();
        _isInitialized = true;
        return Task.CompletedTask;
    }

    protected override Kernel BuildKernel(IKernelBuilder builder)
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            throw new InvalidOperationException("API Key 未设置");
        }

        // 重新初始化前释放上一份 HttpClient，避免切换模型/提供商时累积 socket（RES-001）。
        _httpClient?.Dispose();
        _httpClient = CreateOpenAIHttpClient();
        if (!string.IsNullOrEmpty(_endpoint))
        {
#pragma warning disable SKEXP0010
            builder.AddOpenAIChatCompletion(
                modelId: _modelId,
                apiKey: _apiKey!,
                endpoint: new Uri(_endpoint!),
                httpClient: _httpClient);
#pragma warning restore SKEXP0010
        }
        else
        {
#pragma warning disable SKEXP0010
            builder.AddOpenAIChatCompletion(
                modelId: _modelId,
                apiKey: _apiKey!,
                httpClient: _httpClient);
#pragma warning restore SKEXP0010
        }

        return builder.Build();
    }

    private static HttpClient CreateOpenAIHttpClient()
    {
        var client = new HttpClient(new OpenAIResponseNormalizationHandler(new HttpClientHandler()))
        {
            // Cloudflare 524 = 100 秒源超时。默认 HttpClient.Timeout 也是 100 秒，两者竞争导致
            // 上传大图片时必定 524。设为 5 分钟给源站充足处理时间。
            Timeout = TimeSpan.FromMinutes(5)
        };
        // 设置默认 User-Agent，避免 Cloudflare Tunnel 端点因缺少 User-Agent 返回 HTTP 530。
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Aemeath-Agent/1.0");
        return client;
    }

    public void SetModel(string modelId)
    {
        _modelId = modelId;
        if (_isInitialized)
        {
            _isInitialized = false;
            InitializeAsync(_apiKey!, _endpoint).GetAwaiter().GetResult();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _httpClient?.Dispose();
        _httpClient = null;
    }
}
