using Microsoft.SemanticKernel;
using System.ComponentModel;
using System.IO;

namespace Aemeath.Core.Tools;

public class FileSystemPlugin
{
    private readonly ToolConfirmationService? _confirmationService;

    public FileSystemPlugin(ToolConfirmationService? confirmationService = null)
    {
        _confirmationService = confirmationService;
    }

    private static bool IsAllowedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var full = Path.GetFullPath(path.Trim());
            // 必须是绝对路径，且落在允许的白名单根目录内（SEC-003）：
            // 用户主目录（文档/下载/桌面等日常文件）+ 临时目录。
            var allowedRoots = GetAllowedRoots();
            var allowed = allowedRoots.Any(root =>
                full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(full, root, StringComparison.OrdinalIgnoreCase));

            if (!allowed)
            {
                return false;
            }

            // 明确禁止进入应用私有数据目录（settings/记忆/mcp 配置含敏感凭据）
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Aemeath");
            if (full.StartsWith(appData + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(full, appData, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>文件访问白名单根目录：用户主目录 + 系统临时目录。</summary>
    private static List<string> GetAllowedRoots()
    {
        var roots = new List<string>();
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            roots.Add(Path.GetFullPath(userProfile));
        }

        var temp = Path.GetFullPath(Path.GetTempPath());
        if (!roots.Any(r => string.Equals(r, temp, StringComparison.OrdinalIgnoreCase)))
        {
            roots.Add(temp);
        }

        return roots;
    }

    [KernelFunction("read_file")]
    [Description("读取文件内容")]
    public string ReadFile(
        [Description("文件路径")] string path)
    {
        try
        {
            if (!IsAllowedPath(path))
            {
                return "拒绝访问：路径不在允许范围内";
            }
            if (!File.Exists(path))
            {
                return $"文件不存在：{path}";
            }
            
            return File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            return $"读取文件失败：{ex.Message}";
        }
    }

    [KernelFunction("write_file")]
    [Description("写入文件内容")]
    public string WriteFile(
        [Description("文件路径")] string path,
        [Description("文件内容")] string content)
    {
        if (_confirmationService is not null && File.Exists(path))
        {
            return _confirmationService.RequestConfirmation(
                "覆盖已有文件",
                $"即将覆盖写入文件：{path}",
                () => WriteFileCore(path, content));
        }

        return WriteFileCore(path, content);
    }

    private static string WriteFileCore(string path, string content)
    {
        try
        {
            if (!IsAllowedPath(path))
            {
                return "拒绝访问：路径不在允许范围内";
            }
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            File.WriteAllText(path, content);
            return $"文件已成功写入：{path}";
        }
        catch (Exception ex)
        {
            return $"写入文件失败：{ex.Message}";
        }
    }

    [KernelFunction("create_file")]
    [Description("创建文件并写入内容")]
    public string CreateFile(
        [Description("文件路径")] string path,
        [Description("文件内容")] string content = "")
    {
        return WriteFile(path, content);
    }

    [KernelFunction("edit_file")]
    [Description("编辑文件内容（覆盖写入）")]
    public string EditFile(
        [Description("文件路径")] string path,
        [Description("新的完整内容")] string content)
    {
        return WriteFile(path, content);
    }

    [KernelFunction("search_files")]
    [Description("搜索文件")]
    public string SearchFiles(
        [Description("搜索目录")] string directory,
        [Description("搜索模式，如*.txt")] string pattern,
        [Description("是否包含子目录")] bool includeSubdirectories = true)
    {
        try
        {
            if (!IsAllowedPath(directory))
            {
                return "拒绝访问：路径不在允许范围内";
            }
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = includeSubdirectories
            };
            
            var files = Directory.GetFiles(directory, pattern, options);
            return files.Length > 0 
                ? $"找到 {files.Length} 个文件:\n" + string.Join("\n", files)
                : "未找到匹配的文件";
        }
        catch (Exception ex)
        {
            return $"搜索文件失败：{ex.Message}";
        }
    }

    [KernelFunction("list_directory")]
    [Description("列出目录内容")]
    public string ListDirectory(
        [Description("目录路径")] string path)
    {
        try
        {
            if (!IsAllowedPath(path))
            {
                return "拒绝访问：路径不在允许范围内";
            }
            var directories = Directory.GetDirectories(path);
            var files = Directory.GetFiles(path);
            
            var result = $"目录：{path}\n\n文件夹:\n";
            foreach (var dir in directories)
            {
                result += $"  [DIR] {Path.GetFileName(dir)}\n";
            }
            
            result += "\n文件:\n";
            foreach (var file in files)
            {
                result += $"  [FILE] {Path.GetFileName(file)}\n";
            }
            
            return result;
        }
        catch (Exception ex)
        {
            return $"列出目录失败：{ex.Message}";
        }
    }
}
