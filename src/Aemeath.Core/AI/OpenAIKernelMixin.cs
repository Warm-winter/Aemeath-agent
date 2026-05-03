using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.Extensions.DependencyInjection;

namespace Aemeath.Core.AI;

/// <summary>
/// OpenAI 兼容 Provider 实现
/// 支持 OpenAI、DeepSeek、Moonshot 等兼容 OpenAI API 的服务
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
            throw new InvalidOperationException("API Key 未设置");
        }

        // 如果有自定义端点，使用自定义配置
        if (!string.IsNullOrEmpty(_endpoint))
        {
#pragma warning disable SKEXP0010
            builder.AddOpenAIChatCompletion(
                modelId: _modelId,
                apiKey: _apiKey!,
                endpoint: new Uri(_endpoint!)
            );
#pragma warning restore SKEXP0010
        }
        else
        {
#pragma warning disable SKEXP0010
            builder.AddOpenAIChatCompletion(
                modelId: _modelId,
                apiKey: _apiKey!
            );
#pragma warning restore SKEXP0010
        }

        return builder.Build();
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
}
