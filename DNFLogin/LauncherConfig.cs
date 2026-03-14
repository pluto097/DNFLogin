using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DNFLogin;

    /// <summary>
    /// 启动器配置模型，负责管理本地配置文件（launcher-config.json）的加载与保存，
    /// 以及提供从云端拉取更新清单、解析待更新版本等核心业务逻辑。
    /// </summary>
    internal sealed class LauncherConfig
{
    private const string ConfigFileName = "launcher-config.json";
    private const string DefaultUpdateManifestUrl = "https://example.com/update-manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public required string GameExePath { get; init; }
    public required string BaseResourceCheckFile { get; init; }
    public required string UpdateManifestUrl { get; set; }
    public string CurrentVersion { get; set; } = "0.0.0";
    public string PVFVersion { get; set; } = "0";

    /// <summary>
    /// 加载本地配置文件。如果文件不存在则创建一个包含默认配置的初始文件。
    /// 还会负责初始化和补全缺失的关键信息。
    /// </summary>
    public static LauncherConfig LoadOrCreate(string baseDirectory)
    {
        var configPath = Path.Combine(baseDirectory, ConfigFileName);
        if (!File.Exists(configPath))
        {
            var defaultConfig = new LauncherConfig
            {
                GameExePath = "DNF.exe",
                BaseResourceCheckFile = "Script.pvf",
                UpdateManifestUrl = DefaultUpdateManifestUrl,
                CurrentVersion = "0.0.0",
                PVFVersion = "0"
            };

            File.WriteAllText(configPath, JsonSerializer.Serialize(defaultConfig, JsonOptions));
            return defaultConfig;
        }

        var content = File.ReadAllText(configPath);
        var rawConfig = JsonSerializer.Deserialize<LauncherConfigData>(content, JsonOptions)
            ?? throw new InvalidOperationException("无法解析 launcher-config.json");

        var shouldSaveMigratedConfig = string.IsNullOrWhiteSpace(rawConfig.UpdateManifestUrl)
            || string.IsNullOrWhiteSpace(rawConfig.CurrentVersion);

        var config = new LauncherConfig
        {
            GameExePath = rawConfig.GameExePath ?? string.Empty,
            BaseResourceCheckFile = rawConfig.BaseResourceCheckFile ?? "Script.pvf",
            UpdateManifestUrl = string.IsNullOrWhiteSpace(rawConfig.UpdateManifestUrl) ? DefaultUpdateManifestUrl : rawConfig.UpdateManifestUrl!,
            CurrentVersion = string.IsNullOrWhiteSpace(rawConfig.CurrentVersion) ? "0.0.0" : rawConfig.CurrentVersion!,
            PVFVersion = string.IsNullOrWhiteSpace(rawConfig.PVFVersion) ? "0" : rawConfig.PVFVersion!
        };

        if (string.IsNullOrWhiteSpace(config.GameExePath)
            || string.IsNullOrWhiteSpace(config.UpdateManifestUrl))
        {
            throw new InvalidOperationException("launcher-config.json 缺少必要字段: gameExePath 或 updateManifestUrl");
        }

        if (shouldSaveMigratedConfig)
        {
            File.WriteAllText(configPath, JsonSerializer.Serialize(config, JsonOptions));
        }

        return config;
    }

    /// <summary>
    /// 使用配置中的 UpdateManifestUrl 异步向云端拉取最新更新清单（update-manifest.json）。
    /// </summary>
    public static async Task<UpdateManifest> LoadManifestFromRemoteAsync(LauncherConfig config, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(config.UpdateManifestUrl, UriKind.Absolute, out var manifestUri))
        {
            throw new InvalidOperationException("launcher-config.json 中 updateManifestUrl 不是有效的绝对地址");
        }

        using var client = new HttpClient();
        using var response = await client.GetAsync(manifestUri, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var manifest = JsonSerializer.Deserialize<UpdateManifest>(content, JsonOptions)
            ?? throw new InvalidOperationException("无法解析 update-manifest.json");

        manifest.IncrementalUpdates ??= [];
        manifest.PvfUpdates ??= [];
        return manifest;
    }

    public static void SaveConfig(string baseDirectory, LauncherConfig config)
    {
        var configPath = Path.Combine(baseDirectory, ConfigFileName);
        File.WriteAllText(configPath, JsonSerializer.Serialize(config, JsonOptions));
    }

    /// <summary>
    /// 解析所有待应用的常规增量更新（版本号大于当前版本且包含有效下载地址的更新包）。
    /// </summary>
    public static IReadOnlyList<UpdatePackage> ResolvePendingUpdates(UpdateManifest manifest, string currentVersion)
    {
        return manifest.IncrementalUpdates
            .Where(x => HasAnyDownloadSource(x)
                && CompareVersions(x.Version, currentVersion) > 0)
            .OrderBy(x => x.Version, VersionComparer.Instance)
            .ToList();
    }

    /// <summary>
    /// 解析所有待应用的 PVF 增量更新（版本号大于当前 PVF 版本且包含有效下载地址的更新包）。
    /// </summary>
    public static IReadOnlyList<UpdatePackage> ResolvePendingPVFUpdates(UpdateManifest manifest, string currentPVFVersion)
    {
        return manifest.PvfUpdates
            .Where(x => HasAnyDownloadSource(x)
                && CompareVersions(x.Version, currentPVFVersion) > 0)
            .OrderBy(x => x.Version, VersionComparer.Instance)
            .ToList();
    }

    /// <summary>
    /// 检查云端清单中的 configUpdateUrl 是否需要同步到本地配置。
    /// 如果需要更新，则回写本地 launcher-config.json 并使用新 URL 重新拉取云端清单。
    /// </summary>
    public static async Task<(LauncherConfig Config, UpdateManifest Manifest)> SyncConfigUpdateUrlAsync(
        string baseDirectory, LauncherConfig config, UpdateManifest manifest, CancellationToken cancellationToken = default)
    {
        var remoteUrl = manifest.ConfigUpdateUrl;

        // configUpdateUrl 为空或未定义，跳过
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return (config, manifest);
        }

        // configUpdateUrl 与当前本地 URL 相同，跳过
        if (string.Equals(config.UpdateManifestUrl, remoteUrl, StringComparison.OrdinalIgnoreCase))
        {
            return (config, manifest);
        }

        // URL 不同，更新本地配置并重新拉取清单
        config.UpdateManifestUrl = remoteUrl;
        SaveConfig(baseDirectory, config);

        var newManifest = await LoadManifestFromRemoteAsync(config, cancellationToken).ConfigureAwait(false);
        return (config, newManifest);
    }

    private static bool HasAnyDownloadSource(UpdatePackage package)
    {
        if (package.DownloadRoutes.Any(r =>
            r.DownloadUrls.Any(url => !string.IsNullOrWhiteSpace(url))
            || !string.IsNullOrWhiteSpace(r.DownloadUrl)))
        {
            return true;
        }

        if (package.DownloadUrls.Any(url => !string.IsNullOrWhiteSpace(url)))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(package.DownloadUrl);
    }

    private static int CompareVersions(string? left, string? right)
    {
        if (Version.TryParse(left, out var lv) && Version.TryParse(right, out var rv))
        {
            return lv.CompareTo(rv);
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class VersionComparer : IComparer<string?>
    {
        public static VersionComparer Instance { get; } = new();

        public int Compare(string? x, string? y) => CompareVersions(x, y);
    }

    private sealed class LauncherConfigData
    {
        public string? GameExePath { get; init; }
        public string? BaseResourceCheckFile { get; init; }
        public string? UpdateManifestUrl { get; init; }
        public string? CurrentVersion { get; init; }
        public string? PVFVersion { get; init; }
    }
}

internal sealed class UpdateManifest
{
    [JsonPropertyName("slowSpeedConfig")]
    public SlowSpeedConfig? SlowSpeedConfig { get; set; }

    [JsonPropertyName("configUpdateUrl")]
    public string? ConfigUpdateUrl { get; set; }

    [JsonPropertyName("fullPackage")]
    public UpdatePackage? FullPackage { get; set; }

    [JsonPropertyName("incrementalUpdates")]
    public List<UpdatePackage> IncrementalUpdates { get; set; } = [];

    [JsonPropertyName("pvfUpdates")]
    public List<UpdatePackage> PvfUpdates { get; set; } = [];
}

/// <summary>
/// 下载速度异常判定阈值配置（从云端 update-manifest.json 读取）。
/// 当下载速度≤thresholdMiBps 且持续≥durationSeconds 秒时，自动切换线路。
/// </summary>
internal sealed class SlowSpeedConfig
{
    [JsonPropertyName("thresholdMiBps")]
    public double? ThresholdMiBps { get; set; }

    [JsonPropertyName("durationSeconds")]
    public int? DurationSeconds { get; set; }
}

internal sealed class UpdatePackage
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "0.0.0";

    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("downloadUrls")]
    public List<string> DownloadUrls { get; set; } = [];

    [JsonPropertyName("downloadRoutes")]
    public List<DownloadRoute> DownloadRoutes { get; set; } = [];

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("downloadArgs")]
    public string DownloadArgs { get; set; } = string.Empty;
}

internal sealed class DownloadRoute
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("downloadUrls")]
    public List<string> DownloadUrls { get; set; } = [];

    [JsonPropertyName("downloadArgs")]
    public string DownloadArgs { get; set; } = string.Empty;
}
