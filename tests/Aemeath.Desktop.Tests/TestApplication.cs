using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(Aemeath.Desktop.Tests.TestApplication))]

namespace Aemeath.Desktop.Tests;

public static class TestApplication
{
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<HeadlessApplication>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }
}

internal sealed class HeadlessApplication : Application
{
    public override void Initialize()
    {
        var baseUri = new Uri("avares://Aemeath-agent/");
        Resources.MergedDictionaries.Add(new ResourceInclude(baseUri)
        {
            Source = new Uri("avares://Aemeath-agent/Styles/AemeathTheme.axaml")
        });
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(baseUri)
        {
            Source = new Uri("avares://Aemeath-agent/Styles/AemeathControls.axaml")
        });
    }
}
