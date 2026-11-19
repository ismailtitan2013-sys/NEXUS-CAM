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

    public MainWindow()
    {
        InitializeComponent();
        CameraSelector.ItemsSource = new[] { "Камера 0", "Камера 1", "Камера 2", "Камера 3" };
        CameraSelector.SelectedIndex = 0;
        ApplyMirror();
        Loaded += (_, _) => CameraStatus.Text = "Готово к запуску камеры";
        Closed += (_, _) => StopCamera();
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
        CameraStatus.Text = $"Камера работает • {width}×{height} • целевые {fps} FPS";
        PreviewBadge.Text = "CAMERA LIVE";
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

                    BitmapSource bitmap = BitmapSourceConverter.ToBitmapSource(frame);
                    bitmap.Freeze();
                    await Dispatcher.InvokeAsync(() => CameraImage.Source = bitmap);
                    await Task.Delay(Math.Max(1, 1000 / fps), token);
                }
            }, CancellationToken.None);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            CameraStatus.Text = "Ошибка камеры: " + ex.Message;
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
            CameraStatus.Text = "Изображение фона выбрано • AI-замена пока не включена";
        }
        catch (Exception ex)
        {
            CameraStatus.Text = "Не удалось открыть изображение: " + ex.Message;
        }
    }
}
