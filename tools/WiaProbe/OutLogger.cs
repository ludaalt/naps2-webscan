// ============================================================================
// OutLogger.cs: журнал NAPS2 в общий вывод (консоль + отчёт)
//
// Зачем: стандартный консольный провайдер пишет только в консоль, а отчёт
// должен содержать те же строки. Особенно важна строка "Error setting property":
// по ней видно, что драйвер отверг свойство ещё до передачи изображения.
// ============================================================================

using Microsoft.Extensions.Logging;

namespace WiaProbe;

internal sealed class OutLoggerProvider : ILoggerProvider
{
    private readonly LogLevel _minimum;

    public OutLoggerProvider(LogLevel minimum) => _minimum = minimum;

    public ILogger CreateLogger(string categoryName) => new OutLogger(categoryName, _minimum);

    public void Dispose()
    {
    }

    private sealed class OutLogger : ILogger
    {
        private readonly string _category;
        private readonly LogLevel _minimum;

        public OutLogger(string category, LogLevel minimum)
        {
            _category = category;
            _minimum = minimum;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && logLevel >= _minimum;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            Out.Line($"{DateTime.Now:HH:mm:ss.fff} [{Level(logLevel)}] {_category}: {formatter(state, exception)}");
            if (exception != null)
            {
                foreach (var line in exception.ToString().Split('\n'))
                {
                    Out.Line($"    {line.TrimEnd('\r')}");
                }
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
    }
}
