namespace VertexAutoTradeBinance8.Configuration;

/// <summary>
/// Единый корень для JSON-файлов, которые читают и бот, и Web UI.
/// По умолчанию: C:\VertexShared
/// Можно переопределить переменной окружения VERTEX_SHARED_DATA.
/// </summary>
public static class SharedDataPaths
{
    /// <summary>
    /// C:\VertexShared (или VERTEX_SHARED_DATA)
    /// </summary>
    public static string Root
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("VERTEX_SHARED_DATA");
            if (!string.IsNullOrWhiteSpace(env))
                return env.TrimEnd('\\', '/');

            return @"C:\VertexShared";
        }
    }

    public static string EngineStateJson => Path.Combine(Root, "engine_state.json");
    public static string MissedTradesJson => Path.Combine(Root, "missed_trades.json");
    public static string ExecutedSignalsJson => Path.Combine(Root, "executed_signals.json");
    public static string AiModelsFolder => Path.Combine(Root, "ai-models");
    public static string AiLearningJson => Path.Combine(AiModelsFolder, "ai_learning.json");
    public static string AiLearningBackupJson => Path.Combine(AiModelsFolder, "ai_learning_backup.json");

    /// <summary>
    /// Создаёт Root и ai-models, если их ещё нет.
    /// </summary>
    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(AiModelsFolder);
    }
}
