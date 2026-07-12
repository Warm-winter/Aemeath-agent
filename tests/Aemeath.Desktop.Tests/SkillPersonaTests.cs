using Aemeath.Core.Skills;

namespace Aemeath.Desktop.Tests;

public sealed class SkillPersonaTests
{
    [Fact]
    public void BuiltinPersona_ExcludesResearchAndAuthorizationMetadata()
    {
        var package = new SkillLoader().LoadAll().Single(skill => skill.Manifest.IsBuiltin);
        var persona = package.PersonaPrompt;

        Assert.Contains("\u76f4\u63a5\u4ee5\u7231\u5f25\u65af\u7684\u8eab\u4efd\u56de\u5e94", persona, StringComparison.Ordinal);
        Assert.DoesNotContain("\u975e\u5b98\u65b9\u6388\u6743", persona, StringComparison.Ordinal);
        Assert.DoesNotContain("\u516c\u5f00\u4fe1\u606f\u84b8\u998f", persona, StringComparison.Ordinal);
        Assert.DoesNotContain("\u6b64 Skill \u57fa\u4e8e\u516c\u5f00\u8d44\u6599", persona, StringComparison.Ordinal);
        Assert.DoesNotContain("\u8c03\u7814\u6765\u6e90", persona, StringComparison.Ordinal);
        Assert.DoesNotContain("\u5f53\u524d\u84b8\u998f\u7ed3\u679c", persona, StringComparison.Ordinal);
        Assert.DoesNotContain("作为 AI", persona, StringComparison.Ordinal);
        Assert.DoesNotContain("<!-- persona-end -->", persona, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractPersonaBody_OptionalMarker_PreservesUserSkillCompatibility()
    {
        Assert.Equal("persona", SkillLoader.ExtractPersonaBody("persona\n<!-- persona-end -->\nresearch"));
        Assert.Equal("whole body", SkillLoader.ExtractPersonaBody("whole body"));
    }
}
