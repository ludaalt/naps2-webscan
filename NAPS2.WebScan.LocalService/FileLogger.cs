using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace NAPS2.WebScan.LocalService;

/// <summary>
/// Файловое логирование службы
///
/// Пишем сразу в два места, по файлу на день в каждом:
/// <list type="bullet">
/// <item>рядом с исполняемым файлом (<c>{каталог службы}\logs</c>) - его сразу
/// видно там же, где <c>custom-scanners.json</c>;</item>
/// <item>в <c>%ProgramData%\NAPS2 WebScan Local Service\logs</c> - штатное для
/// Windows место, доступное даже если каталог службы защищён от записи</item>
/// </list>
///
/// История ограничена: файлы старше <see cref="DefaultKeepDays"/> дней удаляются
/// (срок можно задать в appsettings.json, ключ <c>Logging:File:KeepDays</c>).
/// Чистка выполняется и при старте службы, и при переходе на новый день, чтобы
/// журнал не разрастался и на долго работающей службе
///
/// Отдельный провайдер нужен ещё и потому, что логирование самого NAPS2
/// (EsclServer/ScanningContext) иначе никуда не попадает: увидеть причину
/// ошибки сканирования можно, только передав им логгер (см. Worker)
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    /// <summary>Сколько дней хранить журнал, если в настройках не задано иное</summary>
    public const int DefaultKeepDays = 30;

    private readonly LogFileWriter[] _writers;

    public FileLoggerProvider(int keepDays)
    {
        _writers = GetLogDirectories().Select(dir => new LogFileWriter(dir, keepDays)).ToArray();
    }

    /// <summary>Срок хранения журнала из настроек (по умолчанию <see cref="DefaultKeepDays"/>)</summary>
    public static int GetRetentionDays(IConfiguration configuration)
    {
        var days = configuration.GetValue("Logging:File:KeepDays", DefaultKeepDays);
        return days < 1 ? DefaultKeepDays : days;
    }

    /// <summary>Каталоги с файлами журнала (рядом с exe и в ProgramData)</summary>
    public static IReadOnlyList<string> GetLogDirectories() => new[]
    {
        Path.Combine(AppContext.BaseDirectory, "logs"),
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "NAPS2 WebScan Local Service",
            "logs")
    }
    // На всякий случай: если службу запустили из ProgramData, каталоги совпадут
    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _writers);

    public void Dispose()
    {
        foreach (var writer in _writers)
        {
            writer.Dispose();
        }
    }
}

/// <summary>Пишет строки одной категории во все файлы журнала</summary>
internal sealed class FileLogger : ILogger
{
    private readonly string _category;
    private readonly LogFileWriter[] _writers;

    public FileLogger(string category, LogFileWriter[] writers)
    {
        _category = category;
        _writers = writers;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        WriteAll($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Level(logLevel)}] {_category}: {message}");
        if (exception != null)
        {
            WriteAll(Indent(exception.ToString()));
        }
    }

    private void WriteAll(string text)
    {
        foreach (var writer in _writers)
        {
            writer.WriteLine(text);
        }
    }

    private static string Level(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO ",
        LogLevel.Warning => "WARN ",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRIT ",
        _ => "NONE "
    };

    private static string Indent(string text) => "    " + text.Replace("\n", "\n    ");
}

/// <summary>
/// Пишет строки в файл текущего дня, при смене дня открывает
/// новый файл и удаляет файлы старше срока хранения
/// 
/// Ошибки записи намеренно не пробрасываются: 
/// журнал не должен ронять службу (например, если каталог
/// службы защищён от записи - останется второй каталог)
/// </summary>
internal sealed class LogFileWriter : IDisposable
{
    private readonly string _directory;
    private readonly int _keepDays;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private DateOnly _day;

    public LogFileWriter(string directory, int keepDays)
    {
        _directory = directory;
        _keepDays = keepDays;
        lock (_gate)
        {
            Roll();
        }
    }

    public void WriteLine(string text)
    {
        lock (_gate)
        {
            try
            {
                // новый день - новый файл, попутно чистим старые
                if (_writer == null || DateOnly.FromDateTime(DateTime.Now) != _day)
                {
                    Roll();
                }

                _writer?.WriteLine(text);
            }
            catch
            {
                // журнал не должен мешать основной работе службы
            }
        }
    }

    /// <summary>Открывает файл текущего дня и удаляет файлы старше срока хранения</summary>
    private void Roll()
    {
        try
        {
            Directory.CreateDirectory(_directory);
            PruneOldFiles();

            _writer?.Dispose();
            var today = DateOnly.FromDateTime(DateTime.Now);
            var path = Path.Combine(_directory, $"service-{today:yyyy-MM-dd}.log");
            // FileShare.ReadWrite - чтобы файл можно было открыть на просмотр, пока служба пишет
            _writer = new StreamWriter(
                new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                new UTF8Encoding(false))
            {
                AutoFlush = true
            };
            _day = today;
        }
        catch
        {
            // каталог недоступен - писать некуда, работаем без журнала
            _writer = null;
        }
    }

    private void PruneOldFiles()
    {
        var cutoff = DateTime.Now.AddDays(-_keepDays);
        foreach (var file in Directory.EnumerateFiles(_directory, "service-*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // занятый или уже удалённый файл - пропускаем
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
