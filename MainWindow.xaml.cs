using AduSkin.Controls.Metro;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
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
        private Process? _currentAria2Process;
        private bool _isAutoClosing;
        private volatile bool _isUserCancelling;
        private string _aria2Path = null!;
        private string _sevenZipPath = null!;

        // 【下载速度异常判定阈值】当下载速度≤2MB/s且持续时间≥30秒时，判定为当前线路异常，自动切换到下一条线路
        private const double SlowSpeedThresholdMiBps = 2.0; // 速度阈值：2MiB/s（约2MB/s）
        private const int SlowSpeedDurationSeconds = 30;     // 持续时间阈值：30秒

        public MainWindow()
        {
            InitializeComponent();
            _baseDirectory = AppContext.BaseDirectory;
            Title = "Dungeon & Fighter Launcher";
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
            if (!_isAutoClosing)
            {
                var aria2 = _currentAria2Process;
                var isDownloading = false;
                try { isDownloading = aria2 is not null && !aria2.HasExited; } catch { }

                if (isDownloading)
                {
                    var result = MessageBox.Show(
                        "正在下载更新，关闭程序将中止下载。\n确定要退出吗？",
                        "退出确认",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result != MessageBoxResult.Yes)
                    {
                        e.Cancel = true;
                        return;
                    }
                }
            }

            _isUserCancelling = true;
            KillAria2Process();
            base.OnClosing(e);
        }

        private void KillAria2Process()
        {
            try
            {
                var process = _currentAria2Process;
                if (process is not null && !process.HasExited)
                {
                    process.Kill();
                }
            }
            catch
            {
                // 进程可能已退出或已释放
            }
        }

        private static string ExtractEmbeddedTool(string resourceName, string outputDir)
        {
            var outputPath = Path.Combine(outputDir, resourceName);
            if (File.Exists(outputPath))
            {
                return outputPath;
            }

            Directory.CreateDirectory(outputDir);

            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"未找到嵌入资源: {resourceName}");
            using var fs = File.Create(outputPath);
            stream.CopyTo(fs);

            return outputPath;
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

                var toolsDir = Path.Combine(_baseDirectory, ".tools");
                _aria2Path = ExtractEmbeddedTool("aria2c.exe", toolsDir);
                _sevenZipPath = ExtractEmbeddedTool("7za.exe", toolsDir);

                var config = LauncherConfig.LoadOrCreate(_baseDirectory);
                var manifest = await LauncherConfig.LoadManifestFromRemoteAsync(config).ConfigureAwait(false);

                // 检查云端清单中的 configUpdateUrl，若与本地不同则同步并重新拉取清单
                (config, manifest) = await LauncherConfig.SyncConfigUpdateUrlAsync(_baseDirectory, config, manifest).ConfigureAwait(false);

                const double baseSpan = 30d;
                const double pvfSpan = 20d;
                const double incrementalSpan = 50d;

                await EnsureBasePackageAsync(config, manifest, 0, baseSpan).ConfigureAwait(false);

                // PVF 更新通道：在基础资源检查之后、常规增量更新之前执行
                await ApplyPVFUpdatesAsync(config, manifest, baseSpan, pvfSpan).ConfigureAwait(false);

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
                    var stageStart = baseSpan + pvfSpan + (perUpdateSpan * i);
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
                ReportProgress("更新失败", $"{ex.Message}\n请联系管理员获取帮助", null, _globalProgress);
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

            if (manifest.FullPackage is null)
            {
                throw new InvalidOperationException("缺少基础资源且 update-manifest.json 未配置 fullPackage.downloadUrl");
            }
            // 验证是否至少有一个下载线路
            _ = ResolveDownloadRoutes(manifest.FullPackage);

            await DownloadAndExtractByAria2Async(config, manifest.FullPackage, "完整资源包", stageStart, stageSpan)
                .ConfigureAwait(false);

            config.CurrentVersion = manifest.FullPackage.Version;
            LauncherConfig.SaveConfig(_baseDirectory, config);
            ReportStageProgress("基础资源检查", "完整资源包安装完成", 100, stageStart, stageSpan);
        }

        /// <summary>
        /// PVF 专用更新通道：独立管理 PVF 文件的增量更新。
        /// 更新失败时立即终止整个更新流程，异常向上层传播。
        /// </summary>
        private async Task ApplyPVFUpdatesAsync(LauncherConfig config, UpdateManifest manifest, double stageStart, double stageSpan)
        {
            var pvfUpdates = LauncherConfig.ResolvePendingPVFUpdates(manifest, config.PVFVersion);

            if (pvfUpdates.Count == 0)
            {
                ReportStageProgress("PVF资源检查", $"PVF资源已是最新（当前版本：{config.PVFVersion}）", 100, stageStart, stageSpan);
                return;
            }

            var perUpdateSpan = stageSpan / pvfUpdates.Count;

            for (var i = 0; i < pvfUpdates.Count; i++)
            {
                var update = pvfUpdates[i];
                var updateStageStart = stageStart + (perUpdateSpan * i);

                await DownloadAndExtractByAria2Async(config, update, $"PVF更新 {update.Version}", updateStageStart, perUpdateSpan)
                    .ConfigureAwait(false);

                config.PVFVersion = update.Version;
                LauncherConfig.SaveConfig(_baseDirectory, config);
            }

            ReportStageProgress("PVF更新完成", $"PVF资源已更新到版本 {config.PVFVersion}", 100, stageStart, stageSpan);
        }

        private async Task DownloadAndExtractByAria2Async(LauncherConfig config, UpdatePackage package, string displayName, double stageStart, double stageSpan)
        {
            var routes = ResolveDownloadRoutes(package);
            Exception? lastException = null;

            for (var routeIdx = 0; routeIdx < routes.Count; routeIdx++)
            {
                var route = routes[routeIdx];
                var routeLabel = routes.Count > 1 ? $"[{route.Name}] " : "";
                var downloadUrls = route.Urls;
                var isMultiPart = downloadUrls.Count > 1;
                var tempArchive = CreateLocalCacheFile(downloadUrls[0]);

                UpdateRouteDisplay(route.Name, route.RouteIndex, route.TotalRoutes);

                var extractionSucceeded = false;
                try
                {
                    // 【多级切换逻辑】判断是否为最后一条线路，最后一条线路不触发速度异常切换
                    var isLastRoute = routeIdx >= routes.Count - 1;

                    ReportStageProgress($"准备下载{displayName}", $"{routeLabel}{package.Description}", 0, stageStart, stageSpan);
                    await RunAria2DownloadAsync(_aria2Path, downloadUrls, tempArchive, route.IsApiRoute, isLastRoute, (percent, detail) =>
                    {
                        var normalized = Math.Clamp(percent * 0.85, 0, 85);
                        var text = string.IsNullOrWhiteSpace(package.Description)
                            ? $"{routeLabel}{detail}"
                            : $"{routeLabel}{detail}\n{package.Description}";
                        ReportStageProgress($"正在下载{displayName}", text, normalized, stageStart, stageSpan);
                    }).ConfigureAwait(false);

                    var extractName = isMultiPart ? $"{displayName}(分卷)" : displayName;
                    ReportStageProgress($"正在解压{displayName}", $"{routeLabel}正在使用 7z 验证并解压文件，请稍候", 90, stageStart, stageSpan);
                    EnsureValidArchiveFile(tempArchive, extractName, string.Join(",", downloadUrls));
                    await ExtractBySevenZipAsync(_sevenZipPath, tempArchive, extractName, string.Join(",", downloadUrls)).ConfigureAwait(false);
                    ReportStageProgress($"完成{displayName}", $"已更新到版本 {package.Version}", 100, stageStart, stageSpan);
                    extractionSucceeded = true;
                    return; // 下载解压成功，直接返回
                }
                catch (Exception ex)
                {
                    lastException = ex;

                    // 用户正在关闭程序，保留缓存文件以支持下次启动时断点续传
                    if (_isUserCancelling)
                    {
                        break;
                    }

                    if (routeIdx < routes.Count - 1)
                    {
                        // 仅在切换线路时清理缓存（不同线路 URL 不同，不能交叉续传）
                        CleanupPartialDownload(tempArchive);

                        var nextRoute = routes[routeIdx + 1];
                        // 根据异常类型显示不同的切换原因：速度过慢或下载失败
                        var switchTitle = ex is SlowSpeedSwitchException
                            ? $"{route.Name} 速度过慢"
                            : $"{route.Name} 下载失败";
                        ReportStageProgress(
                            switchTitle,
                            $"正在切换到 {nextRoute.Name}...\n原因: {ex.Message}",
                            0, stageStart, stageSpan);
                        await Task.Delay(1500).ConfigureAwait(false);
                    }
                    // 最后一条线路失败时不清理，保留 .aria2 控制文件以支持断点续传
                }
                finally
                {
                    if (extractionSucceeded)
                    {
                        CleanupCompletedDownload(tempArchive);
                    }
                }
            }

            // 所有线路均失败
            UpdateRouteDisplay(null, 0, 0);
            throw new InvalidOperationException(
                $"{displayName} 所有下载线路均失败: {lastException?.Message}", lastException);
        }

        private void CleanupPartialDownload(string tempArchive)
        {
            try
            {
                // 删除 .aria2 控制文件和部分下载的文件
                var aria2File = tempArchive + ".aria2";
                if (File.Exists(aria2File)) File.Delete(aria2File);
                if (File.Exists(tempArchive)) File.Delete(tempArchive);

                // 清理可能的分卷文件
                var cacheDirectory = Path.GetDirectoryName(tempArchive) ?? _baseDirectory;
                var firstPartFileName = Path.GetFileName(tempArchive);
                var firstPartExtension = Path.GetExtension(firstPartFileName);
                var filePrefix = firstPartFileName.EndsWith(firstPartExtension, StringComparison.OrdinalIgnoreCase)
                    ? firstPartFileName[..^firstPartExtension.Length]
                    : firstPartFileName;

                foreach (var file in Directory.GetFiles(cacheDirectory, $"{filePrefix}.*"))
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // 清理失败忽略
            }
        }

        private void CleanupCompletedDownload(string tempArchive)
        {
            try
            {
                var cacheDirectory = Path.GetDirectoryName(tempArchive) ?? _baseDirectory;
                var firstPartFileName = Path.GetFileName(tempArchive);
                var firstPartExtension = Path.GetExtension(firstPartFileName);
                var filePrefix = firstPartFileName.EndsWith(firstPartExtension, StringComparison.OrdinalIgnoreCase)
                    ? firstPartFileName[..^firstPartExtension.Length]
                    : firstPartFileName;

                var pattern = $"{filePrefix}.*";
                var files = Directory.GetFiles(cacheDirectory, pattern);

                foreach (var file in files)
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // 清理失败忽略
            }
        }

        private sealed record ResolvedRoute(string Name, IReadOnlyList<string> Urls, int RouteIndex, int TotalRoutes, bool IsApiRoute);

        private static IReadOnlyList<ResolvedRoute> ResolveDownloadRoutes(UpdatePackage package)
        {
            var routes = new List<ResolvedRoute>();

            // 优先从 downloadRoutes 中解析多条线路
            if (package.DownloadRoutes.Count > 0)
            {
                foreach (var route in package.DownloadRoutes)
                {
                    var (urls, isApiRoute) = GetUrlsFromRoute(route);
                    if (urls.Count > 0)
                    {
                        routes.Add(new ResolvedRoute(
                            string.IsNullOrWhiteSpace(route.Name) ? $"线路{routes.Count + 1}" : route.Name,
                            urls,
                            routes.Count + 1,
                            0, // TotalRoutes 稍后回填
                            isApiRoute));
                    }
                }
            }

            // 兼容旧格式：将顶层 downloadUrls / downloadUrl / apiDownloadUrl 作为备用线路
            if (routes.Count == 0)
            {
                var (legacyUrls, isApiRoute) = GetLegacyUrls(package);
                if (legacyUrls.Count > 0)
                {
                    routes.Add(new ResolvedRoute("默认线路", legacyUrls, 1, 1, isApiRoute));
                    return routes;
                }
            }

            if (routes.Count == 0)
            {
                throw new InvalidOperationException($"版本 {package.Version} 未配置下载地址");
            }

            // 回填 TotalRoutes
            var total = routes.Count;
            for (var i = 0; i < routes.Count; i++)
            {
                routes[i] = routes[i] with { TotalRoutes = total };
            }

            return routes;
        }

        private static (IReadOnlyList<string> Urls, bool IsApiRoute) GetUrlsFromRoute(DownloadRoute route)
        {
            if (route.DownloadUrls.Count > 0)
            {
                var urls = route.DownloadUrls.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                if (urls.Count > 0)
                {
                    return (urls, false);
                }
            }

            if (!string.IsNullOrWhiteSpace(route.DownloadUrl))
            {
                return ([route.DownloadUrl], false);
            }

            if (!string.IsNullOrWhiteSpace(route.ApiDownloadUrl))
            {
                return ([route.ApiDownloadUrl], true);
            }

            return ([], false);
        }

        private static (IReadOnlyList<string> Urls, bool IsApiRoute) GetLegacyUrls(UpdatePackage package)
        {
            if (package.DownloadUrls.Count > 0)
            {
                var urls = package.DownloadUrls.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                if (urls.Count > 0)
                {
                    return (urls, false);
                }
            }

            if (!string.IsNullOrWhiteSpace(package.DownloadUrl))
            {
                return ([package.DownloadUrl], false);
            }

            if (!string.IsNullOrWhiteSpace(package.ApiDownloadUrl))
            {
                return ([package.ApiDownloadUrl], true);
            }

            return ([], false);
        }

        private async Task RunAria2DownloadAsync(string aria2Path, IReadOnlyList<string> urls, string outputFile, bool isApiRoute, bool isLastRoute, Action<double, string> onProgress)
        {
            if (urls.Count == 1)
            {
                await DownloadSingleFileByAria2Async(aria2Path, urls[0], outputFile, isApiRoute, isLastRoute, onProgress).ConfigureAwait(false);
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
                await DownloadSingleFileByAria2Async(aria2Path, urls[i], partFile, isApiRoute, isLastRoute, (partPercent, detail) =>
                {
                    var totalPercent = ((i + (partPercent / 100d)) / urls.Count) * 100d;
                    var detailWithPart = $"{partTitle} | {detail}";
                    onProgress(totalPercent, LocalizeAria2Line(detailWithPart));
                }).ConfigureAwait(false);
            }

            onProgress(100, "下载完成");
        }

        private async Task DownloadSingleFileByAria2Async(string aria2Path, string url, string outputFile, bool isApiRoute, bool isLastRoute, Action<double, string> onProgress)
        {
            if (File.Exists(outputFile) && !File.Exists(outputFile + ".aria2"))
            {
                onProgress(100, "使用已下载的缓存文件");
                return;
            }

            var concurrency = isApiRoute ? "8" : "16";
            var baseArgs = $"--continue=true --auto-file-renaming=false --auto-save-interval=3 --summary-interval=1 --console-log-level=notice -x {concurrency} -s {concurrency} --file-allocation=none --min-split-size=4M -k4M";

            if (isApiRoute)
            {
                baseArgs += " --header=\"User-Agent: pan.baidu.com\" --header=\"Referer: https://pan.baidu.com/\"";
            }

            baseArgs += $" --dir=\"{Path.GetDirectoryName(outputFile)}\" --out=\"{Path.GetFileName(outputFile)}\" \"{url}\"";

            var startInfo = new ProcessStartInfo
            {
                FileName = aria2Path,
                Arguments = baseArgs,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _baseDirectory
            };

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _currentAria2Process = process;

            try
            {
                process.Start();

                var progressRegex = new Regex(@"\((\d+)%\)", RegexOptions.Compiled);
                // 用于解析aria2输出中的下载速度，如 DL:1.5MiB、DL:500KiB
                var speedRegex = new Regex(@"DL:(\d+(?:\.\d+)?)(B|KiB|MiB|GiB)", RegexOptions.Compiled);
                // 【速度异常判定】记录速度持续低于阈值的起始时间
                DateTime? slowSpeedStartTime = null;

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

                    // 【下载速度实时监控】当非最后一条线路时，监测速度是否持续低于阈值
                    if (!isLastRoute)
                    {
                        var speedMatch = speedRegex.Match(line);
                        if (speedMatch.Success && double.TryParse(speedMatch.Groups[1].Value, out var speedValue))
                        {
                            var speedMiBps = ConvertSpeedToMiBps(speedValue, speedMatch.Groups[2].Value);

                            // 【异常速度判定】速度≤2MB/s时开始计时，速度恢复则重置计时
                            if (speedMiBps <= SlowSpeedThresholdMiBps)
                            {
                                slowSpeedStartTime ??= DateTime.UtcNow;
                                var elapsed = (DateTime.UtcNow - slowSpeedStartTime.Value).TotalSeconds;
                                if (elapsed >= SlowSpeedDurationSeconds)
                                {
                                    // 【自动线路切换】速度持续过慢，终止当前aria2进程并切换到下一条线路
                                    try { process.Kill(); } catch { }
                                    throw new SlowSpeedSwitchException(
                                        $"下载速度≤{SlowSpeedThresholdMiBps}MB/s 已持续 {elapsed:F0} 秒");
                                }
                            }
                            else
                            {
                                // 速度恢复正常，重置慢速计时器
                                slowSpeedStartTime = null;
                            }
                        }
                    }
                }

                var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException($"aria2 下载失败，退出码 {process.ExitCode}: {stderr}");
                }

                onProgress(100, "下载完成");
            }
            finally
            {
                _currentAria2Process = null;
            }
        }

        private static string LocalizeAria2Line(string line) => line;

        private static string CreateLocalCacheFile(string downloadUrl)
        {
            var cacheDirectory = Path.Combine(AppContext.BaseDirectory, "cache");
            Directory.CreateDirectory(cacheDirectory);

            var extension = GetArchiveExtension(downloadUrl);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(downloadUrl)))[..16];
            return Path.Combine(cacheDirectory, $"update-{hash}{extension}");
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

            _isAutoClosing = true;
            Dispatcher.Invoke(Close);
        }

        private void ReportProgress(string title, string detail, double? stepPercent = null, double? globalPercent = null)
        {
            if (Dispatcher.HasShutdownStarted) return;
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

        private void UpdateRouteDisplay(string? routeName, int routeIndex, int totalRoutes)
        {
            if (Dispatcher.HasShutdownStarted) return;
            Dispatcher.Invoke(() =>
            {
                if (routeName is null || totalRoutes <= 1)
                {
                    RouteInfoText.Visibility = Visibility.Collapsed;
                    RouteInfoText.Text = string.Empty;
                }
                else
                {
                    RouteInfoText.Visibility = Visibility.Visible;
                    RouteInfoText.Text = $"当前下载线路: {routeName} ({routeIndex}/{totalRoutes})";
                }
            });
        }

        /// <summary>
        /// 将aria2输出的速度值转换为MiB/s单位
        /// </summary>
        private static double ConvertSpeedToMiBps(double value, string unit) => unit switch
        {
            "GiB" => value * 1024,
            "MiB" => value,
            "KiB" => value / 1024,
            "B" => value / (1024 * 1024),
            _ => 0
        };

        /// <summary>
        /// 下载速度过慢触发的线路切换异常
        /// </summary>
        private sealed class SlowSpeedSwitchException : Exception
        {
            public SlowSpeedSwitchException(string message) : base(message) { }
        }
    }
}
