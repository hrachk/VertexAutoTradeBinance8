namespace VertexAutoTradeBinance8.Models
{
    /// <summary>
    /// Настройки пути для снапшота движка.
    /// Используется EngineStateSnapshotService.
    /// </summary>
    public class EngineStateSettings
    {
        /// <summary>
        /// Полный путь к файлу снапшота (engine_state.json).
        /// Пример: "C:\\VertexShared\\engine_state.json"
        /// </summary>
        public string? SnapshotPath { get; set; }
    }
}
