using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace NexusCam;

public sealed class PortraitBackgroundProcessor : IDisposable
{
    private const string ModelUrl = "https://huggingface.co/onnx-community/modnet-webnn/resolve/9f63834f891c3fdf63550dc0823d88ff559ace5e/onnx/model.onnx";
    private const string ModelSha256 = "07C308CF0FC7E6E8B2065A12ED7FC07E1DE8FEBB7DC7839D7B7F15DD66584DF9";
    private const int ModelInputHeight = 256;
    private const double MatteRefreshRate = 7.0;

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;
    private Mat? _cachedAlpha;
    private Mat? _cachedBackground;
    private long _lastMatteTicks;
    private int _cachedWidth;
    private int _cachedHeight;

    private PortraitBackgroundProcessor(string modelPath)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = 2,
            InterOpNumThreads = 1
        };
        _session = new InferenceSession(modelPath, options);
        _inputName = _session.InputMetadata.Keys.First();
        _outputName = _session.OutputMetadata.Keys.First();
    }

    public static async Task<PortraitBackgroundProcessor> CreateAsync(Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXUS-CAM", "Models");
        Directory.CreateDirectory(folder);
        string modelPath = Path.Combine(folder, "modnet-webcam.onnx");

        bool valid = File.Exists(modelPath) && await HasExpectedHashAsync(modelPath, cancellationToken);
        if (!valid)
        {
            progress?.Invoke("Загрузка AI-модели...");
            string tempPath = modelPath + ".download";
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(4) };
            using var response = await client.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = File.Create(tempPath))
                await source.CopyToAsync(target, cancellationToken);

            if (!await HasExpectedHashAsync(tempPath, cancellationToken))
            {
                File.Delete(tempPath);
                throw new InvalidDataException("Проверка целостности AI-модели не пройдена.");
            }
            File.Move(tempPath, modelPath, true);
        }

        progress?.Invoke("Подготовка AI-модели...");
        return new PortraitBackgroundProcessor(modelPath);
    }

    private static async Task<bool> HasExpectedHashAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, token);
        return Convert.ToHexString(hash).Equals(ModelSha256, StringComparison.OrdinalIgnoreCase);
    }

    public Mat ReplaceBackground(Mat frame, Mat background)
    {
        int width = frame.Width;
        int height = frame.Height;
        long now = Stopwatch.GetTimestamp();
        bool refreshMatte = _cachedAlpha is null ||
            Stopwatch.GetElapsedTime(_lastMatteTicks, now).TotalSeconds >= 1.0 / MatteRefreshRate ||
            _cachedWidth != width || _cachedHeight != height;

        if (refreshMatte)
        {
            using var resized = new Mat();
            int inputWidth = Math.Max(32, (int)Math.Round((double)width / height * ModelInputHeight / 32) * 32);
            Cv2.Resize(frame, resized, new Size(inputWidth, ModelInputHeight), 0, 0, InterpolationFlags.Area);
            using var rgb = new Mat();
            Cv2.CvtColor(resized, rgb, ColorConversionCodes.BGR2RGB);

            var tensor = new DenseTensor<float>(new[] { 1, 3, ModelInputHeight, inputWidth });
            for (int y = 0; y < ModelInputHeight; y++)
            for (int x = 0; x < inputWidth; x++)
            {
                var pixel = rgb.At<Vec3b>(y, x);
                tensor[0, 0, y, x] = pixel.Item0 / 127.5f - 1f;
                tensor[0, 1, y, x] = pixel.Item1 / 127.5f - 1f;
                tensor[0, 2, y, x] = pixel.Item2 / 127.5f - 1f;
            }

            var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };
            using var results = _session.Run(inputs);
            var output = results.First(x => x.Name == _outputName).AsTensor<float>();
            int matteHeight = output.Dimensions[^2];
            int matteWidth = output.Dimensions[^1];
            using var alphaSmall = new Mat(matteHeight, matteWidth, MatType.CV_8UC1);
            for (int y = 0; y < matteHeight; y++)
            for (int x = 0; x < matteWidth; x++)
                alphaSmall.Set(y, x, (byte)(Math.Clamp(output[0, 0, y, x], 0f, 1f) * 255f));

            _cachedAlpha?.Dispose();
            _cachedAlpha = new Mat();
            Cv2.Resize(alphaSmall, _cachedAlpha, new Size(width, height), 0, 0, InterpolationFlags.Linear);
            _lastMatteTicks = now;
            _cachedWidth = width;
            _cachedHeight = height;
        }

        if (_cachedBackground is null || _cachedBackground.Width != width || _cachedBackground.Height != height)
        {
            _cachedBackground?.Dispose();
            _cachedBackground = new Mat();
            Cv2.Resize(background, _cachedBackground, new Size(width, height), 0, 0, InterpolationFlags.Linear);
        }

        // OpenCV vectorized operations avoid the old per-pixel C# loops.
        using var alphaFloat = new Mat();
        _cachedAlpha!.ConvertTo(alphaFloat, MatType.CV_32FC1, 1.0 / 255.0);
        using var alpha3 = new Mat();
        Cv2.CvtColor(alphaFloat, alpha3, ColorConversionCodes.GRAY2BGR);
        using var inverseAlpha = new Mat();
        Cv2.Subtract(Scalar.All(1.0), alpha3, inverseAlpha);

        using var foregroundFloat = new Mat();
        frame.ConvertTo(foregroundFloat, MatType.CV_32FC3);
        using var backgroundFloat = new Mat();
        _cachedBackground!.ConvertTo(backgroundFloat, MatType.CV_32FC3);
        using var foregroundPart = new Mat();
        using var backgroundPart = new Mat();
        Cv2.Multiply(foregroundFloat, alpha3, foregroundPart);
        Cv2.Multiply(backgroundFloat, inverseAlpha, backgroundPart);
        using var combined = new Mat();
        Cv2.Add(foregroundPart, backgroundPart, combined);

        var result = new Mat();
        combined.ConvertTo(result, MatType.CV_8UC3);
        return result;
    }

    public void Dispose()
    {
        _cachedAlpha?.Dispose();
        _cachedBackground?.Dispose();
        _session.Dispose();
    }
}
