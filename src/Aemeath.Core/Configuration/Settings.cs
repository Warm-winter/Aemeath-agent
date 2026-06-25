namespace Aemeath.Core.Configuration;

public class Settings
{
    public string CurrentProvider { get; set; } = "OpenAI";
    public Dictionary<string, ApiKey> ApiKeys { get; set; } = new();
    public string DefaultModel { get; set; } = "gpt-4o";
    public bool EnableAlwaysOnTop { get; set; } = true;
    public bool MinimizeToTray { get; set; } = false;
    public bool EnableAutoStart { get; set; } = false;
    public bool IsPetFollowing { get; set; } = false;
    public int PetWidth { get; set; } = 125;
    public int PetHeight { get; set; } = 125;
    public string PetSizePreset { get; set; } = "normal";
    public double PetOpacity { get; set; } = 1.0;
    public bool EnablePetBubbles { get; set; } = true;
    public bool EnablePetIdleGreeting { get; set; } = true;
    public bool EnablePetEdgeSnap { get; set; } = true;
    public string SystemPrompt { get; set; } = "Default";
    public bool EnableParticleEffects { get; set; } = true;
    public bool EnableVoiceInput { get; set; } = false;
    public string? AzureSpeechKey { get; set; }
    public string? AzureSpeechRegion { get; set; }
    public string UserAvatarType { get; set; } = "male";
    public string? CustomUserAvatarPath { get; set; }
    public string? ChatBackgroundImagePath { get; set; }
    public string? UvExecutablePath { get; set; }
    public string? BunExecutablePath { get; set; }
    public string? McpServersConfigPath { get; set; }

    // ===== Mem0 长期记忆 =====
    /// <summary>Mem0 运行 venv 路径（含 python.exe）。由 Mem0DependencyService 创建。</summary>
    public string? Mem0PythonPath { get; set; }
    /// <summary>是否启用 Mem0 长期记忆编排（每轮 add + 发送前 search）。</summary>
    public bool Mem0Enabled { get; set; } = true;
    /// <summary>辅助视觉模型名（VisionPlugin 用，OpenAI 兼容，需支持图片输入）。</summary>
    public string? VisionModel { get; set; }
    /// <summary>辅助视觉模型 endpoint（OpenAI 兼容）。为空时与主 Provider 同源。</summary>
    public string? VisionEndpoint { get; set; }
    /// <summary>辅助视觉模型使用的提供商名（从已配置 Provider 中选）。为空则复用当前对话 Provider。</summary>
    public string? VisionProvider { get; set; }
    /// <summary>辅助视觉模型 API Key（DPAPI 加密）。为空时复用该提供商已保存的 key。</summary>
    public string? VisionApiKey { get; set; }
    /// <summary>嵌入模型名（Mem0 向量化用，OpenAI 兼容）。</summary>
    public string? Mem0EmbedModel { get; set; }
    /// <summary>嵌入维度（需与 Mem0EmbedModel 匹配）。</summary>
    public int Mem0EmbedDims { get; set; } = 1536;

    // ===== 电脑控制 =====
    /// <summary>电脑控制后端：auto（默认，优先轨A）| uia（轨A）| ufo（轨B）。</summary>
    public string ComputerControlBackend { get; set; } = "auto";
    /// <summary>UFO 专用 Python 解释器路径（轨 B，用户安装 UFO 后填入）。</summary>
    public string? UfoPythonPath { get; set; }
}
