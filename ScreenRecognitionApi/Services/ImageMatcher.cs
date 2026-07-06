using OpenCvSharp;
using OpenCvSharp.Features2D;
using OpenCvSharp.Flann;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ScreenRecognitionApi.Services; // Строго тот же namespace!

public class ImageMatcher
{
    private const string TemplatesDir = "templates";
    private const string NotFoundSignal = "NOT_FOUND";
    private const double MinMatchPercentage = 5.0;

    private class TemplateData
    {
        public string Id { get; set; }
        public KeyPoint[] KeyPoints { get; set; }
        public Mat Descriptors { get; set; }
        public int TotalKeyPoints { get; set; }
    }

    private readonly List<TemplateData> _indexedTemplates = new();
    private readonly SIFT _sift;
    private readonly FlannBasedMatcher _flann;

    public ImageMatcher()
    {
        _sift = SIFT.Create();

        
        _flann = new FlannBasedMatcher();

        InitializeTemplates();
    }

    private void InitializeTemplates()
    {
        if (!Directory.Exists(TemplatesDir))
        {
            Directory.CreateDirectory(TemplatesDir);
            return;
        }

        var extensions = new[] { "*.bmp", "*.jpg", "*.png", "*.jpeg" };
        var files = extensions.SelectMany(ext => Directory.GetFiles(TemplatesDir, ext, SearchOption.TopDirectoryOnly))
                              .Distinct();

        foreach (var file in files)
        {
            using var img = Cv2.ImRead(file, ImreadModes.Grayscale);
            if (img.Empty()) continue;

            var keyPoints = new KeyPoint[0];
            var descriptors = new Mat();

            _sift.DetectAndCompute(img, null, out keyPoints, descriptors);

            if (!descriptors.Empty() && keyPoints.Length > 0)
            {
                string fileName = Path.GetFileName(file);

                _indexedTemplates.Add(new TemplateData
                {
                    Id = ExtractTemplateId(fileName),
                    KeyPoints = keyPoints,
                    Descriptors = descriptors,
                    TotalKeyPoints = keyPoints.Length
                });
            }
        }
    }

    // Метод принимает массив байт из памяти
    public string FindBestTemplate(byte[] photoBytes)
    {
        if (photoBytes == null || photoBytes.Length == 0 || !_indexedTemplates.Any())
            return NotFoundSignal;

        using var imgPhoto = Cv2.ImDecode(photoBytes, ImreadModes.Grayscale);
        if (imgPhoto.Empty())
            return NotFoundSignal;
        
        var desPhoto = new Mat();

        _sift.DetectAndCompute(imgPhoto, null, out var kpPhoto, desPhoto);
        if (desPhoto.Empty() || desPhoto.Rows < 2)
            return NotFoundSignal;

        string bestTemplateId = NotFoundSignal;
        int maxValidMatches = 0;
        bool hasConflict = false;

        foreach (var template in _indexedTemplates)
        {
            var matches = _flann.KnnMatch(template.Descriptors, desPhoto, k: 2);
            var goodMatches = new List<DMatch>();

            foreach (var matchGroup in matches)
            {
                if (matchGroup.Length == 2)
                {
                    var m = matchGroup[0];
                    var n = matchGroup[1];
                    if (m.Distance < 0.7f * n.Distance)
                    {
                        goodMatches.Add(m);
                    }
                }
            }

            int validMatchesCount = 0;

            if (goodMatches.Count >= 4)
            {
                var ptsTemplate = goodMatches.Select(m => template.KeyPoints[m.QueryIdx].Pt).Select(p => new Point2f(p.X, p.Y)).ToArray();
                var ptsPhoto = goodMatches.Select(m => kpPhoto[m.TrainIdx].Pt).Select(p => new Point2f(p.X, p.Y)).ToArray();

                using var srcPoints = InputArray.Create(ptsTemplate);
                using var dstPoints = InputArray.Create(ptsPhoto);
                using var mask = new Mat();

                var homography = Cv2.FindHomography(srcPoints, dstPoints, HomographyMethods.Ransac, 5.0, mask);

                if (!mask.Empty())
                {
                    for (int i = 0; i < mask.Rows; i++)
                    {
                        if (mask.At<byte>(i, 0) > 0)
                        {
                            validMatchesCount++;
                        }
                    }
                }
            }
            else
            {
                validMatchesCount = goodMatches.Count;
            }

            // --- ДОБАВЛЕННЫЙ БЛОК ЛОГИРОВАНИЯ ---
            // Считаем процент совпадения от общего числа точек этого шаблона
            double matchPercentage = template.TotalKeyPoints > 0
                ? ((double)validMatchesCount / template.TotalKeyPoints) * 100
                : 0;

            Console.WriteLine($"Шаблон [{template.Id}]: Всего точек шаблона={template.TotalKeyPoints} | Валидных совпадений={validMatchesCount} ({matchPercentage:F2}%)");
            // ------------------------------------

            if (matchPercentage >= MinMatchPercentage)
            {
                if (validMatchesCount > maxValidMatches && validMatchesCount > 0)
                {
                    maxValidMatches = validMatchesCount;
                    bestTemplateId = template.Id;
                    hasConflict = false;
                }
                else if (validMatchesCount == maxValidMatches && validMatchesCount > 0)
                {
                    hasConflict = true;
                }
            }
            
        }

        Console.WriteLine($"ИТОГ МАТЧИНГА: Выбран шаблон '{bestTemplateId}' (макс. точек: {maxValidMatches})");
        Console.WriteLine("=====================================\n");

        return hasConflict ? NotFoundSignal : bestTemplateId;
    }

    private string ExtractTemplateId(string fileName)
    {
        string lowerName = fileName.ToLower();
        int templateIndex = lowerName.IndexOf("_template");

        if (templateIndex != -1)
        {
            return fileName.Substring(0, templateIndex);
        }

        return Path.GetFileNameWithoutExtension(fileName);
    }
}