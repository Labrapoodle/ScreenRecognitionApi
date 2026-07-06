using Microsoft.AspNetCore.Cors.Infrastructure;
using ScreenRecognitionApi.Services;

var builder = WebApplication.CreateBuilder(args);

// Контроллеры
builder.Services.AddControllers();

// Swagger
builder.Services.AddEndpointsApiExplorer();


// HttpClient для обращения к Ollama
builder.Services.AddHttpClient("ollama", client =>
{
    client.BaseAddress = new Uri("http://localhost:11434/");
    client.Timeout = TimeSpan.FromMinutes(30);
});

// Основной сервис распознавания
builder.Services.AddSingleton<ImageMatcher>();
builder.Services.AddSingleton<OcrService>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()   // Разрешить запросы с любых IP-адресов
              .AllowAnyMethod()   // Разрешить любые методы (POST, GET, OPTIONS и т.д.)
              .AllowAnyHeader();  // Разрешить любые заголовки в запросе
    });
});

var app = builder.Build();

// Swagger только для разработки

//app.UseHttpsRedirection();

app.UseCors("AllowAll");

app.UseAuthorization();

app.MapControllers();

app.Run();