using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace DNFLogin;

internal sealed class LauncherConfig
{
    private const string ConfigFileName = "launcher-config.json";
    private const string StateFileName = "launcher-state.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public required string Aria2Path { get; init; }
    public required string GameExePath { get; init; }
    public required string BaseResourceCheckFile { get; init; }
    public required string ManifestUrl { get; init; }

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
                ManifestUrl = "https://example.com/update-manifest.json"
            };

            File.WriteAllText(configPath, JsonSerializer.Serialize(defaultConfig, JsonOptions));
            return defaultConfig;
        }

        var content = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize<LauncherConfig>(content, JsonOptions)
            ?? throw new InvalidOperationException("无法解析 launcher-config.json");

        if (string.IsNullOrWhiteSpace(config.Aria2Path) ||
            string.IsNullOrWhiteSpace(config.GameExePath) ||
            !IsValidUrl(config.ManifestUrl))
        {
            throw new InvalidOperationException("launcher-config.json 缺少必要字段: aria2Path / gameExePath / manifestUrl");
        }

        return config;
    }

    public static async Task<UpdateManifest> LoadManifestFromUrlAsync(HttpClient httpClient, string manifestUrl)
    {
        if (!IsValidUrl(manifestUrl))
        {
            throw new InvalidOperationException("manifestUrl 无效，请检查 launcher-config.json");
        }

        var content = await httpClient.GetStringAsync(manifestUrl).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(content, JsonOptions)
            ?? throw new InvalidOperationException("无法解析云端 update-manifest.json");

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

    private static bool IsValidUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme is Uri.UriSchemeHttp or Uri.UriSchemeHttps;
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
