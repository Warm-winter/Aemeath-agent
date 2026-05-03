using System.Text;

namespace Aemeath.Desktop.Services;

public static class AppLogger
{
    private static readonly object Sync = new();
    private static string _logDir = string.Empty;

    public static void Initialize()
    {
        if (!string.IsNullOrWhiteSpace(_logDir))
        {
            return;
        }

        try
        {
            _logDir = Path.Combine(AppContext.BaseDirectory, "log");
            Directory.CreateDirectory(_logDir);
        }
        catch
        {
            _logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Aemeath", "log");
            Directory.CreateDirectory(_logDir);
        }

        Info("logger", "logger initialized");
    }

    public static void Info(string source, string message) => Write("INFO", source, message);
    public static void Error(string source, string message, Exception? ex = null)
        => Write("ERROR", source, ex is null ? message : message + " | " + ex);

    private static void Write(string level, string source, string message)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_logDir))
            {
                Initialize();
            }

            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{source}] {message}{Environment.NewLine}";
            var file = Path.Combine(_logDir, $"{DateTimeOffset.Now:yyyyMMdd}.log");
            lock (Sync)
            {
                File.AppendAllText(file, line, Encoding.UTF8);
            }
        }
        catch
        {
        }
    }
}
