namespace Aemeath.Core.MCP;

/// <summary>
/// 内置受保护 MCP 服务注册表。
///
/// 受保护的服务（项目自带、核心功能依赖）：
/// - 在设置界面对用户**隐藏**（不显示卡片）
/// - **不可删除、不可禁用**（底层守卫拒绝）
/// - 运行时**永远强制启用**（即使 Enabled 字段为 false 也加载）
///
/// 这样设计是为了防止小白用户误删 memory/filesystem 等核心服务，
/// 导致 AI 丧失记忆或文件读写能力。
/// </summary>
public static class McpBuiltinRegistry
{
    /// <summary>
    /// 受保护的服务 Id 集合（已 NormalizeId 规范化：小写）。
    /// 新增内置受保护服务时在此登记。
    /// </summary>
    public static readonly HashSet<string> ProtectedIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "memory",       // 项目自带的全局持久记忆 MCP
        "filesystem"    // 项目自带的文件读写 MCP
    };

    /// <summary>判断给定 Id 是否受保护。对 Id 做与 NormalizeId 一致的小写规范化。</summary>
    public static bool IsProtected(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        return ProtectedIds.Contains(id.Trim().ToLowerInvariant());
    }
}
