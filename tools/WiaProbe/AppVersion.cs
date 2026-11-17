// ============================================================================
// AppVersion.cs: версия пробника в виде WiaProbe ГГГГММДД
//
// Зачем: отчёт присылают с разных ПК, и по первой строке должно быть сразу
// видно, какой сборкой пользовались. Дата — это дата сборки exe (BuildDate
// из WiaProbe.csproj), а не дата запуска: иначе по отчёту нельзя отличить
// старый exe от нового.
// ============================================================================

using System.Globalization;
using System.Reflection;

namespace WiaProbe;

internal static class AppVersion
{
    private const string Unknown = "unknown";

    /// <summary>Дата сборки в формате ГГГГММДД</summary>
    public static string Date { get; } = ResolveDate();

    /// <summary>Строка для консоли и отчёта: «WiaProbe 20261117»</summary>
    public static string Display => $"WiaProbe {Date}";

    private static string ResolveDate()
    {
        // Основной путь: дата, записанная при сборке в метаданные сборки
        try
        {
            var value = typeof(AppVersion).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "BuildDate")?.Value;
            if (IsDate(value))
            {
                return value!;
            }
        }
        catch
        {
            // Метаданных нет — переходим к запасному варианту
        }

        // Запасной путь: дата файла exe. Для single-file publish именно он
        // запущен, а Assembly.Location пуст
        try
        {
            var process = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(process) && File.Exists(process))
            {
                return File.GetLastWriteTime(process)
                    .ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            }
        }
        catch
        {
            // Дата файла недоступна — ниже вернём заглушку
        }

        return Unknown;
    }

    private static bool IsDate(string? value) =>
        value is { Length: 8 } && value.All(char.IsAsciiDigit);
}
