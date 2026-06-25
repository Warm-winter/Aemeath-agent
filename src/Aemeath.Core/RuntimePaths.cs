namespace Aemeath.Core;

/// <summary>
/// 集中解析「应用运行目录」下的运行时数据/依赖路径。
///
/// 设计取舍：
/// - 占空间大的依赖（Python venv、向量库、UFO 源码）放在应用运行目录（<see cref="AppContext.BaseDirectory"/>）
///   下的 runtime/ 子目录，而非 %AppData%（C 盘），避免大量占用系统盘。
/// - 用户配置（settings/sessions/skills）仍在 %AppData%\Aemeath（升级时不丢、符合 Windows 规范）。
///
/// 便携版/本地解压：runtime/ 就在 exe 旁，跟随程序走。
/// 正式安装到 Program Files：runtime/ 可能只读——本项目默认安装在用户目录或解压运行，避免该问题；
/// 若检测到运行目录不可写，调用方可回退到 %AppData%\Aemeath\runtime。
/// </summary>
public static class RuntimePaths
{
    /// <summary>运行目录（exe 所在目录）。</summary>
    public static string BaseDirectory => AppContext.BaseDirectory;

    /// <summary>运行时依赖根目录（venv、向量库、UFO 源码等都放这里）。</summary>
    public static string RuntimeRoot => Path.Combine(BaseDirectory, "runtime");

    /// <summary>Mem0 专用 venv 目录（命名为 Aemeath-Agent，见 #7）。</summary>
    public static string Mem0VenvDirectory => Path.Combine(RuntimeRoot, "Aemeath-Agent");

    /// <summary>UFO 专用 venv 目录（可选安装，轨 B）。</summary>
    public static string UfoVenvDirectory => Path.Combine(RuntimeRoot, "Aemeath-Agent-Ufo");

    /// <summary>Mem0 数据目录（Qdrant 本地库 + history.db + 桥接脚本）。</summary>
    public static string Mem0DataDirectory => Path.Combine(RuntimeRoot, "mem0-data");

    /// <summary>UFO 源码克隆目录（可选安装）。</summary>
    public static string UfoSourceDirectory => Path.Combine(RuntimeRoot, "ufo-src");

    /// <summary>UFO 桥接脚本目录。</summary>
    public static string UfoBridgeDirectory => Path.Combine(RuntimeRoot, "ufo-bridge");

    /// <summary>确保目录存在。</summary>
    public static void EnsureDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }
    }

    /// <summary>
    /// 解析 venv 里的 python 解释器绝对路径。
    /// Windows: &lt;venv&gt;/Scripts/python.exe；跨平台兼容：bin/python。
    /// </summary>
    public static string ResolveVenvPython(string venvDir)
    {
        if (string.IsNullOrWhiteSpace(venvDir))
        {
            return string.Empty;
        }

        var win = Path.Combine(venvDir, "Scripts", "python.exe");
        if (File.Exists(win))
        {
            return win;
        }

        var unix = Path.Combine(venvDir, "bin", "python");
        return File.Exists(unix) ? unix : string.Empty;
    }
}
