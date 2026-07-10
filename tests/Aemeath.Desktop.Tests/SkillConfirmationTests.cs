using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Aemeath.Desktop.Views;

namespace Aemeath.Desktop.Tests;

public sealed class SkillConfirmationTests
{
    [AvaloniaFact]
    public async Task ConfirmUserSkillDeletionAsync_BothAccepted_RequiresTwoConfirmations()
    {
        var owner = new Window();
        var calls = 0;

        var result = await SkillConfigPanel.ConfirmUserSkillDeletionAsync(
            owner,
            "sample",
            (_, _, _, _) =>
            {
                calls++;
                return Task.FromResult(true);
            });

        Assert.True(result);
        Assert.Equal(2, calls);
    }

    [AvaloniaFact]
    public async Task ConfirmUserSkillDeletionAsync_FirstDenied_StopsImmediately()
    {
        var owner = new Window();
        var calls = 0;

        var result = await SkillConfigPanel.ConfirmUserSkillDeletionAsync(
            owner,
            "sample",
            (_, _, _, _) =>
            {
                calls++;
                return Task.FromResult(false);
            });

        Assert.False(result);
        Assert.Equal(1, calls);
    }
}
