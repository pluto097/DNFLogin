using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DNFLogin;

internal sealed class LauncherConfig
{
    private const string ConfigFileName = "launcher-config.json";
    private const string ManifestFileName = "update-manifest.json";
    private const string StateFileName = "launcher-state.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public required string Aria2Path { get; init; }
    public required string GameExePath { get; init; }
    public required string BaseResourceCheckFile { get; init; }

    public static LauncherConfig LoadOrCreate(string baseDirectory)
    {
        var configPath = Path.Combine(baseDirectory, ConfigFileName);
        if (!File.Exists(configPath))
        {
            var defaultConfig = new LauncherConfig
            {
                Aria2Path = "aria2c",
                GameExePath = "DNF.exe",
                BaseResourceCheckFile = "Script.pvf"
            };

            File.WriteAllText(configPath, JsonSerializer.Serialize(defaultConfig, JsonOptions));
            return defaultConfig;
        }

        var content = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize<LauncherConfig>(content, JsonOptions)
            ?? throw new InvalidOperationException("无法解析 launcher-config.json");

        if (string.IsNullOrWhiteSpace(config.Aria2Path) || string.IsNullOrWhiteSpace(config.GameExePath))
        {
            throw new InvalidOperationException("launcher-config.json 缺少必要字段: aria2Path 或 gameExePath");
        }

        return config;
    }

    public static UpdateManifest LoadOrCreateManifest(string baseDirectory)
    {
        var path = Path.Combine(baseDirectory, ManifestFileName);
        if (!File.Exists(path))
        {
            var defaultManifest = new UpdateManifest
            {
                FullPackage = new UpdatePackage
                {
                    Version = "1.0.0",
                    DownloadUrl = "https://example.com/full-package.zip",
                    Description = "首次安装完整包"
                },
                IncrementalUpdates =
                [
                    new UpdatePackage
                    {
                        Version = "1.0.1",
                        DownloadUrl = "https://example.com/patch-1.0.1.zip",
                        Description = "示例补丁"
                    }
                ]
            };

            File.WriteAllText(path, JsonSerializer.Serialize(defaultManifest, JsonOptions));
            return defaultManifest;
        }

        var content = File.ReadAllText(path);
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(content, JsonOptions)
            ?? throw new InvalidOperationException("无法解析 update-manifest.json");

        manifest.IncrementalUpdates ??= [];
        return manifest;
    }

    public static LauncherState LoadOrCreateState(string baseDirectory)
    {
        var path = Path.Combine(baseDirectory, StateFileName);
        if (!File.Exists(path))
        {
            var state = new LauncherState { CurrentVersion = "0.0.0" };
            SaveState(baseDirectory, state);
            return state;
        }

        var content = File.ReadAllText(path);
        return JsonSerializer.Deserialize<LauncherState>(content, JsonOptions)
               ?? new LauncherState { CurrentVersion = "0.0.0" };
    }

    public static void SaveState(string baseDirectory, LauncherState state)
    {
        var path = Path.Combine(baseDirectory, StateFileName);
        File.WriteAllText(path, JsonSerializer.Serialize(state, JsonOptions));
    }

    public static IReadOnlyList<UpdatePackage> ResolvePendingUpdates(UpdateManifest manifest, string currentVersion)
    {
        return manifest.IncrementalUpdates
            .Where(x => !string.IsNullOrWhiteSpace(x.DownloadUrl) && CompareVersions(x.Version, currentVersion) > 0)
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

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
}

internal sealed class LauncherState
{
    [JsonPropertyName("currentVersion")]
    public string CurrentVersion { get; set; } = "0.0.0";
}
