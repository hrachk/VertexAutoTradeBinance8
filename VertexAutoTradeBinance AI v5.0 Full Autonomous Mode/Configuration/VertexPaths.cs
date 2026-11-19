namespace VertexAutoTradeBinance8.Configuration;

/// <summary>
/// Единая папка данных, общая для бота (Worker) и Web UI.
/// Раньше каждый процесс писал JSON в свой bin/ (AppContext.BaseDirectory) →
/// Web не видел сделки бота. Теперь оба процесса читают/пишут одну папку.
///
/// Порядок выбора:
///   1) переменная окружения VERTEX_DATA_DIR (или legacy VERTEX_SHARED)
///   2) Windows: C:\VertexShared
///   3) Linux/macOS: ~/VertexShared
/// </summary>
public static class VertexPaths
{
    private static readonly Lazy<string> _dataDir = new(Resolve);

    public static string DataDir => _dataDir.Value;

    public static string File(string name) => Path.Combine(DataDir, name);

    public static string SubDir(string name)
    {
        var dir = Path.Combine(DataDir, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string ExecutedSignals => File("executed_signals.json");
    public static string ManagedPositions => File("managed_positions.json");
    public static string MissedTrades => File("missed_trades.json");
    public static string TradingControl => File("trading_control.json");
    public static string EngineState => File("engine_state.json");
    public static string AiModelsDir => SubDir("ai-models");
    public static string LogsDir => SubDir("logs");

    private static string Resolve()
    {
        var env = Environment.GetEnvironmentVariable("VERTEX_DATA_DIR")
                  ?? Environment.GetEnvironmentVariable("VERTEX_SHARED"); // legacy name
        string dir;
        if (!string.IsNullOrWhiteSpace(env))
            dir = env.Trim();
        else if (OperatingSystem.IsWindows())
            dir = @"C:\VertexShared";
        else
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "VertexShared");

        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Атомарная запись файла (tmp + replace) — Web никогда не прочитает половину JSON.</summary>
    public static void WriteAllTextAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + "." + Environment.ProcessId + ".tmp";
        System.IO.File.WriteAllText(tmp, content);
        System.IO.File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Чтение файла, который может писать другой процесс (FileShare.ReadWrite + retry).</summary>
    public static string? ReadAllTextShared(string path)
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                if (!System.IO.File.Exists(path)) return null;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                return sr.ReadToEnd();
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }
        return null;
    }
}
