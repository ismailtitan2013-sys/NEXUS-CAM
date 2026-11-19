using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using Window = System.Windows.Window;

namespace NexusCam;

public partial class MainWindow : Window
{
    private VideoCapture? _capture;
    private CancellationTokenSource? _captureCancellation;
    private PortraitBackgroundProcessor? _portraitProcessor;
    private Mat? _backgroundMat;
    private string? _backgroundPath;
    private volatile bool _aiEnabled;

    public MainWindow()
    {
        InitializeComponent();
        CameraSelector.ItemsSource = new[] { "Камера 0", "Камера 1", "Камера 2", "Камера 3" };
        CameraSelector.SelectedIndex = 0;
        ApplyMirror();
        Loaded += (_, _) => CameraStatus.Text = "Готово к запуску камеры";
        Closed += (_, _) =>
        {
            StopCamera();
            _portraitProcessor?.Dispose();
            _backgroundMat?.Dispose();
        };
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_captureCancellation is not null) return;

        int cameraIndex = Math.Max(0, CameraSelector.SelectedIndex);
        var capture = new VideoCapture(cameraIndex, VideoCaptureAPIs.DSHOW);
        if (!capture.IsOpened())
        {
            capture.Dispose();
            CameraStatus.Text = "Не удалось открыть камеру. Проверь разрешения Windows.";
            return;
        }

        int resolution = ResolutionSelector.SelectedIndex;
        int width = resolution == 2 ? 1920 : resolution == 1 ? 1280 : 640;
        int height = resolution == 2 ? 1080 : resolution == 1 ? 720 : 360;
        int fps = FpsSelector.SelectedIndex == 2 ? 30 : FpsSelector.SelectedIndex == 0 ? 15 : 24;

        capture.Set(VideoCaptureProperties.FrameWidth, width);
        capture.Set(VideoCaptureProperties.FrameHeight, height);
        capture.Set(VideoCaptureProperties.Fps, fps);

        _capture = capture;
        _captureCancellation = new CancellationTokenSource();
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        CameraSelector.IsEnabled = false;
        ResolutionSelector.IsEnabled = false;
        FpsSelector.IsEnabled = false;
        CameraStatus.Text = _aiEnabled
            ? "Камера работает • AI-замена фона включена"
            : $"Камера работает • {width}×{height} • целевые {fps} FPS";
        PreviewBadge.Text = _aiEnabled ? "AI BACKGROUND LIVE" : "CAMERA LIVE";
        LiveDot.Fill = new SolidColorBrush(Color.FromRgb(117, 224, 177));

        var token = _captureCancellation.Token;
        try
        {
            await Task.Run(async () =>
            {
                using var frame = new Mat();
                while (!token.IsCancellationRequested)
                {
                    var activeCapture = _capture;
                    if (activeCapture is null || !activeCapture.Read(frame) || frame.Empty())
                    {
                        await Task.Delay(80, token);
                        continue;
                    }

                    BitmapSource bitmap;
                    if (_aiEnabled && _portraitProcessor is not null && _backgroundMat is not null)
                    {
                        using var processed = _portraitProcessor.ReplaceBackground(frame, _backgroundMat);
                        bitmap = BitmapSourceConverter.ToBitmapSource(processed);
                    }
                    else
                    {
                        bitmap = BitmapSourceConverter.ToBitmapSource(frame);
                    }

                    bitmap.Freeze();
                    await Dispatcher.InvokeAsync(() => CameraImage.Source = bitmap);
                    await Task.Delay(Math.Max(1, 1000 / fps), token);
                }
            }, CancellationToken.None);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() => CameraStatus.Text = "Ошибка камеры/AI: " + ex.Message);
            StopCamera();
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e) => StopCamera();

    private void StopCamera()
    {
        _captureCancellation?.Cancel();
        _captureCancellation?.Dispose();
        _captureCancellation = null;
        _capture?.Release();
        _capture?.Dispose();
        _capture = null;

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ResetUi);
            return;
        }
        ResetUi();
    }

    private void ResetUi()
    {
        CameraImage.Source = null;
        CameraStatus.Text = "Предпросмотр остановлен";
        PreviewBadge.Text = "CAMERA OFFLINE";
        LiveDot.Fill = new SolidColorBrush(Color.FromRgb(119, 132, 155));
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        CameraSelector.IsEnabled = true;
        ResolutionSelector.IsEnabled = true;
        FpsSelector.IsEnabled = true;
    }

    private void MirrorToggle_Changed(object sender, RoutedEventArgs e) => ApplyMirror();

    private void ApplyMirror()
    {
        bool mirror = MirrorToggle?.IsChecked == true;
        CameraImage.LayoutTransform = new ScaleTransform(mirror ? -1 : 1, 1);
    }

    private void ChooseBackgroundButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выбрать изображение для фона",
            Filter = "Изображения|*.png;*.jpg;*.jpeg;*.bmp;*.webp",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(dialog.FileName);
            image.EndInit();
            image.Freeze();

            BackgroundThumbnail.Source = image;
            BackgroundName.Text = Path.GetFileName(dialog.FileName);
            _backgroundPath = dialog.FileName;
            _backgroundMat?.Dispose();
            _backgroundMat = Cv2.ImRead(dialog.FileName, ImreadModes.Color);
            if (_backgroundMat.Empty())
            {
                _backgroundMat.Dispose();
                _backgroundMat = null;
                throw new InvalidDataException("Файл изображения не удалось прочитать.");
            }
            CameraStatus.Text = "Фон выбран. Можно включить AI-замену.";
            AiStatus.Text = "Фон готов. Нажми кнопку ниже, чтобы загрузить AI-модель.";
        }
        catch (Exception ex)
        {
            CameraStatus.Text = "Не удалось открыть изображение: " + ex.Message;
        }
    }

    private async void AiButton_Click(object sender, RoutedEventArgs e)
    {
        if (_aiEnabled)
        {
            _aiEnabled = false;
            AiButton.Content = "✨  Включить AI-замену фона";
            AiStatus.Text = "AI-замена выключена.";
            PreviewBadge.Text = _captureCancellation is null ? "CAMERA OFFLINE" : "CAMERA LIVE";
            return;
        }

        if (string.IsNullOrWhiteSpace(_backgroundPath) || _backgroundMat is null || _backgroundMat.Empty())
        {
            AiStatus.Text = "Сначала выбери изображение фона.";
            return;
        }

        AiButton.IsEnabled = false;
        AiStatus.Text = "Подготовка AI-модуля...";
        try
        {
            _portraitProcessor ??= await PortraitBackgroundProcessor.CreateAsync(
                message => Dispatcher.Invoke(() => AiStatus.Text = message));
            _aiEnabled = true;
            AiButton.Content = "Выключить AI-замену фона";
            AiStatus.Text = "AI-модель готова. Запусти камеру для замены фона.";
            if (_captureCancellation is not null)
                CameraStatus.Text = "AI-замена фона включена";
        }
        catch (Exception ex)
        {
            AiStatus.Text = "Не удалось загрузить AI-модель: " + ex.Message;
        }
        finally
        {
            AiButton.IsEnabled = true;
        }
    }
}
