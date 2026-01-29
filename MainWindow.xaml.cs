using AduSkin.Controls.Metro;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.ComponentModel;

namespace DNFLogin
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : AduWindow
    {
        private static readonly HttpClient HttpClient = new();
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };
        private static readonly Uri DefaultBackgroundUri = new("pack://application:,,,/Assets/default-bg-img.png", UriKind.Absolute);

        private readonly string _backgroundApiUrl;
        private readonly string _fullPackageApiUrl;
        private readonly string _versionApiUrlTemplate;
        private ImageBrush? _backgroundBrush;
        private readonly DispatcherTimer _backgroundRotationTimer;
        private IReadOnlyList<string> _backgroundImageUrls = Array.Empty<string>();
        private int _backgroundImageIndex;
        private bool _rotationInitialized;
        private bool _hasShown;
        private bool _loginWindowOpened;
        private double _globalProgress;
        private bool _updateStarted;

        public MainWindow()
        {
            InitializeComponent();
            Title = LoginConfig.GetWindowTitle();
            SetDefaultBackground();
            var baseUrl = LoginConfig.GetBaseUrl().TrimEnd('/');
            _backgroundApiUrl = $"{baseUrl}/api/v1/client/big-pic-list";
            _fullPackageApiUrl = $"{baseUrl}/api/v1/client/full-package";
            _versionApiUrlTemplate = $"{baseUrl}/api/v1/client/version";
            _backgroundRotationTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(10)
            };
            _backgroundRotationTimer.Tick += OnBackgroundRotationTick;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _ = LoadBackgroundAsync();
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

        private async Task LoadBackgroundAsync()
        {
            try
            {
                var json = await HttpClient.GetStringAsync(_backgroundApiUrl).ConfigureAwait(false);
                Debug.WriteLine($"背景接口响应: {json}");
                var items = JsonSerializer.Deserialize<List<BackgroundImageItem>>(json, JsonOptions);
                var imageUrls = items?
                    .Select(i => i.ImageUrl)
                    .Where(url => Uri.TryCreate(url, UriKind.Absolute, out _))
                    .Distinct()
                    .ToList();

                if (imageUrls is null || imageUrls.Count == 0)
                {
                    return;
                }

                _backgroundImageUrls = imageUrls;
                _backgroundImageIndex = _backgroundImageUrls.Count - 1;

                await StartBackgroundRotationAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"无法加载背景图片: {ex}");
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            _backgroundRotationTimer.Stop();
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

        private async void OnBackgroundRotationTick(object? sender, EventArgs e)
        {
            if (_backgroundImageUrls.Count <= 1)
            {
                _backgroundRotationTimer.Stop();
                return;
            }

            _backgroundImageIndex--;
            if (_backgroundImageIndex < 0)
            {
                _backgroundImageIndex = _backgroundImageUrls.Count - 1;
            }

            try
            {
                await TrySetBackgroundFromUrlAsync(_backgroundImageUrls[_backgroundImageIndex]);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"无法切换背景图片: {ex}");
            }
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

        private void SetBackgroundImage(byte[] imageBytes)
        {
            if (TryGetBackgroundBrush() is not ImageBrush brush)
            {
                return;
            }

            using var memory = new MemoryStream(imageBytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = memory;
            bitmap.EndInit();
            bitmap.Freeze();

            brush.ImageSource = bitmap;
        }

        private async Task TrySetBackgroundFromUrlAsync(string imageUrl)
        {
            if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri))
            {
                return;
            }

            Debug.WriteLine($"切换背景: {uri}");
            var imageBytes = await HttpClient.GetByteArrayAsync(uri).ConfigureAwait(false);

            await AnimateBrushOpacityAsync(0.0, 0.2).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() => SetBackgroundImage(imageBytes));
            await AnimateBrushOpacityAsync(1.0, 0.2).ConfigureAwait(false);
        }

        private Task AnimateBrushOpacityAsync(double toOpacity, double durationSeconds)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            void StartAnimation()
            {
                if (TryGetBackgroundBrush() is not ImageBrush brush)
                {
                    tcs.TrySetResult(true);
                    return;
                }

                var animation = new DoubleAnimation
                {
                    To = toOpacity,
                    Duration = TimeSpan.FromSeconds(durationSeconds),
                    FillBehavior = FillBehavior.HoldEnd
                };

                animation.Completed += (_, _) => tcs.TrySetResult(true);
                brush.BeginAnimation(Brush.OpacityProperty, animation);
            }

            if (Dispatcher.CheckAccess())
            {
                StartAnimation();
            }
            else
            {
                _ = Dispatcher.InvokeAsync(StartAnimation);
            }

            return tcs.Task;
        }

        private async Task StartBackgroundRotationAsync()
        {
            if (_rotationInitialized)
            {
                return;
            }

            _rotationInitialized = true;

            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);

            if (_backgroundImageUrls.Count == 0)
            {
                return;
            }

            await TrySetBackgroundFromUrlAsync(_backgroundImageUrls[_backgroundImageIndex]).ConfigureAwait(false);

            if (_backgroundImageUrls.Count > 1 && !_backgroundRotationTimer.IsEnabled)
            {
                _backgroundRotationTimer.Start();
            }
        }

        private async Task RunUpdateFlowAsync()
        {
            if (_updateStarted)
            {
                return;
            }

            _updateStarted = true;

            const double baseSpan = 40d;
            const double incrementalSpan = 60d;

            try
            {
                ReportStageProgress("正在初始化", "准备检查本地资源", 0, 0, baseSpan);
                await EnsureBasePackageAsync(0, baseSpan).ConfigureAwait(false);

                var currentVersion = LoginConfig.GetGameVersion();
                ReportProgress("正在检查版本", $"当前版本：{currentVersion}", 100, baseSpan);
                var updates = await FetchIncrementalUpdatesAsync(currentVersion).ConfigureAwait(false);

                if (updates.Count == 0)
                {
                    ReportProgress("已是最新版本", $"当前版本：{currentVersion}", 100, 100);
                    await Task.Delay(600).ConfigureAwait(false);
                    OpenLoginWindow();
                    return;
                }

                var perUpdateSpan = updates.Count > 0 ? incrementalSpan / updates.Count : incrementalSpan;

                for (var i = 0; i < updates.Count; i++)
                {
                    var update = updates[i];
                    var versionLabel = update.Version ?? $"补丁{i + 1}";
                    var stageStart = baseSpan + (perUpdateSpan * i);

                    var description = FormatUpdateDescription(update.Description);
                    ReportStageProgress("正在准备更新", CombineDetail($"即将更新至 {versionLabel}", description), 0, stageStart, perUpdateSpan);
                    await DownloadAndExtractAsync(update.DownloadUrl!, $"资源包 {versionLabel}", stageStart, perUpdateSpan, description).ConfigureAwait(false);
                    LoginConfig.SetGameVersion(update.Version);
                    ReportStageProgress("更新完成", CombineDetail($"已安装版本 {versionLabel}", description), 100, stageStart, perUpdateSpan);
                }

                ReportProgress("所有更新完成", "即将进入登录界面", 100, 100);
                await Task.Delay(600).ConfigureAwait(false);
                OpenLoginWindow();
            }
            catch (Exception ex)
            {
                ReportProgress("更新失败", ex.Message, null, _globalProgress);
            }
        }

        private async Task EnsureBasePackageAsync(double stageStart, double stageSpan)
        {
            var scriptPath = Path.Combine(AppContext.BaseDirectory, "Script.pvf");
            if (File.Exists(scriptPath))
            {
                LoginConfig.GetGameVersion();
                ReportStageProgress("正在校验完整性", "检测到基础资源，跳过完整包下载", 100, stageStart, stageSpan);
                return;
            }

            ReportStageProgress("正在校验完整性", "缺少基础资源，准备下载完整包", 10, stageStart, stageSpan);
            var package = await FetchFullPackageInfoAsync().ConfigureAwait(false)
                          ?? throw new InvalidOperationException("无法获取完整资源包信息");

            if (string.IsNullOrWhiteSpace(package.DownloadUrl))
            {
                throw new InvalidOperationException("完整包下载地址为空");
            }

            await DownloadAndExtractAsync(package.DownloadUrl, "完整资源包", stageStart, stageSpan).ConfigureAwait(false);

            var version = string.IsNullOrWhiteSpace(package.Version)
                ? LoginConfig.GetGameVersion()
                : package.Version!.Trim();

            LoginConfig.SetGameVersion(version);
            ReportStageProgress("正在校验完整性", "完整资源包更新完成", 100, stageStart, stageSpan);
        }

        private async Task<FullPackageResponse?> FetchFullPackageInfoAsync()
        {
            var json = await HttpClient.GetStringAsync(_fullPackageApiUrl).ConfigureAwait(false);
            return JsonSerializer.Deserialize<FullPackageResponse>(json, JsonOptions);
        }

        private async Task<List<IncrementalUpdate>> FetchIncrementalUpdatesAsync(string currentVersion)
        {
            var url = $"{_versionApiUrlTemplate}/{Uri.EscapeDataString(currentVersion)}";
            var json = await HttpClient.GetStringAsync(url).ConfigureAwait(false);
            var updates = JsonSerializer.Deserialize<List<IncrementalUpdate>>(json, JsonOptions) ?? new List<IncrementalUpdate>();

            return updates
                .Where(u => !string.IsNullOrWhiteSpace(u?.DownloadUrl))
                .OrderBy(u => u!.Version, StringComparer.OrdinalIgnoreCase)
                .ToList()!;
        }

        private async Task DownloadAndExtractAsync(string downloadUrl, string displayName, double stageStart, double stageSpan, string? descriptionDetail = null)
        {
            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                throw new InvalidOperationException("下载地址无效");
            }

            var tempFile = CreateLocalCacheFile();
            try
            {
                using var response = await HttpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                var buffer = new byte[81920];

                await using (var destination = File.OpenWrite(tempFile))
                await using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                {
                    long read = 0;
                    int bytesRead;
                    while ((bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false)) > 0)
                    {
                        await destination.WriteAsync(buffer.AsMemory(0, bytesRead)).ConfigureAwait(false);
                        read += bytesRead;

                        if (totalBytes > 0)
                        {
                            var percent = Math.Clamp((read / (double)totalBytes) * 80d, 0d, 80d);
                            var detail = CombineDetail($"下载 {displayName}: {FormatSize(read)} / {FormatSize(totalBytes)}", descriptionDetail);
                            ReportStageProgress($"正在下载{displayName}", detail, percent, stageStart, stageSpan);
                        }
                        else
                        {
                            var detail = CombineDetail($"下载 {displayName}: {FormatSize(read)}", descriptionDetail);
                            ReportStageProgress($"正在下载{displayName}", detail, 60, stageStart, stageSpan);
                        }
                    }
                }

                ReportStageProgress($"正在下载{displayName}", CombineDetail($"下载 {displayName} 完成", descriptionDetail), 85, stageStart, stageSpan);
                ReportStageProgress($"正在解压{displayName}", CombineDetail($"正在解压 {displayName}", descriptionDetail), 90, stageStart, stageSpan);
                ZipFile.ExtractToDirectory(tempFile, AppContext.BaseDirectory, true);
                ReportStageProgress($"正在解压{displayName}", CombineDetail($"完成解压 {displayName}", descriptionDetail), 100, stageStart, stageSpan);
            }
            finally
            {
                try
                {
                    File.Delete(tempFile);
                }
                catch
                {
                    // ignored
                }
            }
        }

        private static string CreateLocalCacheFile()
        {
            var cacheDirectory = Path.Combine(AppContext.BaseDirectory, "cache");
            Directory.CreateDirectory(cacheDirectory);
            return Path.Combine(cacheDirectory, $"update-{Guid.NewGuid():N}.zip");
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

        private static string FormatSize(long bytes)
        {
            if (bytes <= 0)
            {
                return "0 B";
            }

            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            var unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }

            return $"{size:0.##} {units[unit]}";
        }

        private static string FormatUpdateDescription(string? description)
        {
            return string.IsNullOrWhiteSpace(description)
                ? "暂无更新说明"
                : $"更新说明：{description.Trim()}";
        }

        private static string CombineDetail(string primary, string? extra)
        {
            return string.IsNullOrWhiteSpace(extra) ? primary : $"{primary}\n{extra}";
        }

        private void OpenLoginWindow()
        {
            if (_loginWindowOpened)
            {
                return;
            }

            _loginWindowOpened = true;

            Dispatcher.InvokeAsync(() =>
            {
                var loginWindow = new LoginWindow();
                Application.Current.MainWindow = loginWindow;
                loginWindow.Show();
                Close();
            });
        }


        private sealed record BackgroundImageItem(
            [property: JsonPropertyName("id")] int Id,
            [property: JsonPropertyName("title")] string? Title,
            [property: JsonPropertyName("imageUrl")] string? ImageUrl);

        private sealed record FullPackageResponse(
            [property: JsonPropertyName("version")] string? Version,
            [property: JsonPropertyName("downloadUrl")] string? DownloadUrl);

        private sealed record IncrementalUpdate(
            [property: JsonPropertyName("version")] string? Version,
            [property: JsonPropertyName("downloadUrl")] string? DownloadUrl,
            [property: JsonPropertyName("description")] string? Description);
    }
}