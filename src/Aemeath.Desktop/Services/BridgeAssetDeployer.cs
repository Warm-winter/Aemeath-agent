using System.Reflection;
using Aemeath.Core;

namespace Aemeath.Desktop.Services;

/// <summary>
/// 把内嵌的桥接脚本（ufo_runner.py 等）释放到磁盘，供运行时调用。
/// Mem0 的桥接脚本由 Mem0Client 自行按需释放；UFO 的脚本只在启用 UFO 时才需要，
/// 这里在启动时统一释放到运行目录下的 runtime/ufo-bridge。
/// </summary>
public static class BridgeAssetDeployer
{
    private static string BridgeDir => RuntimePaths.UfoBridgeDirectory;

    /// <summary>释放 ufo_runner.py 到 ufo-bridge 目录。</summary>
    public static void DeployUfoRunner()
    {
        try
        {
            Directory.CreateDirectory(BridgeDir);
            var target = Path.Combine(BridgeDir, "ufo_runner.py");
            var assembly = Assembly.GetExecutingAssembly();
            // 资源在 Aemeath.Core 程序集里，这里跨程序集读取
            var coreAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "Aemeath.Core", StringComparison.OrdinalIgnoreCase));
            if (coreAssembly is null)
            {
                return;
            }

            const string resourceName = "Aemeath.Core.ComputerControl.ufo_runner.py";
            using var stream = coreAssembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                return;
            }

            var tmp = target + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.CopyTo(fs);
            }

            if (File.Exists(target)) File.Delete(target);
            File.Move(tmp, target);
            AppLogger.Info("app", $"UFO 桥接脚本已释放：{target}");
        }
        catch (Exception ex)
        {
            AppLogger.Error("app", "释放 UFO 桥接脚本失败", ex);
        }
    }
}
