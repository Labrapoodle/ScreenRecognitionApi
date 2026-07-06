using OpenCvSharp;
using OpenCvSharp.Features2D;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ScreenRecognitionApi.Services;

public class OcrService
{
    private readonly HttpClient _httpClient;
    private readonly ImageMatcher _imageMatcher; // Добавили поле матчера

    // Корневая папка с шаблонами
    private const string TemplatesDir = "templates";

    public OcrService(IHttpClientFactory factory, ImageMatcher imageMatcher)
    {
        _httpClient = factory.CreateClient("ollama");
        _imageMatcher = imageMatcher;
    }

    // Координаты нарезки (остаются как были)
    private static readonly Dictionary<string, List<Rect>> CropCoordinates = new()
    {
        { "1_2", new List<Rect> { CreateRect(261, 109, 419, 136)} },
        { "3_1", new List<Rect> { CreateRect(265, 59, 376, 88), CreateRect(262, 184, 418, 222), CreateRect(783, 110, 954, 164), CreateRect(784, 286, 953, 311), CreateRect(783, 407, 968, 442) } },
        { "3_2", new List<Rect> { CreateRect(265, 84, 497, 139), CreateRect(787, 385, 973, 418) } },
        { "4_1", new List<Rect> { CreateRect(259, 358, 431, 386), CreateRect(524, 358, 702, 386)} },
        { "4_2", new List<Rect> { CreateRect(261, 384, 451, 416), CreateRect(523, 336, 705, 365)} },
        { "4_3", new List<Rect> { CreateRect(295, 25, 511, 62), CreateRect(787, 25, 1003, 62)} },
        { "5_1", new List<Rect> { CreateRect(260, 184, 434, 237)} },
        { "6_theory", new List<Rect> { CreateRect(20, 177, 280, 537), CreateRect(424, 330, 829, 555), CreateRect(472, 210, 518, 238), CreateRect(518, 109, 609, 252), CreateRect(610, 208, 746, 241), CreateRect(609, 125, 969, 154), CreateRect(865, 560, 983, 574) } },
        { "6_real", new List<Rect> { CreateRect(20, 177, 280, 537), CreateRect(424, 330, 829, 555), CreateRect(472, 210, 518, 238), CreateRect(518, 109, 609, 252), CreateRect(610, 208, 746, 241), CreateRect(609, 125, 969, 154), CreateRect(865, 560, 983, 574) } },
        { "13_1", new List<Rect> { CreateRect(262, 508, 448, 535), CreateRect(783, 534, 968, 531)} }
    };

    // { "3_1", new List<Rect> { CreateRect(260, 50, 512, 543), CreateRect(780, 80, 1010, 445) } }
    // { "3_2", new List<Rect> { CreateRect(260, 55, 492, 420), CreateRect(783, 227, 1015, 424) } }
    // { "5_1", new List<Rect> { CreateRect(255, 48, 730, 267), CreateRect(265, 438, 768, 517) } }
    // { "4_1", new List<Rect> { CreateRect(256, 51, 506, 562), CreateRect(519, 206, 762, 563), CreateRect(787, 107, 1014, 590) } }
    // { "13_1", new List<Rect> { CreateRect(262, 84, 503, 562), CreateRect(524, 85, 764, 387), CreateRect(783, 56, 1013, 567) } }
    // 


    //private static readonly Regex CodeValueRegex = new(@"(?<![A-Za-z0-9])(?<code>[A-Z]\d{1,4})\s*=\s*(?<num>\d+(?:\.\d+)?)(?!\d)", RegexOptions.Compiled);

    private static readonly Regex CodeValueRegex = new(@"(?<![A-Za-z0-9А-Яа-я])(?<code>[A-ZА-Я]\d{1,4}(?:\.\d+)?)\s*=\s*(?<num>\d+(?:\.\d+)?)(?!\d)",    RegexOptions.Compiled);
    private static Rect CreateRect(int x1, int y1, int x2, int y2) => new Rect(x1, y1, x2 - x1, y2 - y1);

    
    
    // ВНИМАНИЕ: fileKey теперь необязательный параметр! 
    // Если клиент его не прислал, мы определим его автоматически
    public async Task<string> Process(byte[] imageBytes, string fileKey = null)
    {
        List<byte[]> blocksToSend = new();

        // 1. Автоопределение типа экрана, если ключ не передан
        if (string.IsNullOrWhiteSpace(fileKey) || fileKey == "NOT_FOUND")
        {
            fileKey = _imageMatcher.FindBestTemplate(imageBytes);
            Console.WriteLine($"[INFO] Автоопределение экрана. Найден ключ: {fileKey}");
        }

        // 2. Ищем динамический путь к файлу шаблона (проверяем разные расширения)
        string detectedTemplatePath = null;
        if (fileKey != "NOT_FOUND")
        {
            var extensions = new[] { ".bmp", ".jpg", ".png", ".jpeg" };
            foreach (var ext in extensions)
            {
                string testPath = Path.Combine(TemplatesDir, $"{fileKey}_template{ext}");
                if (File.Exists(testPath))
                {
                    detectedTemplatePath = testPath;
                    break;
                }
            }
        }

        // 3. Логика обработки: с выравниванием или без
        if (detectedTemplatePath != null)
        {
            Console.WriteLine($"[INFO] Применяем выравнивание по шаблону: {detectedTemplatePath}");
            byte[] warpedImageBytes = ExtractAndWarpArea(imageBytes, detectedTemplatePath);

            if (warpedImageBytes == null)
                throw new Exception("Не удалось выполнить выравнивание изображения.");

            if (CropCoordinates.TryGetValue(fileKey, out var rects))
            {
                blocksToSend = CropImageIntoBlocks(warpedImageBytes, rects);
            }
            else
            {
                blocksToSend.Add(warpedImageBytes);
            }
        }
        else
        {
            // Если шаблон физически отсутствует на диске или совпадений не найдено
            Console.WriteLine($"[WARNING] Шаблон для ключа '{fileKey}' не найден в папке '{TemplatesDir}'. Обработка картинки целиком.");
            blocksToSend.Add(imageBytes);
        }


        if (fileKey == "6_theory" || fileKey == "6_real")
        {
            return await ProcessSpecialScreen6(blocksToSend);
        }

        // 4. Отправка блоков в Ollama (без изменений)
        StringBuilder allResponses = new();
        foreach (var block in blocksToSend)
        {
            string rawContent = await SendToOllama(block, "Это фрагмент технического экрана. Извлеки весь текст полностью, включая числа, параметры и единицы измерения. Не пропускай ничего.");
            allResponses.AppendLine(rawContent);
        }

        Console.WriteLine("\n=== СЫРОЙ ОТВЕТ ОТ QWEN (ДО ОБРАБОТКИ REGEX) ===");
        Console.WriteLine(allResponses.ToString());
        Console.WriteLine("================================================\n");

        return BuildResultJson(allResponses.ToString());
    }


    private async Task<string> ProcessSpecialScreen6(List<byte[]> blocks)
    {
        var finalJsonData = new Dictionary<string, object>();

        // 1. Массив индивидуальных промптов для каждого из 7 блоков
        string[] customPrompts = new string[]
        {
            "На рисунке много чисел, верни то, которое встречается чаще всего. Ответь только числом.",
            "На рисунке много чисел, верни то, которое встречается чаще всего. Ответь только числом.",
            "Извлеки ровно 1 числовое значение. Ответь только числом.",
            "На рисунке много чисел, верни то, которое встречается чаще всего. Ответь только числом.",
            "Извлеки ровно 3 числовых значения параметров по порядку. Выведи только числа через пробел.",
            "Извлеки ровно 8 числовых значений параметров по порядку. Выведи только числа через пробел.",
            "Извлеки ровно 2 числовых значения. Выведи только числа через пробел."
        };

        Console.WriteLine("\n=== СТАРТ СПЕЦИАЛЬНОЙ ОБРАБОТКИ ЭКРАНА 6 ===");

        // Проходим циклом по всем назанным блокам
        for (int i = 0; i < blocks.Count && i < customPrompts.Length; i++)
        {
            // Отправляем блок с его персональным промптом
            string rawResponse = await SendToOllama(blocks[i], customPrompts[i]);

            Console.WriteLine($"[Блок {i + 1}] Ответ Qwen: {rawResponse.Trim()}");

            // Извлекаем из ответа Qwen все найденные числа (целые и с плавающей точкой)
            var extractedNumbers = Regex.Matches(rawResponse, @"\d+(?:\.\d+)?")
                                        .Cast<Match>()
                                        .Select(m => NormalizeNumber(m.Value))
                                        .ToList();

            // Вспомогательная локальная функция безопасного взятия числа по индексу
            object GetValue(int index) => index < extractedNumbers.Count ? extractedNumbers[index] : null;

            // 2. ХАРДКОД JSON ПОЛЕЙ ДЛЯ КАЖДОГО БЛОКА
            // Замените "ParamX" на ваши реальные бизнес-названия полей (например, "C17", "S20"...)
            switch (i)
            {
                case 0: // Блок 1 (Ждем 1 число)
                    finalJsonData["Температура игл"] = GetValue(0);
                    break;

                case 1: // Блок 2 (Ждем 1 число)
                    finalJsonData["Температура горячих каналов"] = GetValue(0);
                    break;

                case 2: // Блок 3 (Ждем 1 число)
                    finalJsonData["Температура сопла"] = GetValue(0);
                    break;

                case 3: // Блок 4 (Ждем 1 число)
                    finalJsonData["Температура обвода"] = GetValue(0);
                    break;

                case 4: // Блок 5 (Ждем 3 числа)
                    finalJsonData["Темп. инж. цилиндра 57"] = GetValue(0);
                    finalJsonData["Темп. инж. цилиндра 58"] = GetValue(1);
                    finalJsonData["Темп. инж. цилиндра 59"] = GetValue(2);
                    break;

                case 5: // Блок 6 (Ждем 8 чисел)
                    finalJsonData["Темп 71 зоны шнека"] = GetValue(0);
                    finalJsonData["Темп 72 зоны шнека"] = GetValue(1);
                    finalJsonData["Темп 73 зоны шнека"] = GetValue(2);
                    finalJsonData["Темп 74 зоны шнека"] = GetValue(3);
                    finalJsonData["Темп 75 зоны шнека"] = GetValue(4);
                    finalJsonData["Темп 76 зоны шнека"] = GetValue(5);
                    finalJsonData["Темп 77 зоны шнека"] = GetValue(6);
                    finalJsonData["Температура гранулята"] = GetValue(7);
                    break;

                case 6: // Блок 7 (Ждем 2 числа)
                    finalJsonData["Температура охл. воды выход"] = GetValue(0);
                    finalJsonData["Температура охл. воды вход"] = GetValue(1);
                    break;
            }
        }

        Console.WriteLine("============================================\n");
        return JsonSerializer.Serialize(finalJsonData);
    }

    /// <summary>
    /// Вспомогательный метод отправки одного блока в Ollama
    /// </summary>
    private async Task<string> SendToOllama(byte[] block, string prompt)
    {
        string b64String = Convert.ToBase64String(block);
        var payloadObject = new
        {
            model = "qwen2.5vl:3b",
            messages = new[] { new { role = "user", content = prompt, images = new[] { b64String } } },
            stream = false,
            options = new { num_predict = 64, num_ctx = 256 }
        };

        string jsonPayload = JsonSerializer.Serialize(payloadObject);
        using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync("api/chat", content);

        response.EnsureSuccessStatusCode();
        string responseString = await response.Content.ReadAsStringAsync();

        using JsonDocument doc = JsonDocument.Parse(responseString);
        return doc.RootElement.GetProperty("message").GetProperty("content").GetString();
    }


    private static string BuildResultJson(string combinedText)
    {
        var result = new Dictionary<string, object>();

        foreach (Match match in CodeValueRegex.Matches(combinedText))
        {
            string code = match.Groups["code"].Value;

            // Превращаем похожие русские буквы в английские для стандартизации ключей
            code = code.Replace('С', 'C')   // Русская С -> Английская C
                       .Replace('В', 'B')   // Русская В -> Английская B
                       .Replace('А', 'A')   // Русская А -> Английская A
                       .Replace('Т', 'T')   // Русская Т -> Английская T
                       .Replace('М', 'M')   // Русская М -> Английская M
                       .Replace('Р', 'P');  // Русская Р -> Английская P

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

        Cv2.ImWrite("warped_output.bmp", warped);

        return warped.ImEncode(".png");
    }
}