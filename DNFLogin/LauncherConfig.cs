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

internal sealed class LauncherConfig
{
    private const string ConfigFileName = "launcher-config.json";
    private const string StateFileName = "launcher-state.json";
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

        var migratedCurrentVersion = rawConfig.CurrentVersion;
        if (string.IsNullOrWhiteSpace(migratedCurrentVersion))
        {
            migratedCurrentVersion = LoadLegacyCurrentVersion(baseDirectory);
            shouldSaveMigratedConfig = true;
        }

        var config = new LauncherConfig
        {
            GameExePath = rawConfig.GameExePath ?? string.Empty,
            BaseResourceCheckFile = rawConfig.BaseResourceCheckFile ?? "Script.pvf",
            UpdateManifestUrl = string.IsNullOrWhiteSpace(rawConfig.UpdateManifestUrl) ? DefaultUpdateManifestUrl : rawConfig.UpdateManifestUrl!,
            CurrentVersion = string.IsNullOrWhiteSpace(migratedCurrentVersion) ? "0.0.0" : migratedCurrentVersion!,
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
            TryDeleteLegacyStateFile(baseDirectory);
        }

        return config;
    }

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

    public static IReadOnlyList<UpdatePackage> ResolvePendingUpdates(UpdateManifest manifest, string currentVersion)
    {
        return manifest.IncrementalUpdates
            .Where(x => HasAnyDownloadSource(x)
                && CompareVersions(x.Version, currentVersion) > 0)
            .OrderBy(x => x.Version, VersionComparer.Instance)
            .ToList();
    }

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
            || !string.IsNullOrWhiteSpace(r.DownloadUrl)
            || !string.IsNullOrWhiteSpace(r.ApiDownloadUrl)))
        {
            return true;
        }

        if (package.DownloadUrls.Any(url => !string.IsNullOrWhiteSpace(url)))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(package.ApiDownloadUrl))
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

    private static string LoadLegacyCurrentVersion(string baseDirectory)
    {
        var path = Path.Combine(baseDirectory, StateFileName);
        if (!File.Exists(path))
        {
            return "0.0.0";
        }

        var content = File.ReadAllText(path);
        var state = JsonSerializer.Deserialize<LauncherState>(content, JsonOptions);
        return string.IsNullOrWhiteSpace(state?.CurrentVersion) ? "0.0.0" : state.CurrentVersion;
    }

    private static void TryDeleteLegacyStateFile(string baseDirectory)
    {
        var path = Path.Combine(baseDirectory, StateFileName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

internal sealed class UpdateManifest
{
    [JsonPropertyName("configUpdateUrl")]
    public string? ConfigUpdateUrl { get; set; }

    [JsonPropertyName("fullPackage")]
    public UpdatePackage? FullPackage { get; set; }

    [JsonPropertyName("incrementalUpdates")]
    public List<UpdatePackage> IncrementalUpdates { get; set; } = [];

    [JsonPropertyName("pvfUpdates")]
    public List<UpdatePackage> PvfUpdates { get; set; } = [];
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

    [JsonPropertyName("apiDownloadUrl")]
    public string ApiDownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
}

internal sealed class DownloadRoute
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("downloadUrls")]
    public List<string> DownloadUrls { get; set; } = [];

    [JsonPropertyName("apiDownloadUrl")]
    public string ApiDownloadUrl { get; set; } = string.Empty;
}

internal sealed class LauncherState
{
    [JsonPropertyName("currentVersion")]
    public string CurrentVersion { get; set; } = "0.0.0";
}
