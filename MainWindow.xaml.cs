using AduSkin.Controls.Metro;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace DNFLogin
{
    public partial class MainWindow : AduWindow
    {
        private static readonly Uri DefaultBackgroundUri = new("pack://application:,,,/Assets/default-bg-img.png", UriKind.Absolute);

        private readonly string _baseDirectory;
        private bool _hasShown;
        private double _globalProgress;
        private bool _updateStarted;
        private ImageBrush? _backgroundBrush;

        public MainWindow()
        {
            InitializeComponent();
            _baseDirectory = AppContext.BaseDirectory;
            Title = "DOF 下载与更新器";
            SetDefaultBackground();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _ = RunUpdateFlowAsync();
        }

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);

            if (_hasShown)
            {
                return;
            }

            _hasShown = true;
            FadeInWindow();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
        }

        private void RootContainer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void FadeInWindow()
        {
            if (Opacity >= 1)
            {
                return;
            }

            var animation = new DoubleAnimation
            {
                To = 1,
                Duration = TimeSpan.FromMilliseconds(320),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            BeginAnimation(OpacityProperty, animation);
        }

        private ImageBrush? TryGetBackgroundBrush()
        {
            if (_backgroundBrush is not null)
            {
                return _backgroundBrush;
            }

            _backgroundBrush = FindName("BackgroundImageBrush") as ImageBrush;
            return _backgroundBrush;
        }

        private void SetDefaultBackground()
        {
            if (TryGetBackgroundBrush() is not ImageBrush brush)
            {
                return;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = DefaultBackgroundUri;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            brush.ImageSource = bitmap;
        }

        private async Task RunUpdateFlowAsync()
        {
            if (_updateStarted)
            {
                return;
            }

            _updateStarted = true;

            try
            {
                ReportProgress("正在初始化", "读取本地配置并请求云端更新配置", 5, 5);
                var config = LauncherConfig.LoadOrCreate(_baseDirectory);
                var manifest = await LauncherConfig.LoadManifestFromRemoteAsync(config).ConfigureAwait(false);

                const double baseSpan = 40d;
                const double incrementalSpan = 60d;

                await EnsureBasePackageAsync(config, manifest, 0, baseSpan).ConfigureAwait(false);

                var currentVersion = config.CurrentVersion;
                var updates = LauncherConfig.ResolvePendingUpdates(manifest, currentVersion);

                if (updates.Count == 0)
                {
                    ReportProgress("已是最新版本", $"当前版本：{currentVersion}", 100, 100);
                    await Task.Delay(400).ConfigureAwait(false);
                    LaunchGame(config);
                    return;
                }

                var perUpdateSpan = incrementalSpan / updates.Count;

                for (var i = 0; i < updates.Count; i++)
                {
                    var update = updates[i];
                    var stageStart = baseSpan + (perUpdateSpan * i);
                    await DownloadAndExtractByAria2Async(config, update, $"增量包 {update.Version}", stageStart, perUpdateSpan)
                        .ConfigureAwait(false);

                    config.CurrentVersion = update.Version;
                    LauncherConfig.SaveConfig(_baseDirectory, config);
                }

                ReportProgress("所有更新完成", $"当前版本：{config.CurrentVersion}", 100, 100);
                await Task.Delay(600).ConfigureAwait(false);
                LaunchGame(config);
            }
            catch (Exception ex)
            {
                ReportProgress("更新失败", ex.Message, null, _globalProgress);
            }
        }

        private async Task EnsureBasePackageAsync(LauncherConfig config, UpdateManifest manifest, double stageStart, double stageSpan)
        {
            var checkFile = Path.Combine(_baseDirectory, config.BaseResourceCheckFile);
            if (File.Exists(checkFile))
            {
                ReportStageProgress("基础资源检查", "检测到基础资源，跳过完整包下载", 100, stageStart, stageSpan);
                return;
            }

            if (manifest.FullPackage is null || string.IsNullOrWhiteSpace(manifest.FullPackage.DownloadUrl))
            {
                throw new InvalidOperationException("缺少基础资源且 update-manifest.json 未配置 fullPackage.downloadUrl");
            }

            await DownloadAndExtractByAria2Async(config, manifest.FullPackage, "完整资源包", stageStart, stageSpan)
                .ConfigureAwait(false);

            config.CurrentVersion = manifest.FullPackage.Version;
            LauncherConfig.SaveConfig(_baseDirectory, config);
            ReportStageProgress("基础资源检查", "完整资源包安装完成", 100, stageStart, stageSpan);
        }

        private async Task DownloadAndExtractByAria2Async(LauncherConfig config, UpdatePackage package, string displayName, double stageStart, double stageSpan)
        {
            var downloadUrls = GetDownloadUrls(package);
            var isMultiPart = downloadUrls.Count > 1;
            var tempArchive = CreateLocalCacheFile(downloadUrls[0]);
            try
            {
                ReportStageProgress($"准备下载{displayName}", package.Description, 0, stageStart, stageSpan);
                await RunAria2DownloadAsync(config.Aria2Path, downloadUrls, tempArchive, (percent, detail) =>
                {
                    var normalized = Math.Clamp(percent * 0.85, 0, 85);
                    var text = string.IsNullOrWhiteSpace(package.Description) ? detail : $"{detail}\n{package.Description}";
                    ReportStageProgress($"正在下载{displayName}", text, normalized, stageStart, stageSpan);
                }).ConfigureAwait(false);

                var extractName = isMultiPart ? $"{displayName}(分卷)" : displayName;
                ReportStageProgress($"正在解压{displayName}", "正在使用 7z 验证并解压文件，请稍候", 90, stageStart, stageSpan);
                EnsureValidArchiveFile(tempArchive, extractName, string.Join(",", downloadUrls));
                await ExtractBySevenZipAsync(config.SevenZipPath, tempArchive, extractName, string.Join(",", downloadUrls)).ConfigureAwait(false);
                ReportStageProgress($"完成{displayName}", $"已更新到版本 {package.Version}", 100, stageStart, stageSpan);
            }
            finally
            {
                if (File.Exists(tempArchive))
                {
                    File.Delete(tempArchive);
                }
            }
        }

        private static IReadOnlyList<string> GetDownloadUrls(UpdatePackage package)
        {
            if (package.DownloadUrls.Count > 0)
            {
                var urls = package.DownloadUrls.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                if (urls.Count > 0)
                {
                    return urls;
                }
            }

            if (!string.IsNullOrWhiteSpace(package.DownloadUrl))
            {
                return [package.DownloadUrl];
            }

            throw new InvalidOperationException($"版本 {package.Version} 未配置下载地址");
        }

        private async Task RunAria2DownloadAsync(string aria2Path, IReadOnlyList<string> urls, string outputFile, Action<double, string> onProgress)
        {
            if (urls.Count == 1)
            {
                await DownloadSingleFileByAria2Async(aria2Path, urls[0], outputFile, onProgress).ConfigureAwait(false);
                return;
            }

            var cacheDirectory = Path.GetDirectoryName(outputFile) ?? _baseDirectory;
            var firstPartFileName = Path.GetFileName(outputFile);
            var firstPartExtension = Path.GetExtension(firstPartFileName);
            var filePrefix = firstPartFileName.EndsWith(firstPartExtension, StringComparison.OrdinalIgnoreCase)
                ? firstPartFileName[..^firstPartExtension.Length]
                : firstPartFileName;

            for (var i = 0; i < urls.Count; i++)
            {
                var partFile = i == 0 ? outputFile : Path.Combine(cacheDirectory, $"{filePrefix}.{i + 1:D3}");
                var partTitle = $"分卷 {i + 1}/{urls.Count}";
                await DownloadSingleFileByAria2Async(aria2Path, urls[i], partFile, (partPercent, detail) =>
                {
                    var totalPercent = ((i + (partPercent / 100d)) / urls.Count) * 100d;
                    var detailWithPart = $"{partTitle} | {detail}";
                    onProgress(totalPercent, LocalizeAria2Line(detailWithPart));
                }).ConfigureAwait(false);
            }

            onProgress(100, "下载完成");
        }

        private async Task DownloadSingleFileByAria2Async(string aria2Path, string url, string outputFile, Action<double, string> onProgress)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = aria2Path,
                Arguments = $"--allow-overwrite=true --auto-file-renaming=false --summary-interval=1 --console-log-level=notice -x 8 -s 8 --dir=\"{Path.GetDirectoryName(outputFile)}\" --out=\"{Path.GetFileName(outputFile)}\" \"{url}\"",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _baseDirectory
            };

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Start();

            var progressRegex = new Regex(@"\((\d+)%\)", RegexOptions.Compiled);

            while (!process.HasExited)
            {
                var line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    await Task.Delay(100).ConfigureAwait(false);
                    continue;
                }

                var match = progressRegex.Match(line);
                if (match.Success && double.TryParse(match.Groups[1].Value, out var value))
                {
                    onProgress(value, LocalizeAria2Line(line.Trim()));
                }
            }

            var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"aria2 下载失败，退出码 {process.ExitCode}: {stderr}");
            }

            onProgress(100, "下载完成");
        }

        private static string LocalizeAria2Line(string line)
        {
            return line
                .Replace("GiB", "GB", StringComparison.Ordinal)
                .Replace("MiB", "MB", StringComparison.Ordinal)
                .Replace("KiB", "KB", StringComparison.Ordinal)
                .Replace("CN:", "线程:", StringComparison.Ordinal)
                .Replace("DL:", "下载速度:", StringComparison.Ordinal)
                .Replace("ETA:", "剩余时间:", StringComparison.Ordinal)
                .Replace("MB/s", "MB/s", StringComparison.Ordinal);
        }

        private static string CreateLocalCacheFile(string downloadUrl)
        {
            var cacheDirectory = Path.Combine(AppContext.BaseDirectory, "cache");
            Directory.CreateDirectory(cacheDirectory);

            var extension = GetArchiveExtension(downloadUrl);
            return Path.Combine(cacheDirectory, $"update-{Guid.NewGuid():N}{extension}");
        }

        private static string GetArchiveExtension(string downloadUrl)
        {
            if (Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri))
            {
                var ext = Path.GetExtension(uri.AbsolutePath);
                if (!string.IsNullOrWhiteSpace(ext))
                {
                    return ext;
                }
            }

            return ".7z";
        }

        private static void EnsureValidArchiveFile(string filePath, string displayName, string downloadUrl)
        {
            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists || fileInfo.Length <= 0)
            {
                throw new InvalidOperationException($"{displayName} 下载文件为空，请检查下载地址是否可用: {downloadUrl}");
            }
        }

        private async Task ExtractBySevenZipAsync(string sevenZipPath, string archivePath, string displayName, string downloadUrl)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = sevenZipPath,
                Arguments = $"x -y \"{archivePath}\" -o\"{_baseDirectory}\"",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _baseDirectory
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync().ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"{displayName} 解压失败，请检查 sevenZipPath 和压缩包格式。downloadUrl: {downloadUrl}\n7z 退出码: {process.ExitCode}\n{stderr}\n{stdout}");
            }
        }

        private void LaunchGame(LauncherConfig config)
        {
            var exePath = Path.IsPathRooted(config.GameExePath)
                ? config.GameExePath
                : Path.Combine(_baseDirectory, config.GameExePath);

            if (!File.Exists(exePath))
            {
                throw new FileNotFoundException($"未找到配置的游戏启动文件: {exePath}");
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? _baseDirectory,
                UseShellExecute = true
            });

            Dispatcher.Invoke(Close);
        }

        private void ReportProgress(string title, string detail, double? stepPercent = null, double? globalPercent = null)
        {
            Dispatcher.Invoke(() =>
            {
                StepTitleText.Text = title;
                StepDetailText.Text = detail ?? string.Empty;

                if (stepPercent.HasValue)
                {
                    var clampedStep = Math.Clamp(stepPercent.Value, 0, 100);
                    StepProgressBar.Value = clampedStep;
                    StepPercentText.Text = $"{Math.Floor(clampedStep)}%";
                }

                if (globalPercent.HasValue)
                {
                    _globalProgress = Math.Clamp(globalPercent.Value, 0, 100);
                    TotalProgressBar.Value = _globalProgress;
                    TotalPercentText.Text = $"{Math.Floor(_globalProgress)}%";
                }
            });
        }

        private void ReportStageProgress(string title, string detail, double stepPercent, double stageStart, double stageSpan)
        {
            var normalizedStep = Math.Clamp(stepPercent, 0, 100);
            var global = stageStart + (stageSpan * (normalizedStep / 100d));
            ReportProgress(title, detail, normalizedStep, global);
        }
    }
}
