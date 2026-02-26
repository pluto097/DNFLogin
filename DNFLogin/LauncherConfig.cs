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

    public required string Aria2Path { get; init; }
    public required string GameExePath { get; init; }
    public required string BaseResourceCheckFile { get; init; }
    public required string UpdateManifestUrl { get; init; }
    public required string SevenZipPath { get; init; }
    public string CurrentVersion { get; set; } = "0.0.0";

    public static LauncherConfig LoadOrCreate(string baseDirectory)
    {
        var configPath = Path.Combine(baseDirectory, ConfigFileName);
        if (!File.Exists(configPath))
        {
            var defaultConfig = new LauncherConfig
            {
                Aria2Path = "aria2c",
                GameExePath = "DNF.exe",
                BaseResourceCheckFile = "Script.pvf",
                UpdateManifestUrl = DefaultUpdateManifestUrl,
                SevenZipPath = "7z",
                CurrentVersion = "0.0.0"
            };

            File.WriteAllText(configPath, JsonSerializer.Serialize(defaultConfig, JsonOptions));
            return defaultConfig;
        }

        var content = File.ReadAllText(configPath);
        var rawConfig = JsonSerializer.Deserialize<LauncherConfigData>(content, JsonOptions)
            ?? throw new InvalidOperationException("无法解析 launcher-config.json");

        var shouldSaveMigratedConfig = string.IsNullOrWhiteSpace(rawConfig.UpdateManifestUrl)
            || string.IsNullOrWhiteSpace(rawConfig.SevenZipPath)
            || string.IsNullOrWhiteSpace(rawConfig.CurrentVersion);

        var migratedCurrentVersion = rawConfig.CurrentVersion;
        if (string.IsNullOrWhiteSpace(migratedCurrentVersion))
        {
            migratedCurrentVersion = LoadLegacyCurrentVersion(baseDirectory);
            shouldSaveMigratedConfig = true;
        }

        var config = new LauncherConfig
        {
            Aria2Path = rawConfig.Aria2Path ?? string.Empty,
            GameExePath = rawConfig.GameExePath ?? string.Empty,
            BaseResourceCheckFile = rawConfig.BaseResourceCheckFile ?? "Script.pvf",
            UpdateManifestUrl = string.IsNullOrWhiteSpace(rawConfig.UpdateManifestUrl) ? DefaultUpdateManifestUrl : rawConfig.UpdateManifestUrl!,
            SevenZipPath = string.IsNullOrWhiteSpace(rawConfig.SevenZipPath) ? "7z" : rawConfig.SevenZipPath!,
            CurrentVersion = string.IsNullOrWhiteSpace(migratedCurrentVersion) ? "0.0.0" : migratedCurrentVersion!
        };

        if (string.IsNullOrWhiteSpace(config.Aria2Path)
            || string.IsNullOrWhiteSpace(config.GameExePath)
            || string.IsNullOrWhiteSpace(config.UpdateManifestUrl)
            || string.IsNullOrWhiteSpace(config.SevenZipPath))
        {
            throw new InvalidOperationException("launcher-config.json 缺少必要字段: aria2Path、gameExePath、updateManifestUrl 或 sevenZipPath");
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
            .Where(x => (x.DownloadUrls.Any(url => !string.IsNullOrWhiteSpace(url)) || !string.IsNullOrWhiteSpace(x.DownloadUrl))
                && CompareVersions(x.Version, currentVersion) > 0)
            .OrderBy(x => x.Version, VersionComparer.Instance)
            .ToList();
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
        public string? Aria2Path { get; init; }
        public string? GameExePath { get; init; }
        public string? BaseResourceCheckFile { get; init; }
        public string? UpdateManifestUrl { get; init; }
        public string? SevenZipPath { get; init; }
        public string? CurrentVersion { get; init; }
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
    [JsonPropertyName("fullPackage")]
    public UpdatePackage? FullPackage { get; set; }

    [JsonPropertyName("incrementalUpdates")]
    public List<UpdatePackage> IncrementalUpdates { get; set; } = [];
}

internal sealed class UpdatePackage
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "0.0.0";

    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("downloadUrls")]
    public List<string> DownloadUrls { get; set; } = [];

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
}

internal sealed class LauncherState
{
    [JsonPropertyName("currentVersion")]
    public string CurrentVersion { get; set; } = "0.0.0";
}
