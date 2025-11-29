using System;
using System.Collections.Generic;
using Binance.Net.Enums;

namespace VertexAutoTradeBinance8.Services.Formatting
{
    public static class ConsoleSymbolTableFormatter
    {
        private static readonly Dictionary<string, Dictionary<KlineInterval, (string Status, string Note)>> _state
            = new();

        private const string Cyan = "\u001b[36m";
        private const string Magenta = "\u001b[35m";
        private const string Reset = "\u001b[0m";

        public static void StartSymbol(string symbol)
        {
            _state[symbol] = new();

            Console.WriteLine($"\n{Cyan}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━{Reset}");
            Console.WriteLine($"{Magenta}📌 {symbol} | 📊 Таймфреймы анализируются...{Reset}");
            Console.WriteLine($"{Cyan}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━{Reset}");
            Console.WriteLine(" TF   | Статус      | Комментарий");
            Console.WriteLine("---------------------------------------------");
        }

        public static void UpdateTf(string symbol, KlineInterval tf, string status, string msg)
        {
            if (!_state.ContainsKey(symbol))
                _state[symbol] = new();

            _state[symbol][tf] = (status, msg);

            Console.WriteLine($" {Short(tf),-4} | {status,-10} | {msg}");
        }

        private static string Short(KlineInterval tf) =>
            tf switch
            {
                KlineInterval.OneMinute => "1m",
                KlineInterval.FiveMinutes => "5m",
                KlineInterval.FifteenMinutes => "15m",
                KlineInterval.OneHour => "1h",
                KlineInterval.FourHour => "4h",
                KlineInterval.OneDay => "1d",
                _ => tf.ToString()
            };
    }
}
