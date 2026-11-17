// ============================================================================
// Out.cs: вывод одновременно в консоль и в текстовый отчёт
//
// Зачем: в Windows PowerShell 5.1 перенаправление `WiaProbe.exe ... > dump.txt`
// перекодирует UTF-8 в кодовую страницу консоли (обычно CP866), и в файле
// получаются кракозябры. Поэтому файл создаёт сам пробник: он всегда в UTF-8
// с BOM, и его можно открыть Блокнотом, Word.
// ============================================================================

using System.Text;

namespace WiaProbe;

internal static class Out
{
    private static StreamWriter? _writer;

    /// <summary>Путь отчётного файла, если он открыт</summary>
    public static string? FilePath { get; private set; }

    /// <summary>
    /// Открывает отчётный файл (перезаписывает существующий).
    /// Если записать по указанному пути нельзя (нет прав, диск только для
    /// чтения), отчёт уводится во временную папку: файл должен появиться
    /// в любом случае, иначе с ПК с МФУ нечего прислать.
    /// </summary>
    /// <returns>true, если отчёт удалось открыть</returns>
    public static bool Open(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            OpenCore(path);
            return true;
        }
        catch (Exception ex)
        {
            var fallback = Path.Combine(Path.GetTempPath(), "WiaProbe",
                $"wiaprobe-{DateTime.Now:yyyyMMdd-HHmmss}", "report.txt");
            try
            {
                OpenCore(fallback);
                Console.Error.WriteLine(
                    $"Не удалось создать отчёт по пути {Path.GetFullPath(path)}: {ex.Message}");
                Console.Error.WriteLine($"Отчёт сохранён во временную папку: {FilePath}");
                return true;
            }
            catch (Exception fallbackEx)
            {
                Console.Error.WriteLine(
                    $"Не удалось открыть отчёт: {ex.Message}; " +
                    $"запасной путь тоже не сработал: {fallbackEx.Message}");
                return false;
            }
        }
    }

    private static void OpenCore(string path)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // BOM нужен, чтобы Блокнот и Excel сразу поняли, что это UTF-8
        _writer = new StreamWriter(
            new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true))
        {
            AutoFlush = true
        };
        FilePath = full;
    }

    public static void Close()
    {
        _writer?.Dispose();
        _writer = null;
    }

    public static void Line(string text = "")
    {
        Console.WriteLine(text);
        _writer?.WriteLine(text);
    }

    public static void Write(string text)
    {
        Console.Write(text);
        _writer?.Write(text);
    }

    /// <summary>Заголовок раздела: пустая строка, линия, название, линия</summary>
    public static void Section(string title)
    {
        Line();
        Line(new string('=', 78));
        Line(title);
        Line(new string('=', 78));
    }
}
