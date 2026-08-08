using Microsoft.Extensions.Configuration;

namespace VertexAutoTradeBinance8.Services.Storage;

/// <summary>
/// Единая точка правды для путей к данным.
///
/// ПРОБЛЕМА, КОТОРУЮ ЭТО РЕШАЕТ
/// Движок и Web — два разных процесса, и оба считали путь как
/// Path.Combine(AppContext.BaseDirectory, "...json"). Но BaseDirectory у них
/// разный:
///     движок → ...\VertexAutoTradeBinance AI v5.0 Full Autonomous Mode\bin\Debug\net8.0\
///     web    → ...\VertexAutoTradeBinance8.Web\bin\Debug\net8.0\
/// Движок писал в свою папку, Web читал из своей и всегда находил пустоту.
/// Отсюда пустые страницы во всех разделах, кроме тех, что ходят прямо в Binance.
/// В коде остались закомментированные абсолютные пути вида
/// C:\Users\karap\... и F:\VERTEX TRADING SYSTEM\... — это были ручные обходы
/// ровно этой проблемы, привязанные к конкретной машине.
///
/// РЕШЕНИЕ
/// Оба процесса резолвят корень данных одинаково, в таком порядке:
///   1. переменная окружения VERTEX_DATA_ROOT
///   2. ключ конфигурации Storage:DataRoot
///   3. %LOCALAPPDATA%\VertexAutoTrade\data  (Windows)
///      ~/.local/share/VertexAutoTrade/data  (Linux/macOS)
/// Третий вариант не зависит от того, откуда запущен процесс, поэтому движок
/// и Web встречаются в одной папке без единой строчки настройки.
/// </summary>
public sealed class VertexPaths
{
    public const string EnvVar = "VERTEX_DATA_ROOT";
    public const string ConfigKey = "Storage:DataRoot";

    public string Root { get; }

    /// <summary>Как именно был выбран корень — показывается в UI на странице диагностики.</summary>
    public string RootSource { get; }

    public VertexPaths(IConfiguration? config = null)
    {
        var env = Environment.GetEnvironmentVariable(EnvVar);
        var cfg = config?[ConfigKey];

        if (!string.IsNullOrWhiteSpace(env))
        {
            Root = Path.GetFullPath(env);
            RootSource = $"переменная окружения {EnvVar}";
        }
        else if (!string.IsNullOrWhiteSpace(cfg))
        {
            Root = Path.GetFullPath(cfg);
            RootSource = $"конфигурация {ConfigKey}";
        }
        else
        {
            Root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VertexAutoTrade", "data");
            RootSource = "путь по умолчанию";
        }

        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Path.Combine(Root, "ai-models"));
    }

    public string EngineState => Path.Combine(Root, "engine_state.json");
    public string ExecutedSignals => Path.Combine(Root, "executed_signals.json");
    public string MissedTrades => Path.Combine(Root, "missed_trades.json");
    public string Decisions => Path.Combine(Root, "decisions.json");
    public string AiLearning => Path.Combine(Root, "ai-models", "ai_learning.json");
    public string AiLearningBackup => Path.Combine(Root, "ai-models", "ai_learning_backup.json");
    public string AiModelsFolder => Path.Combine(Root, "ai-models");

    /// <summary>
    /// Атомарная запись: пишем во временный файл и подменяем.
    /// Раньше все журналы писались через File.WriteAllText поверх живого файла —
    /// если процесс падал или Web читал в этот момент, на диске оставался
    /// обрезанный JSON, и файл переставал разбираться уже навсегда.
    /// </summary>
    public static void WriteAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);

        if (File.Exists(path))
            File.Replace(tmp, path, null);
        else
            File.Move(tmp, path);
    }

    /// <summary>Чтение, устойчивое к тому, что файл прямо сейчас переписывают.</summary>
    public static string? ReadOrNull(string path)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return null;

                using var fs = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                return sr.ReadToEnd();
            }
            catch (IOException)
            {
                Thread.Sleep(40);
            }
        }

        return null;
    }
}
