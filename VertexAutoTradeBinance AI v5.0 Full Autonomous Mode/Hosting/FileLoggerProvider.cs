using System.Collections.Concurrent;
using System.Text;
using VertexAutoTradeBinance8.Configuration;

namespace VertexAutoTradeBinance8.Hosting;

/// <summary>
/// Минимальный файловый логгер без внешних зависимостей: {DataDir}/logs/{prefix}-yyyyMMdd.log.
/// Пишет в фоне, хранит последние 14 дней.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _prefix;
    private readonly LogLevel _min;
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), 20000);
    private readonly Thread _writer;

    public FileLoggerProvider(string prefix = "bot", LogLevel min = LogLevel.Information)
    {
        _prefix = prefix;
        _min = min;
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "vertex-file-log" };
        _writer.Start();
        CleanupOld();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    private void Enqueue(string line)
    {
        if (!_queue.IsAddingCompleted) _queue.TryAdd(line);
    }

    private void WriteLoop()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                var path = Path.Combine(VertexPaths.LogsDir, $"{_prefix}-{DateTime.UtcNow:yyyyMMdd}.log");
                File.AppendAllText(path, line, Encoding.UTF8);
            }
            catch { /* лог не должен ронять процесс */ }
        }
    }

    private void CleanupOld()
    {
        try
        {
            foreach (var f in Directory.GetFiles(VertexPaths.LogsDir, $"{_prefix}-*.log"))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-14)) File.Delete(f);
        }
        catch { }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _p;
        private readonly string _cat;
        public FileLogger(FileLoggerProvider p, string cat) { _p = p; _cat = cat.Split('.').Last(); }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= _p._min;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var lvl = logLevel switch
            {
                LogLevel.Trace => "TRC", LogLevel.Debug => "DBG", LogLevel.Information => "INF",
                LogLevel.Warning => "WRN", LogLevel.Error => "ERR", LogLevel.Critical => "CRT", _ => "   "
            };
            var sb = new StringBuilder();
            sb.Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("Z ").Append(lvl).Append(' ')
              .Append('[').Append(_cat).Append("] ").Append(formatter(state, exception)).AppendLine();
            if (exception != null) sb.AppendLine(exception.ToString());
            _p.Enqueue(sb.ToString());
        }
    }
}
