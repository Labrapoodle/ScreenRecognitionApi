using Microsoft.AspNetCore.Mvc;
using ScreenRecognitionApi.Services;
using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Threading.Tasks;

namespace ScreenRecognitionApi.Controllers;

// 1. Создаем простую модель для входящего JSON (DTO)
public class OcrRequest
{
    [Required]
    public string ImageBase64 { get; set; }
    public string? FileKey { get; set; }
}

[ApiController]
// Обратите внимание: маршрут теперь будет строго "api/ocr" вместо "api/[controller]"
// Так как имя контроллера OcrController, [controller] превратится в ocr.
[Route("api/ocr")]
public class OcrController : ControllerBase
{
    private readonly OcrService _ocrService;

    public OcrController(OcrService ocrService)
    {
        _ocrService = ocrService;
    }

    /// <summary>
    /// Распознавание технического экрана через Base64 JSON
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Recognize([FromBody] OcrRequest request)
    {
        // Проверяем, что объект вообще пришел
        if (request == null)
        {
            return BadRequest(new { success = false, error = "Некорректный JSON запрос." });
        }

        // Проверяем строку Base64
        if (string.IsNullOrWhiteSpace(request.ImageBase64))
        {
            return BadRequest(new { success = false, error = "Файл изображения в формате Base64 не передан." });
        }

        

        try
        {
            // 2. Декодируем текстовую Base64 строку обратно в бинарный массив байт
            byte[] imageBytes = Convert.FromBase64String(request.ImageBase64);

            // 3. Передаем байты напрямую в ваш OcrService (как и было раньше!)
            string result = await _ocrService.Process(imageBytes, request.FileKey);

            // Возвращаем результат
            return Content(result, "application/json");
        }
        catch (FormatException)
        {
            return BadRequest(new { success = false, error = "Переданная строка не является валидным Base64." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { success = false, error = ex.Message });
        }
    }
}