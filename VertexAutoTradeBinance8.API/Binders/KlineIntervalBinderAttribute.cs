using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace VertexAutoTradeBinance8.API.Binders
{
    public class KlineIntervalBinderAttribute : ModelBinderAttribute
    {
        public KlineIntervalBinderAttribute() : base(typeof(KlineIntervalModelBinder))
        {
        }
    }
}
