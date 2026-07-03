using OpenCvSharp;
using OpenCvSharp.Features2D;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScreenRecognitionApi.Services;

public class OcrService
{
    private readonly HttpClient _httpClient;

    // Единственный шаблон для теста
    private const string TemplatePath =
        @"C:\Users\k_alejnikov\Desktop\only_text\templates\3_1_template.bmp";

    public OcrService(IHttpClientFactory factory)
    {
        _httpClient = factory.CreateClient("ollama");
    }

    // Координаты нарезки
    private static readonly Dictionary<string, List<Rect>> CropCoordinates = new()
    {
        { "3_1", new List<Rect> { CreateRect(260, 50, 512, 543), CreateRect(780, 80, 1010, 445) } },
        { "3_2", new List<Rect> { CreateRect(260, 55, 492, 420), CreateRect(783, 227, 1015, 424) } },
        { "5_1", new List<Rect> { CreateRect(255, 48, 730, 267), CreateRect(265, 438, 768, 517) } },

        { "4_1", new List<Rect> {
            CreateRect(256, 51, 506, 562),
            CreateRect(519, 206, 762, 563),
            CreateRect(787, 107, 1014, 590)
        } },

        { "13_1", new List<Rect> {
            CreateRect(262, 84, 503, 562),
            CreateRect(524, 85, 764, 387),
            CreateRect(783, 56, 1013, 567)
        } }
    };

    private static readonly Regex CodeValueRegex = new(
        @"(?<![A-Za-z0-9])(?<code>[A-Z]\d{1,4})\s*=\s*(?<num>\d+(?:\.\d+)?)(?!\d)",
        RegexOptions.Compiled);

    private static Rect CreateRect(int x1, int y1, int x2, int y2)
    {
        return new Rect(x1, y1, x2 - x1, y2 - y1);
    }

    public async Task<string> Process(byte[] imageBytes, string fileKey)
    {
        if (!File.Exists(TemplatePath))
            throw new Exception($"Не найден шаблон: {TemplatePath}");

        byte[] warpedImageBytes = ExtractAndWarpArea(imageBytes, TemplatePath);

        if (warpedImageBytes == null)
            throw new Exception("Не удалось выполнить выравнивание изображения.");

        List<byte[]> blocksToSend = new();

        if (CropCoordinates.TryGetValue(fileKey, out var rects))
        {
            blocksToSend = CropImageIntoBlocks(warpedImageBytes, rects);
        }
        else
        {
            blocksToSend.Add(warpedImageBytes);
        }

        StringBuilder allResponses = new();

        foreach (var block in blocksToSend)
        {
            string b64String = Convert.ToBase64String(block);

            string prompt =
                "Это фрагмент технического экрана. Извлеки весь текст полностью, включая числа, параметры и единицы измерения. Не пропускай ничего.";

            var payloadObject = new
            {
                model = "qwen2.5vl:3b",
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = prompt,
                        images = new[]
                        {
                            b64String
                        }
                    }
                },
                stream = false,
                options = new
                {
                    num_predict = 2048,
                    num_ctx = 4096
                }
            };

            string jsonPayload = JsonSerializer.Serialize(payloadObject);

            using var content = new StringContent(
                jsonPayload,
                Encoding.UTF8,
                "application/json");

            using var response =
                await _httpClient.PostAsync("api/chat", content);

            response.EnsureSuccessStatusCode();

            string responseString =
                await response.Content.ReadAsStringAsync();

            using JsonDocument doc =
                JsonDocument.Parse(responseString);

            string rawContent =
                doc.RootElement
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            allResponses.AppendLine(rawContent);
        }

        return BuildResultJson(allResponses.ToString());
    }
    private static string BuildResultJson(string combinedText)
    {
        var result = new Dictionary<string, object>();

        foreach (Match match in CodeValueRegex.Matches(combinedText))
        {
            string code = match.Groups["code"].Value;
            string numStr = match.Groups["num"].Value;

            result[code] = NormalizeNumber(numStr);
        }

        return JsonSerializer.Serialize(result);
    }

    private static object NormalizeNumber(string numStr)
    {
        if (numStr.Contains('.'))
        {
            if (double.TryParse(
                numStr,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double d))
            {
                return d;
            }

            return numStr;
        }

        if (long.TryParse(numStr, out long l))
        {
            return l;
        }

        return numStr;
    }

    private static List<byte[]> CropImageIntoBlocks(
        byte[] originalImageBytes,
        List<Rect> zones)
    {
        var resultBlocks = new List<byte[]>();

        using var originalMat =
            Cv2.ImDecode(originalImageBytes, ImreadModes.Color);

        foreach (var zone in zones)
        {
            int safeX = Math.Max(0, Math.Min(zone.X, originalMat.Cols - 1));
            int safeY = Math.Max(0, Math.Min(zone.Y, originalMat.Rows - 1));

            int safeWidth =
                Math.Min(zone.Width, originalMat.Cols - safeX);

            int safeHeight =
                Math.Min(zone.Height, originalMat.Rows - safeY);

            var safeRect = new Rect(
                safeX,
                safeY,
                safeWidth,
                safeHeight);

            if (safeRect.Width <= 0 ||
                safeRect.Height <= 0)
            {
                continue;
            }

            using var cropped =
                new Mat(originalMat, safeRect).Clone();

            resultBlocks.Add(
                cropped.ImEncode(".png"));
        }

        return resultBlocks;
    }

    private static byte[] ExtractAndWarpArea(
    byte[] imageBytes,
    string templatePath)
    {
        using var imgPhoto =
            Cv2.ImDecode(imageBytes, ImreadModes.Color);

        using var imgScreen =
            Cv2.ImRead(templatePath);

        if (imgPhoto.Empty() || imgScreen.Empty())
        {
            throw new Exception("Не удалось загрузить изображение или шаблон.");
        }

        using var grayPhoto = new Mat();
        using var grayScreen = new Mat();

        Cv2.CvtColor(
            imgPhoto,
            grayPhoto,
            ColorConversionCodes.BGR2GRAY);

        Cv2.CvtColor(
            imgScreen,
            grayScreen,
            ColorConversionCodes.BGR2GRAY);

        using var sift = SIFT.Create();

        using var desPhoto = new Mat();
        using var desScreen = new Mat();

        sift.DetectAndCompute(
            grayPhoto,
            null,
            out var kpPhoto,
            desPhoto);

        sift.DetectAndCompute(
            grayScreen,
            null,
            out var kpScreen,
            desScreen);

        using var flann = new FlannBasedMatcher();

        var matches = flann.KnnMatch(
            desScreen,
            desPhoto,
            2);

        var goodMatches = new List<DMatch>();

        foreach (var match in matches)
        {
            if (match.Length >= 2 &&
                match[0].Distance < 0.7f * match[1].Distance)
            {
                goodMatches.Add(match[0]);
            }
        }

        if (goodMatches.Count < 4)
        {
            throw new Exception(
                "Недостаточно совпадений SIFT.");
        }

        var ptsScreen = new List<Point2f>();
        var ptsPhoto = new List<Point2f>();

        foreach (var match in goodMatches)
        {
            ptsScreen.Add(
                kpScreen[match.QueryIdx].Pt);

            ptsPhoto.Add(
                kpPhoto[match.TrainIdx].Pt);
        }

        using var ptsScreenMat =
            Mat.FromPixelData(
                ptsScreen.Count,
                1,
                MatType.CV_32FC2,
                ptsScreen.ToArray());

        using var ptsPhotoMat =
            Mat.FromPixelData(
                ptsPhoto.Count,
                1,
                MatType.CV_32FC2,
                ptsPhoto.ToArray());

        using var homography =
            Cv2.FindHomography(
                ptsScreenMat,
                ptsPhotoMat,
                HomographyMethods.Ransac,
                5.0);

        if (homography.Empty())
        {
            throw new Exception(
                "Не удалось вычислить гомографию.");
        }

        using var inverse =
            homography.Inv();

        using var warped =
            new Mat();

        Cv2.WarpPerspective(
            imgPhoto,
            warped,
            inverse,
            imgScreen.Size());

        return warped.ImEncode(".png");
    }
}