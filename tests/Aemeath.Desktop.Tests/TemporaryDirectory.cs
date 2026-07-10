namespace Aemeath.Desktop.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "Aemeath.Desktop.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        var tempRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
        var target = System.IO.Path.GetFullPath(Path);
        if (!target.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to delete non-temporary directory: {target}");
        }

        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }
    }
}
