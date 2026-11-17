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

    /// <summary>Открывает отчётный файл (перезаписывает существующий)</summary>
    public static void Open(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

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
