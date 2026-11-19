using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
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
        CameraSelector.ItemsSource = new[] { "Камера 0", "Камера 1", "Камера 2" };
        CameraSelector.SelectedIndex = 0;
        Loaded += (_, _) => CameraStatus.Text = "Готово к запуску локального предпросмотра";
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
            CameraStatus.Text = "Не удалось открыть камеру. Проверь разрешение Windows.";
            return;
        }

        capture.Set(VideoCaptureProperties.FrameWidth, 640);
        capture.Set(VideoCaptureProperties.FrameHeight, 360);
        capture.Set(VideoCaptureProperties.Fps, 15);

        _capture = capture;
        _captureCancellation = new CancellationTokenSource();
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        CameraSelector.IsEnabled = false;
        CameraStatus.Text = "Камера работает • 640×360 • целевые 15 FPS";

        var token = _captureCancellation.Token;
        await Task.Run(async () =>
        {
            using var frame = new Mat();
            while (!token.IsCancellationRequested)
            {
                if (_capture is null || !_capture.Read(frame) || frame.Empty())
                {
                    await Task.Delay(100, token).ContinueWith(_ => { });
                    continue;
                }

                BitmapSource bitmap = BitmapSourceConverter.ToBitmapSource(frame);
                bitmap.Freeze();
                await Dispatcher.InvokeAsync(() => CameraImage.Source = bitmap);
                await Task.Delay(20, token).ContinueWith(_ => { });
            }
        }, CancellationToken.None);
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
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        CameraSelector.IsEnabled = true;
    }
}
