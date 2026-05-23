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
}
