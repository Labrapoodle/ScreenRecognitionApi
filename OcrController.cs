using Microsoft.AspNetCore.Mvc;
using ScreenRecognitionService.Services;

namespace ScreenRecognitionApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OcrController : ControllerBase
{
    private readonly OcrService _ocrService;

    public OcrController(OcrService ocrService)
    {
        _ocrService = ocrService;
    }

    /// <summary>
    /// Распознавание технического экрана
    /// </summary>
    /// <param name="image">Изображение</param>
    /// <param name="fileKey">Ключ шаблона (например 3_1)</param>
    /// <returns>JSON с найденными параметрами</returns>
    [HttpPost]
    public async Task<IActionResult> Recognize(
        IFormFile image,
        [FromForm] string fileKey)
    {
        if (image == null || image.Length == 0)
        {
            return BadRequest(new
            {
                success = false,
                error = "Файл изображения не передан."
            });
        }

        if (string.IsNullOrWhiteSpace(fileKey))
        {
            return BadRequest(new
            {
                success = false,
                error = "Не указан fileKey."
            });
        }

        try
        {
            using var ms = new MemoryStream();
            await image.CopyToAsync(ms);

            byte[] imageBytes = ms.ToArray();

            string result = await _ocrService.Process(imageBytes, fileKey);

            return Content(result, "application/json");
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                success = false,
                error = ex.Message
            });
        }
    }
}