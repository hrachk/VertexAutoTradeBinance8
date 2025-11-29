using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Binance.Net.Enums;
using VertexAutoTradeBinance8.Helpers;

namespace VertexAutoTradeBinance8.API.Binders
{
    public class KlineIntervalModelBinder : IModelBinder
    {
        public Task BindModelAsync(ModelBindingContext context)
        {
            var value = context.ValueProvider.GetValue(context.ModelName).FirstValue;

            if (string.IsNullOrWhiteSpace(value))
            {
                context.Result = ModelBindingResult.Success(null);
                return Task.CompletedTask;
            }

            // 1) Enum формат: OneHour, FourHour, etc
            if (Enum.TryParse(typeof(KlineInterval), value, true, out var parsed))
            {
                context.Result = ModelBindingResult.Success(parsed);
                return Task.CompletedTask;
            }

            // 2) Человеческий формат: 1h, 5m, 15m, 4h, 1d
            var converted = value.ToKlineInterval();
            context.Result = ModelBindingResult.Success(converted);

            return Task.CompletedTask;
        }
    }
}
