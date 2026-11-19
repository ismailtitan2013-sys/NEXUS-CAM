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
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;

    private PortraitBackgroundProcessor(string modelPath)
    {
        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        options.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2);
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
            progress?.Invoke("Загрузка AI-модели (около 26 МБ)...");
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
        int originalWidth = frame.Width;
        int originalHeight = frame.Height;
        int inputHeight = 512;
        int inputWidth = Math.Max(32, (int)Math.Round((double)originalWidth / originalHeight * inputHeight / 32) * 32);

        using var resized = new Mat();
        Cv2.Resize(frame, resized, new Size(inputWidth, inputHeight), 0, 0, InterpolationFlags.Area);
        using var rgb = new Mat();
        Cv2.CvtColor(resized, rgb, ColorConversionCodes.BGR2RGB);

        var tensor = new DenseTensor<float>(new[] { 1, 3, inputHeight, inputWidth });
        for (int y = 0; y < inputHeight; y++)
        for (int x = 0; x < inputWidth; x++)
        {
            var pixel = rgb.At<Vec3b>(y, x);
            tensor[0, 0, y, x] = (pixel.Item0 / 255f - 0.5f) / 0.5f;
            tensor[0, 1, y, x] = (pixel.Item1 / 255f - 0.5f) / 0.5f;
            tensor[0, 2, y, x] = (pixel.Item2 / 255f - 0.5f) / 0.5f;
        }

        using var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };
        using var results = _session.Run(inputs);
        var output = results.First(x => x.Name == _outputName).AsTensor<float>();
        int matteHeight = output.Dimensions[^2];
        int matteWidth = output.Dimensions[^1];

        using var alphaSmall = new Mat(matteHeight, matteWidth, MatType.CV_8UC1);
        for (int y = 0; y < matteHeight; y++)
        for (int x = 0; x < matteWidth; x++)
        {
            float value = Math.Clamp(output[0, 0, y, x], 0f, 1f);
            alphaSmall.Set(y, x, (byte)(value * 255f));
        }

        using var alpha = new Mat();
        Cv2.Resize(alphaSmall, alpha, new Size(originalWidth, originalHeight), 0, 0, InterpolationFlags.Linear);
        using var bg = new Mat();
        Cv2.Resize(background, bg, new Size(originalWidth, originalHeight), 0, 0, InterpolationFlags.Linear);

        var composed = new Mat(originalHeight, originalWidth, MatType.CV_8UC3);
        for (int y = 0; y < originalHeight; y++)
        for (int x = 0; x < originalWidth; x++)
        {
            byte a = alpha.At<byte>(y, x);
            var fg = frame.At<Vec3b>(y, x);
            var back = bg.At<Vec3b>(y, x);
            float amount = a / 255f;
            composed.Set(y, x, new Vec3b(
                (byte)(fg.Item0 * amount + back.Item0 * (1f - amount)),
                (byte)(fg.Item1 * amount + back.Item1 * (1f - amount)),
                (byte)(fg.Item2 * amount + back.Item2 * (1f - amount))));
        }
        return composed;
    }

    public void Dispose() => _session.Dispose();
}
