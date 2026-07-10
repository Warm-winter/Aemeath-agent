using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Aemeath.Desktop.Views;

namespace Aemeath.Desktop.Tests;

public sealed class SettingsPanelLayoutTests
{
    [AvaloniaFact]
    public void SkillPanel_WideAndNarrowLayoutsKeepListHeightExplicitAndStable()
    {
        var panel = new SkillConfigPanel();
        var listPane = panel.FindControl<Border>("SkillListPane")!;
        var layout = panel.FindControl<Grid>("SkillLayoutGrid")!;
        var list = panel.FindControl<ListBox>("SkillListBox")!;

        panel.UpdateResponsiveLayout(640);
        Assert.Equal(252, listPane.Height);
        Assert.Equal(0, list.MinHeight);

        panel.UpdateResponsiveLayout(900);
        Assert.True(layout.RowDefinitions[0].Height.IsStar);
        Assert.True(double.IsNaN(listPane.Height));
        Assert.Equal(0, list.MinHeight);
    }
}
