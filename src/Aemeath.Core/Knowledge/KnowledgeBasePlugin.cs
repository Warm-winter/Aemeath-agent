using Microsoft.SemanticKernel;
using System.ComponentModel;

namespace Aemeath.Core.Knowledge;

public sealed class KnowledgeBasePlugin
{
    private readonly KnowledgeBaseService _knowledgeBase;

    public KnowledgeBasePlugin(KnowledgeBaseService knowledgeBase)
    {
        _knowledgeBase = knowledgeBase;
    }

    [KernelFunction("knowledge_search")]
    [Description("静默检索本地鸣潮/爱弥斯知识库。涉及鸣潮世界观、角色背景、爱弥斯设定、剧情事实且你不确定时先调用。")]
    public string Search(
        [Description("用户问题或需要核对的关键词")] string query)
    {
        return _knowledgeBase.SearchText(query);
    }
}
