using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace Aemeath.Core.AI;

/// <summary>
/// OpenAI-compatible provider implementation.
/// </summary>
public sealed class OpenAIKernelMixin : KernelMixinBase
{
    private string? _apiKey;
    private string? _endpoint;
    private string _modelId = "gpt-4o";

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

        var httpClient = CreateOpenAIHttpClient();
        if (!string.IsNullOrEmpty(_endpoint))
        {
#pragma warning disable SKEXP0010
            builder.AddOpenAIChatCompletion(
                modelId: _modelId,
                apiKey: _apiKey!,
                endpoint: new Uri(_endpoint!),
                httpClient: httpClient);
#pragma warning restore SKEXP0010
        }
        else
        {
#pragma warning disable SKEXP0010
            builder.AddOpenAIChatCompletion(
                modelId: _modelId,
                apiKey: _apiKey!,
                httpClient: httpClient);
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
}
