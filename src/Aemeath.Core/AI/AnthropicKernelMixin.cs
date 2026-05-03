using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.Extensions.DependencyInjection;

namespace Aemeath.Core.AI;

/// <summary>
/// Anthropic Claude Provider 实现
/// </summary>
public sealed class AnthropicKernelMixin : KernelMixinBase
{
    private string _modelId = "claude-3-5-sonnet-20241022";

    public AnthropicKernelMixin(string systemPrompt) : base(systemPrompt)
    {
    }

    public override Task InitializeAsync(string apiKey, string? endpoint = null)
    {
        throw new NotSupportedException("当前版本未集成官方 Anthropic Connector，请在设置中使用 OpenAI 兼容服务或切换到 OpenAI Provider。");
    }

    protected override Kernel BuildKernel(IKernelBuilder builder)
    {
        throw new NotSupportedException("Anthropic connector is not configured in this build.");
    }

    public void SetModel(string modelId)
    {
        _modelId = modelId;
    }
}
