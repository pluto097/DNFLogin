using AduSkin.Controls.Metro;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DNFLogin
{
    public partial class LoginWindow : AduWindow
    {
        private static readonly HttpClient HttpClient = new();
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };
        private static readonly Uri DefaultBackgroundUri = new("pack://application:,,,/Assets/default-bg-img.png", UriKind.Absolute);

        private readonly string _backgroundApiUrl;
        private readonly string _loginApiUrl;
        private readonly string _assistConfigUrl;
        private ImageBrush? _backgroundBrush;
        private readonly DispatcherTimer _backgroundRotationTimer;
        private readonly DispatcherTimer _statusAutoHideTimer;
        private IReadOnlyList<string> _backgroundImageUrls = Array.Empty<string>();
        private int _backgroundImageIndex;
        private bool _rotationInitialized;
        private bool _hasShown;
        private bool _isLoggingIn;
        private RegisterWindow? _registerWindow;

        public LoginWindow()
        {
            InitializeComponent();
            Title = LoginConfig.GetWindowTitle();
            SetDefaultBackground();
            var baseUrl = LoginConfig.GetBaseUrl().TrimEnd('/');
            _backgroundApiUrl = $"{baseUrl}/api/v1/client/big-pic-list";
            _loginApiUrl = $"{baseUrl}/api/v1/client/login";
            _assistConfigUrl = $"{baseUrl}/api/v1/admin/assist/config-text";
            _backgroundRotationTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(8)
            };
            _backgroundRotationTimer.Tick += OnBackgroundRotationTick;

            _statusAutoHideTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _statusAutoHideTimer.Tick += (_, _) => HideStatus();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _ = LoadBackgroundAsync();
            LoadSavedCredentials();
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
            _backgroundRotationTimer.Stop();
            _statusAutoHideTimer.Stop();
            base.OnClosing(e);
        }

        private async Task LoadBackgroundAsync()
        {
            try
            {
                var json = await HttpClient.GetStringAsync(_backgroundApiUrl).ConfigureAwait(false);
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

        private async Task<bool> TryUpdateAssistConfigAsync()
        {
            try
            {
                using var response = await HttpClient.GetAsync(_assistConfigUrl).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return false;
                }

                var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var targetPath = Path.Combine(AppContext.BaseDirectory, "DNF.toml");
                await File.WriteAllTextAsync(targetPath, content ?? string.Empty).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"无法更新配置文件: {ex}");
                return false;
            }
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

            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

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

        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoggingIn)
            {
                return;
            }

            var account = AccountTextBox.Text?.Trim() ?? string.Empty;
            var password = PasswordBox.Password ?? string.Empty;

            if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(password))
            {
                UpdateStatus("账号或密码不能为空", Brushes.OrangeRed, true);
                return;
            }

            var button = sender as System.Windows.Controls.Button ?? LoginButton;
            button.IsEnabled = false;
            _isLoggingIn = true;
            UpdateStatus("正在登录...", Brushes.White, false, "提示");

            try
            {
                var token = await LoginAsync(account, password).ConfigureAwait(true);
                var rememberPassword = RememberCheckBox.IsChecked == true;
                if (rememberPassword)
                {
                    var credentials = new LoginConfig.LoginCredentials(account, password, true);
                    LoginConfig.SaveCredentials(credentials);
                }
                else
                {
                    LoginConfig.SaveCredentials(LoginConfig.LoginCredentials.Empty);
                }
                var configUpdated = await TryUpdateAssistConfigAsync().ConfigureAwait(true);
                if (!configUpdated)
                {
                    UpdateStatus("无法更新配置文件，请稍后重试", Brushes.OrangeRed, false, "提示");
                    return;
                }

                if (TryLaunchGame(token))
                {
                    UpdateStatus("登录成功，正在启动游戏...", Brushes.LawnGreen, true, "成功");
                }
            }
            catch (Exception ex)
            {
                UpdateStatus(ex.Message, Brushes.OrangeRed, false, "登录失败");
            }
            finally
            {
                _isLoggingIn = false;
                button.IsEnabled = true;
            }
        }

        private bool TryLaunchGame(string token)
        {
            try
            {
                var exePath = Path.Combine(AppContext.BaseDirectory, "DNF.exe");
                if (!File.Exists(exePath))
                {
                    UpdateStatus("未找到 DNF.exe，请确认文件已放在程序目录", Brushes.OrangeRed, false, "提示");
                    return false;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = token,
                    UseShellExecute = false,
                    CreateNoWindow = false,
                    WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory
                };

                Process.Start(startInfo);
                return true;
            }
            catch (Exception ex)
            {
                UpdateStatus($"无法启动游戏: {ex.Message}", Brushes.OrangeRed, false, "提示");
                return false;
            }
        }

        private void RegisterButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoggingIn)
            {
                return;
            }

            if (_registerWindow is { IsVisible: true })
            {
                _registerWindow.Activate();
                return;
            }

            _registerWindow = new RegisterWindow
            {
                Owner = this
            };

            _registerWindow.Closed += (_, _) =>
            {
                _registerWindow = null;
                Show();
                Activate();
            };

            _registerWindow.Show();
            Hide();
        }

        private async Task<string> LoginAsync(string account, string password)
        {
            var payload = new LoginRequest(account, password);
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await HttpClient.PostAsync(_loginApiUrl, content).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var success = JsonSerializer.Deserialize<LoginResponse>(responseBody, JsonOptions);
                if (string.IsNullOrWhiteSpace(success?.Token))
                {
                    throw new InvalidOperationException("登录成功但未返回有效 token");
                }

                return success.Token;
            }

            var error = JsonSerializer.Deserialize<LoginErrorResponse>(responseBody, JsonOptions);
            var message = string.IsNullOrWhiteSpace(error?.Message)
                ? $"登录失败，状态码：{(int)response.StatusCode}"
                : error!.Message!;
            throw new InvalidOperationException(message);
        }

        private void LoadSavedCredentials()
        {
            var credentials = LoginConfig.GetSavedCredentials();

            if (credentials.RememberPassword)
            {
                AccountTextBox.Text = credentials.Account ?? string.Empty;
                PasswordBox.Password = credentials.Password ?? string.Empty;
                RememberCheckBox.IsChecked = true;
            }
            else
            {
                RememberCheckBox.IsChecked = false;
            }
        }

        private void UpdateStatus(string message, Brush brush, bool autoHide = false, string? title = null)
        {
            _statusAutoHideTimer.Stop();

            if (string.IsNullOrWhiteSpace(message))
            {
                HideStatus();
                return;
            }

            LoginStatusTitle.Text = string.IsNullOrWhiteSpace(title) ? "提示" : title;
            LoginStatusText.Text = message;
            LoginStatusText.Foreground = brush;
            LoginStatusOverlay.Visibility = Visibility.Visible;

            if (autoHide)
            {
                _statusAutoHideTimer.Start();
            }
        }

        private void HideStatus()
        {
            _statusAutoHideTimer.Stop();
            LoginStatusOverlay.Visibility = Visibility.Collapsed;
            LoginStatusText.Text = string.Empty;
        }

        private void LoginStatusCloseButton_Click(object sender, RoutedEventArgs e)
        {
            HideStatus();
        }

        private void LoginStatusOverlay_MouseDown(object sender, MouseButtonEventArgs e)
        {
            HideStatus();
        }

        private void LoginStatusPanel_MouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
        }

        private sealed record BackgroundImageItem(
            [property: JsonPropertyName("id")] int Id,
            [property: JsonPropertyName("title")] string? Title,
            [property: JsonPropertyName("imageUrl")] string? ImageUrl);

        private sealed record LoginRequest(
            [property: JsonPropertyName("accountname")] string Account,
            [property: JsonPropertyName("password")] string Password);

        private sealed record LoginResponse(
            [property: JsonPropertyName("token")] string? Token);

        private sealed record LoginErrorResponse(
            [property: JsonPropertyName("message")] string? Message);
    }
}
