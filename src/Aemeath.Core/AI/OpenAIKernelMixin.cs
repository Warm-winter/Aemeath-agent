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
    // \u6301\u6709 HttpClient \u4ee5\u4fbf\u5728\u91cd\u65b0\u521d\u59cb\u5316/\u91ca\u653e\u65f6\u56de\u6536\u5e95\u5c42 socket\uff08RES-001\uff09\u3002
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
            throw new InvalidOperationException("API Key \u672a\u8bbe\u7f6e");
        }

        // \u91cd\u65b0\u521d\u59cb\u5316\u524d\u91ca\u653e\u4e0a\u4e00\u4efd HttpClient\uff0c\u907f\u514d\u5207\u6362\u6a21\u578b/\u63d0\u4f9b\u5546\u65f6\u7d2f\u79ef socket\uff08RES-001\uff09\u3002
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
        => new(new OpenAIResponseNormalizationHandler(new HttpClientHandler()));

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
