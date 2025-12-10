using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;

namespace VertexAutoTradeBinance8.Web.Services
{
    public class ExecutedSignalUiService
    {
        private readonly ExecutedSignalService _core;

        public ExecutedSignalUiService(ExecutedSignalService core)
        {
            _core = core;
        }

        public List<ExecutedSignalRecord> GetAll() => _core.GetAll();
    }
}
