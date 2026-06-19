using Aemeath.Core.Configuration;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aemeath.Core.MCP;

public sealed record McpDependencyStatus(bool UvExists, bool BunExists, string? UvPath, string? BunPath)
{
    public bool IsComplete => UvExists && BunExists;
}

public sealed record McpDownloadMirror(string Name, string BaseUrl, string Kind, int Priority);

public sealed record McpDependencyInstallResult(
    bool Success,
    string Message,
    IReadOnlyList<string> DownloadedItems,
    IReadOnlyList<string> UsedMirrors);

public sealed class McpDependencyService
{
    private const string UvExeName = "uv.exe";
    private const string UvxExeName = "uvx.exe";
    private const string BunExeName = "bun.exe";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(18);
    private readonly HttpClient _httpClient;

    public static string DefaultBinDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Aemeath",
        "tools",
        "bin");

    public McpDependencyService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = RequestTimeout
        };
    }

    public Task<McpDependencyStatus> CheckAsync(Settings settings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uv = ResolveExecutablePath(settings.UvExecutablePath, UvExeName);
        var bun = ResolveExecutablePath(settings.BunExecutablePath, BunExeName);
        return Task.FromResult(new McpDependencyStatus(
            UvExists: !string.IsNullOrWhiteSpace(uv),
            BunExists: !string.IsNullOrWhiteSpace(bun),
            UvPath: uv,
            BunPath: bun));
    }

    public async Task<McpDependencyInstallResult> InstallMissingAsync(
        Settings settings,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(DefaultBinDirectory);
        var before = await CheckAsync(settings, cancellationToken).ConfigureAwait(false);
        if (before.IsComplete)
        {
            settings.UvExecutablePath = before.UvPath;
            settings.BunExecutablePath = before.BunPath;
            return new McpDependencyInstallResult(
                true,
                "已检测到 uv.exe 和 bun.exe，不必下载。",
                Array.Empty<string>(),
                Array.Empty<string>());
        }

        var downloaded = new List<string>();
        var usedMirrors = new List<string>();
        var errors = new List<string>();

        if (!before.UvExists)
        {
            progress?.Report("正在下载 uv.exe...");
            var uvResult = await InstallUvAsync(progress, cancellationToken).ConfigureAwait(false);
            if (uvResult.Success && !string.IsNullOrWhiteSpace(uvResult.ExecutablePath))
            {
                settings.UvExecutablePath = uvResult.ExecutablePath;
                downloaded.Add("uv.exe");
                if (File.Exists(Path.Combine(DefaultBinDirectory, UvxExeName)))
                {
                    downloaded.Add("uvx.exe");
                }

                usedMirrors.Add(uvResult.MirrorName!);
            }
            else
            {
                errors.Add("uv.exe：" + uvResult.Message);
            }
        }
        else
        {
            settings.UvExecutablePath = before.UvPath;
        }

        if (!before.BunExists)
        {
            progress?.Report("正在下载 bun.exe...");
            var bunResult = await InstallBunAsync(progress, cancellationToken).ConfigureAwait(false);
            if (bunResult.Success && !string.IsNullOrWhiteSpace(bunResult.ExecutablePath))
            {
                settings.BunExecutablePath = bunResult.ExecutablePath;
                downloaded.Add("bun.exe");
                usedMirrors.Add(bunResult.MirrorName!);
            }
            else
            {
                errors.Add("bun.exe：" + bunResult.Message);
            }
        }
        else
        {
            settings.BunExecutablePath = before.BunPath;
        }

        var after = await CheckAsync(settings, cancellationToken).ConfigureAwait(false);
        var success = after.IsComplete;
        var message = success
            ? $"MCP 依赖已准备好：{string.Join("、", downloaded.DefaultIfEmpty("无需下载"))}"
            : $"MCP 依赖未完全准备好：{string.Join("；", errors)}";

        return new McpDependencyInstallResult(success, message, downloaded, usedMirrors);
    }

    public static string? ResolveExecutablePath(string? configuredPath, string exeName)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        var candidates = new[]
        {
            Path.Combine(DefaultBinDirectory, exeName),
            Path.Combine(Directory.GetCurrentDirectory(), "bin", exeName),
            Path.Combine(AppContext.BaseDirectory, "bin", exeName),
            Path.Combine(AppContext.BaseDirectory, exeName),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "bin", exeName),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "bin", exeName),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "bin", exeName)
        };

        return candidates
                   .Select(Path.GetFullPath)
                   .FirstOrDefault(File.Exists)
               ?? null;
    }

    private async Task<InstallOneResult> InstallUvAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var mirrors = new[]
        {
            new McpDownloadMirror("USTC uv", "https://mirrors.ustc.edu.cn/github-release/astral-sh/uv/", "uv-release", 1),
            new McpDownloadMirror("TUNA uv", "https://mirrors.tuna.tsinghua.edu.cn/github-release/astral-sh/uv/", "uv-release", 2),
            new McpDownloadMirror("阿里云 uv", "https://mirrors.aliyun.com/github-release/astral-sh/uv/", "uv-release", 3),
            new McpDownloadMirror("腾讯云 uv", "https://mirrors.cloud.tencent.com/github-release/astral-sh/uv/", "uv-release", 4),
            new McpDownloadMirror("阿里云 PyPI uv", "https://mirrors.aliyun.com/pypi/simple/uv/", "uv-pypi", 5)
        };

        var errors = new List<string>();
        foreach (var mirror in mirrors.OrderBy(x => x.Priority))
        {
            try
            {
                progress?.Report($"正在尝试 {mirror.Name}...");
                var archivePath = mirror.Kind == "uv-pypi"
                    ? await DownloadLatestUvWheelAsync(mirror, cancellationToken).ConfigureAwait(false)
                    : await DownloadFromReleaseMirrorAsync(mirror, "uv-x86_64-pc-windows-msvc.zip", cancellationToken).ConfigureAwait(false);

                ExtractZipExecutables(archivePath, DefaultBinDirectory, UvExeName, UvxExeName);
                var uvPath = Path.Combine(DefaultBinDirectory, UvExeName);
                if (!File.Exists(uvPath))
                {
                    throw new InvalidOperationException("压缩包中未找到 uv.exe");
                }

                TryDeleteFile(archivePath);
                return InstallOneResult.Ok(uvPath, mirror.Name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{mirror.Name}: {ex.Message}");
            }
        }

        return InstallOneResult.Fail(string.Join("；", errors));
    }

    private async Task<InstallOneResult> InstallBunAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var mirrors = new[]
        {
            new McpDownloadMirror("USTC bun", "https://mirrors.ustc.edu.cn/github-release/oven-sh/bun/", "bun-release", 1),
            new McpDownloadMirror("TUNA bun", "https://mirrors.tuna.tsinghua.edu.cn/github-release/oven-sh/bun/", "bun-release", 2),
            new McpDownloadMirror("阿里云 bun", "https://mirrors.aliyun.com/github-release/oven-sh/bun/", "bun-release", 3),
            new McpDownloadMirror("腾讯云 bun", "https://mirrors.cloud.tencent.com/github-release/oven-sh/bun/", "bun-release", 4),
            new McpDownloadMirror("npmmirror bun", "https://registry.npmmirror.com/@oven/bun-windows-x64/latest", "bun-npm", 5),
            new McpDownloadMirror("npmmirror bun baseline", "https://registry.npmmirror.com/@oven/bun-windows-x64-baseline/latest", "bun-npm", 6)
        };

        var errors = new List<string>();
        foreach (var mirror in mirrors.OrderBy(x => x.Priority))
        {
            try
            {
                progress?.Report($"正在尝试 {mirror.Name}...");
                var archivePath = mirror.Kind == "bun-npm"
                    ? await DownloadNpmTarballAsync(mirror, cancellationToken).ConfigureAwait(false)
                    : await DownloadFromReleaseMirrorAsync(mirror, "bun-windows-x64.zip", cancellationToken).ConfigureAwait(false);

                if (archivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
                {
                    ExtractTarGzExecutable(archivePath, DefaultBinDirectory, BunExeName);
                }
                else
                {
                    ExtractZipExecutables(archivePath, DefaultBinDirectory, BunExeName);
                }

                var bunPath = Path.Combine(DefaultBinDirectory, BunExeName);
                if (!File.Exists(bunPath))
                {
                    throw new InvalidOperationException("压缩包中未找到 bun.exe");
                }

                TryDeleteFile(archivePath);
                return InstallOneResult.Ok(bunPath, mirror.Name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{mirror.Name}: {ex.Message}");
            }
        }

        return InstallOneResult.Fail(string.Join("；", errors));
    }

    private async Task<string> DownloadFromReleaseMirrorAsync(
        McpDownloadMirror mirror,
        string assetName,
        CancellationToken cancellationToken)
    {
        var baseUri = EnsureTrailingSlash(mirror.BaseUrl);
        var direct = new Uri(baseUri, "LatestRelease/" + assetName);
        try
        {
            return await DownloadFileAsync(direct, Path.GetExtension(assetName), expectedSha256: null, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Fall through to directory parsing. Mirror providers do not all expose LatestRelease.
        }

        var versionDirectories = await ListReleaseDirectoriesAsync(baseUri, cancellationToken).ConfigureAwait(false);
        foreach (var directory in versionDirectories)
        {
            var directoryUri = new Uri(baseUri, directory);
            var assetUri = await FindAssetInDirectoryAsync(directoryUri, assetName, cancellationToken).ConfigureAwait(false);
            if (assetUri is null)
            {
                continue;
            }

            return await DownloadFileAsync(assetUri, Path.GetExtension(assetName), expectedSha256: null, cancellationToken).ConfigureAwait(false);
        }

        throw new FileNotFoundException($"未在 {mirror.BaseUrl} 找到 {assetName}");
    }

    private async Task<string> DownloadLatestUvWheelAsync(McpDownloadMirror mirror, CancellationToken cancellationToken)
    {
        var page = await GetStringAsync(new Uri(mirror.BaseUrl), cancellationToken).ConfigureAwait(false);
        var candidates = new List<(Version Version, Uri Uri, string? Sha256)>();
        foreach (Match match in Regex.Matches(
                     page,
                     "href=\"(?<href>[^\"]*uv-(?<version>[0-9.]+)-py3-none-win_amd64\\.whl(?:#sha256=(?<sha>[a-fA-F0-9]+))?)\"",
                     RegexOptions.IgnoreCase))
        {
            if (!Version.TryParse(match.Groups["version"].Value, out var version))
            {
                continue;
            }

            var href = System.Net.WebUtility.HtmlDecode(match.Groups["href"].Value);
            var hashIndex = href.IndexOf('#');
            var cleanHref = hashIndex >= 0 ? href[..hashIndex] : href;
            var sha = match.Groups["sha"].Success ? match.Groups["sha"].Value : null;
            candidates.Add((version, new Uri(new Uri(mirror.BaseUrl), cleanHref), sha));
        }

        var latest = candidates
            .OrderByDescending(x => x.Version)
            .FirstOrDefault();
        if (latest.Uri is null)
        {
            throw new FileNotFoundException("阿里云 PyPI 镜像中未找到 Windows x64 uv wheel");
        }

        return await DownloadFileAsync(latest.Uri, ".whl", latest.Sha256, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> DownloadNpmTarballAsync(McpDownloadMirror mirror, CancellationToken cancellationToken)
    {
        var json = await GetStringAsync(new Uri(mirror.BaseUrl), cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("dist", out var dist) ||
            !dist.TryGetProperty("tarball", out var tarballElement))
        {
            throw new InvalidDataException("npm metadata 中没有 dist.tarball");
        }

        var tarball = tarballElement.GetString();
        if (string.IsNullOrWhiteSpace(tarball))
        {
            throw new InvalidDataException("npm metadata 中的 dist.tarball 为空");
        }

        return await DownloadFileAsync(new Uri(tarball), ".tgz", expectedSha256: null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> ListReleaseDirectoriesAsync(Uri baseUri, CancellationToken cancellationToken)
    {
        var page = await GetStringAsync(baseUri, cancellationToken).ConfigureAwait(false);
        var names = Regex.Matches(page, "href=\"(?<href>[^\"]+/)\"", RegexOptions.IgnoreCase)
            .Select(m => System.Net.WebUtility.HtmlDecode(m.Groups["href"].Value))
            .Where(href => href != "../" && !href.StartsWith("?", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(ParseReleaseDirectoryVersion)
            .ThenByDescending(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return names;
    }

    private async Task<Uri?> FindAssetInDirectoryAsync(Uri directoryUri, string assetName, CancellationToken cancellationToken)
    {
        var page = await GetStringAsync(directoryUri, cancellationToken).ConfigureAwait(false);
        foreach (Match match in Regex.Matches(page, "href=\"(?<href>[^\"]+)\"", RegexOptions.IgnoreCase))
        {
            var href = System.Net.WebUtility.HtmlDecode(match.Groups["href"].Value);
            var cleanHref = href.Split('#')[0].Split('?')[0];
            if (Path.GetFileName(cleanHref).Equals(assetName, StringComparison.OrdinalIgnoreCase))
            {
                return new Uri(directoryUri, href);
            }
        }

        return null;
    }

    private async Task<string> DownloadFileAsync(
        Uri uri,
        string extension,
        string? expectedSha256,
        CancellationToken cancellationToken)
    {
        using var response = await GetWithSafeRedirectsAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"下载失败：{(int)response.StatusCode} {response.ReasonPhrase}");
        }

        var tempPath = Path.Combine(Path.GetTempPath(), "aemeath-mcp-" + Guid.NewGuid().ToString("N") + extension);
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var output = File.Create(tempPath))
        {
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }

        // 尝试从响应头补全 sha（SEC-011），部分镜像会在 X-Checksum-SHA256 / X-SHA256 头给出。
        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            foreach (var header in new[] { "X-Checksum-SHA256", "X-SHA256", "ETag" })
            {
                var v = response.Headers.TryGetValues(header, out var vals) ? vals.FirstOrDefault() : null;
                if (!string.IsNullOrWhiteSpace(v))
                {
                    var clean = v.Trim().Trim('"');
                    if (clean.Length >= 64 && clean.All(c => "0123456789abcdefABCDEF".IndexOf(c) >= 0))
                    {
                        expectedSha256 = clean;
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(expectedSha256))
            {
                System.Diagnostics.Debug.WriteLine($"[mcp-dep] {uri} 未提供 SHA256，已跳过完整性校验（供应链风险）");
            }
        }

        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            var actual = await ComputeSha256Async(tempPath, cancellationToken).ConfigureAwait(false);
            if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tempPath);
                throw new InvalidDataException("下载文件 sha256 校验失败");
            }
        }

        return tempPath;
    }

    private async Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await GetWithSafeRedirectsAsync(uri, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"请求失败：{(int)response.StatusCode} {response.ReasonPhrase}");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> GetWithSafeRedirectsAsync(
        Uri uri,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        var current = uri;
        for (var i = 0; i < 6; i++)
        {
            var response = await _httpClient.GetAsync(current, completionOption, cancellationToken).ConfigureAwait(false);
            if (!IsRedirect(response))
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null)
            {
                throw new HttpRequestException("镜像返回了重定向，但没有提供目标地址");
            }

            var next = location.IsAbsoluteUri ? location : new Uri(current, location);
            if (IsGitHubDownloadHost(next.Host))
            {
                throw new HttpRequestException("镜像重定向到 GitHub 官方下载源，已跳过该源");
            }

            current = next;
        }

        throw new HttpRequestException("镜像重定向次数过多");
    }

    private static void ExtractZipExecutables(string archivePath, string destinationDirectory, params string[] exeNames)
    {
        Directory.CreateDirectory(destinationDirectory);
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var exeName in exeNames)
        {
            var entry = archive.Entries.FirstOrDefault(e =>
                Path.GetFileName(e.FullName).Equals(exeName, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                continue;
            }

            entry.ExtractToFile(Path.Combine(destinationDirectory, exeName), overwrite: true);
        }
    }

    private static void ExtractTarGzExecutable(string archivePath, string destinationDirectory, string exeName)
    {
        Directory.CreateDirectory(destinationDirectory);
        using var file = File.OpenRead(archivePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            if (entry.DataStream is null ||
                !Path.GetFileName(entry.Name).Equals(exeName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var output = File.Create(Path.Combine(destinationDirectory, exeName));
            entry.DataStream.CopyTo(output);
            return;
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>安全删除临时下载文件，失败不影响安装结果（RES-013）。</summary>
    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
            // 临时文件清理失败不阻断安装流程
        }
    }

    private static Uri EnsureTrailingSlash(string url)
    {
        return new Uri(url.EndsWith("/", StringComparison.Ordinal) ? url : url + "/");
    }

    private static Version ParseReleaseDirectoryVersion(string name)
    {
        var clean = name.Trim('/').Replace("bun-v", string.Empty, StringComparison.OrdinalIgnoreCase)
            .TrimStart('v');
        return Version.TryParse(clean, out var version) ? version : new Version(0, 0);
    }

    private static bool IsRedirect(HttpResponseMessage response)
        => (int)response.StatusCode is >= 300 and < 400;

    private static bool IsGitHubDownloadHost(string host)
    {
        return host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
               || host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
               || host.Equals("githubusercontent.com", StringComparison.OrdinalIgnoreCase)
               || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)
               || host.Equals("github-releases.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
               || host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record InstallOneResult(bool Success, string Message, string? ExecutablePath, string? MirrorName)
    {
        public static InstallOneResult Ok(string executablePath, string mirrorName)
            => new(true, "OK", executablePath, mirrorName);

        public static InstallOneResult Fail(string message)
            => new(false, message, null, null);
    }
}
