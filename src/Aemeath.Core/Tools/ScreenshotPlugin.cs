using Microsoft.SemanticKernel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Aemeath.Core.Tools;

public class ScreenshotPlugin
{
    [KernelFunction("take_screenshot")]
    [Description("截取屏幕截图")]
    public string TakeScreenshot(
        [Description("保存路径（可选）")] string? savePath = null)
    {
        try
        {
            if (string.IsNullOrEmpty(savePath))
            {
                var tempPath = Path.GetTempPath();
                var fileName = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                savePath = Path.Combine(tempPath, fileName);
            }

            var escaped = savePath.Replace("'", "''");
            var script =
                "$path='" + escaped + "';" +
                "Add-Type -AssemblyName System.Windows.Forms;" +
                "Add-Type -AssemblyName System.Drawing;" +
                "$bounds=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds;" +
                "$bmp=New-Object System.Drawing.Bitmap $bounds.Width,$bounds.Height;" +
                "$g=[System.Drawing.Graphics]::FromImage($bmp);" +
                "$g.CopyFromScreen($bounds.Location,[System.Drawing.Point]::Empty,$bounds.Size);" +
                "$bmp.Save($path,[System.Drawing.Imaging.ImageFormat]::Png);" +
                "$g.Dispose();$bmp.Dispose();";

            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            process?.WaitForExit(15000);

            if (!File.Exists(savePath))
            {
                return "截图失败：未生成输出文件";
            }

            return $"截图已保存到：{savePath}";
        }
        catch (Exception ex)
        {
            return $"截图失败：{ex.Message}";
        }
    }
}
