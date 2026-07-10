using Aemeath.Core.Configuration;

namespace Aemeath.Desktop.Tests;

public sealed class SettingsServiceTests
{
    [Fact]
    public void Save_ReduceMotionAndSidebarPreference_RoundTrips()
    {
        using var temp = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temp.Path, "settings.json");
        var service = new SettingsService(path);
        service.Current.ReduceMotion = true;
        service.Current.IsChatSidebarOpen = false;

        service.Save();
        var reloaded = new SettingsService(path);

        Assert.True(reloaded.Current.ReduceMotion);
        Assert.False(reloaded.Current.IsChatSidebarOpen);
    }

    [Fact]
    public void Load_OldJsonWithoutNewFields_UsesCompatibleDefaults()
    {
        using var temp = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path, "{}");

        var service = new SettingsService(path);

        Assert.False(service.Current.ReduceMotion);
        Assert.True(service.Current.IsChatSidebarOpen);
    }
    [Fact]
    public void Save_SuccessfulWrite_RaisesSettingsChangedOnce()
    {
        using var temp = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temp.Path, "settings.json");
        var service = new SettingsService(path);
        var notificationCount = 0;
        service.SettingsChanged += () => notificationCount++;

        service.Current.ReduceMotion = true;
        service.Save();

        Assert.Equal(1, notificationCount);
    }

}
