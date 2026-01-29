using AduSkin.Controls.Metro;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DNFLogin
{
    public partial class RegisterWindow : AduWindow
    {
        private static readonly HttpClient HttpClient = new();
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };
        private static readonly Uri DefaultBackgroundUri = new("pack://application:,,,/Assets/default-bg-img.png", UriKind.Absolute);
        private static readonly Random Rng = new();

        private readonly string _backgroundApiUrl;
        private readonly string _registerApiUrl;
        private readonly string _captchaApiFormat;
        private ImageBrush? _backgroundBrush;
        private readonly DispatcherTimer _backgroundRotationTimer;
        private readonly DispatcherTimer _statusAutoHideTimer;
        private IReadOnlyList<string> _backgroundImageUrls = Array.Empty<string>();
        private int _backgroundImageIndex;
        private bool _rotationInitialized;
        private bool _hasShown;
        private bool _isSubmitting;
        private string _validationIndex = string.Empty;

        public RegisterWindow()
        {
            InitializeComponent();
            Title = LoginConfig.GetWindowTitle();
            SetDefaultBackground();
            var baseUrl = LoginConfig.GetBaseUrl().TrimEnd('/');
            _backgroundApiUrl = $"{baseUrl}/api/v1/client/big-pic-list";
            _registerApiUrl = $"{baseUrl}/api/v1/client/register";
            _captchaApiFormat = $"{baseUrl}/api/v1/vc/img/{{0}}";

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
            _ = RefreshCaptchaAsync();
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

        private async void CaptchaImage_MouseDown(object sender, MouseButtonEventArgs e)
        {
            await RefreshCaptchaAsync();
        }

        private async Task RefreshCaptchaAsync()
        {
            try
            {
                _validationIndex = GenerateValidationIndex();
                var uri = string.Format(_captchaApiFormat, _validationIndex);
                var bytes = await HttpClient.GetByteArrayAsync(uri).ConfigureAwait(false);

                await Dispatcher.InvokeAsync(() =>
                {
                    using var memory = new MemoryStream(bytes);
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = memory;
                    bitmap.EndInit();
                    bitmap.Freeze();

                    CaptchaImage.Source = bitmap;
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"无法加载验证码: {ex}");
                await Dispatcher.InvokeAsync(() =>
                    ShowStatus("验证码加载失败，请稍后重试", Brushes.OrangeRed, true, "提示"));
            }
        }

        private static string GenerateValidationIndex()
        {
            return Rng.Next(100000, 999999).ToString();
        }

        private async void RegisterButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isSubmitting)
            {
                return;
            }

            var account = AccountTextBox.Text?.Trim() ?? string.Empty;
            var password = PasswordBox.Password ?? string.Empty;
            var confirmPassword = ConfirmPasswordBox.Password ?? string.Empty;
            var captcha = CaptchaTextBox.Text?.Trim() ?? string.Empty;
            var recommender = RecommenderTextBox.Text?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(password))
            {
                ShowStatus("账号和密码不能为空", Brushes.OrangeRed, true, "提示");
                return;
            }

            if (!string.Equals(password, confirmPassword, StringComparison.Ordinal))
            {
                ShowStatus("两次输入的密码不一致", Brushes.OrangeRed, true, "提示");
                return;
            }

            if (string.IsNullOrWhiteSpace(captcha))
            {
                ShowStatus("请输入验证码", Brushes.OrangeRed, true, "提示");
                return;
            }

            if (string.IsNullOrWhiteSpace(_validationIndex))
            {
                await RefreshCaptchaAsync();
            }

            RegisterSubmitButton.IsEnabled = false;
            _isSubmitting = true;
            ShowStatus("正在提交...", Brushes.White, false, "提示");

            try
            {
                await RegisterAsync(account, password, captcha, recommender).ConfigureAwait(true);
                ShowStatus("注册成功", Brushes.LawnGreen, true, "成功");
            }
            catch (Exception ex)
            {
                ShowStatus(ex.Message, Brushes.OrangeRed, false, "注册失败");
                await RefreshCaptchaAsync();
            }
            finally
            {
                _isSubmitting = false;
                RegisterSubmitButton.IsEnabled = true;
            }
        }

        private void BackToLogin_Click(object sender, RoutedEventArgs e)
        {
            if (Owner is LoginWindow loginWindow)
            {
                loginWindow.Show();
                loginWindow.Activate();
            }
            else
            {
                var window = new LoginWindow();
                window.Show();
            }

            Close();
        }

        private async Task RegisterAsync(string account, string password, string captcha, string recommender)
        {
            var payload = new RegisterRequest(account, password, captcha, _validationIndex, recommender);
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await HttpClient.PostAsync(_registerApiUrl, content).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return;
            }

            var error = JsonSerializer.Deserialize<RegisterErrorResponse>(body, JsonOptions);
            var message = string.IsNullOrWhiteSpace(error?.Message)
                ? $"注册失败，状态码：{(int)response.StatusCode}"
                : error!.Message!;
            throw new InvalidOperationException(message);
        }

        private void ShowStatus(string message, Brush brush, bool autoHide, string title)
        {
            _statusAutoHideTimer.Stop();

            StatusTitleText.Text = title;
            StatusBodyText.Text = message;
            StatusBodyText.Foreground = brush;
            RegisterStatusOverlay.Visibility = Visibility.Visible;

            if (autoHide)
            {
                _statusAutoHideTimer.Start();
            }
        }

        private void HideStatus()
        {
            _statusAutoHideTimer.Stop();
            RegisterStatusOverlay.Visibility = Visibility.Collapsed;
            StatusBodyText.Text = string.Empty;
        }

        private void StatusCloseButton_Click(object sender, RoutedEventArgs e)
        {
            HideStatus();
        }

        private void StatusOverlay_MouseDown(object sender, MouseButtonEventArgs e)
        {
            HideStatus();
        }

        private void StatusPanel_MouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
        }

        private sealed record BackgroundImageItem(
            [property: JsonPropertyName("id")] int Id,
            [property: JsonPropertyName("title")] string? Title,
            [property: JsonPropertyName("imageUrl")] string? ImageUrl);

        private sealed record RegisterRequest(
            [property: JsonPropertyName("accountname")] string Account,
            [property: JsonPropertyName("password")] string Password,
            [property: JsonPropertyName("valicode")] string Captcha,
            [property: JsonPropertyName("validationIndex")] string ValidationIndex,
            [property: JsonPropertyName("recommender")] string Recommender);

        private sealed record RegisterErrorResponse(
            [property: JsonPropertyName("message")] string? Message);
    }
}
