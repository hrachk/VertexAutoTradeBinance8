# Vertex AutoTrade Binance — v6 (production)

Алгоритмический бот для Binance USDⓈ-M Futures (hedge mode) + Web-терминал (Blazor Server).

```
VertexAutoTradeBinance AI v5.0 Full Autonomous Mode/   ← бот (Worker): стратегия, риск, исполнение, сопровождение
VertexAutoTradeBinance8.Web/                            ← терминал: дашборд, позиции, журнал, настройки
VertexAutoTradeBinance8.API/                            ← REST API (опционально, защищён токеном)
deploy/                                                  ← publish / установка служб / шаблоны секретов
```

## ⚠️ Перед первым запуском

1. **Отзовите старые API-ключи Binance.** В истории git лежали две пары живых ключей
   (`VertexAutoTradeBinance AI v5.0…/appsettings.json` и `VertexAutoTradeBinance8.API/appsettings.json`).
   Удалить их из файлов недостаточно — они навсегда остались в истории коммитов.
2. Создайте новый ключ: **только Futures + Read**, **без Withdraw**, **IP whitelist**.
3. Ключи — только через переменные окружения или `appsettings.Local.json` (в `.gitignore`):

```json
{ "Binance": { "ApiKey": "...", "SecretKey": "...", "Environment": "Testnet" } }
```

`Environment`: `Live` | `Testnet` | `Demo`. **Сначала прогоните Testnet/Demo минимум несколько дней.**

## Быстрый старт (Windows)

```powershell
# 1. сборка
powershell -ExecutionPolicy Bypass -File deploy\publish.ps1
# 2. секреты
copy deploy\appsettings.Local.example.json publish\bot\appsettings.Local.json   # впишите НОВЫЕ ключи
copy deploy\appsettings.Local.example.json publish\web\appsettings.Local.json   # + Web.AccessKey
# 3. службы (PowerShell от администратора) — автоперезапуск при падении
powershell -ExecutionPolicy Bypass -File deploy\install-services.ps1
```

Терминал: `http://127.0.0.1:5253` (по умолчанию слушает только localhost; для удалённого доступа
задайте `Web__AccessKey` и ставьте за reverse-proxy с HTTPS).

Без служб: `publish\bot\VertexAutoTradeBinance8.exe` и `publish\web\VertexAutoTradeBinance8.Web.exe`.

## Общая папка данных

Бот и терминал работают с одной папкой `VERTEX_DATA_DIR` (по умолчанию `C:\VertexShared`, на Linux `~/VertexShared`):

| файл | назначение |
|---|---|
| `executed_signals.json` | журнал сделок (вход, выход, реальный PnL с комиссиями) |
| `managed_positions.json` | позиции под управлением бота (SL, TP, стадия сопровождения) |
| `trading_control.json` | глобальный ON/OFF и блок выходных — меняется из UI без рестарта |
| `engine_state.json` | heartbeat движка для UI |
| `missed_trades.json` | пропущенные сигналы (≤3000 записей) |
| `ai-models/` | состояние AI-обучения |
| `logs/bot-*.log`, `logs/web-*.log` | логи (14 дней) |

При первом старте v6 бот сам переносит эти файлы из старого `bin/` (если в новой папке их ещё нет).

## Ключевые настройки (`Trading`)

| параметр | default | смысл |
|---|---|---|
| `BaseRiskPercent` | 0.75 | риск на сделку в **процентах** баланса (1.0 = 1%) |
| `MaxRiskPercent` | 1.2 | потолок риска после AI-множителей |
| `MaxDailyLossPercent` | 4.0 | дневной стоп: при убытке ≥ X% новые входы запрещены до 00:00 UTC |
| `Leverage` | 10 | выставляется на бирже; влияет только на маржу |
| `MaxOpenPositions` | 5 | лимит одновременных позиций |
| `MinStopDistancePercent` / `MaxStopDistancePercent` | 0.30 / 6.0 | допустимая дистанция SL |
| `BreakEvenAfterTp1` | true | после TP1 SL → вход + `BreakEvenBufferPercent` |
| `LockProfitAfterTp2` | true | после TP2 SL → уровень TP1 |
| `TpSplit` | 0.4/0.3/0.3 | доли объёма по TP1/TP2/TP3 |
| `AllowOneMinuteEntries` | false | входы по 1m (шум + комиссии) |
| `EntryFillTimeoutSeconds` | 45 | ожидание исполнения лимитного входа |
| `PostCloseCooldownMinutes` / `SameSideCooldownMinutes` | 45 / 240 | отдых после закрытия (после убытка — ×2) |

Изменения риска через UI записываются в `appsettings.json` бота и применяются **после рестарта** службы бота.
ON/OFF и выходные — сразу.

## Жизненный цикл сделки (v6)

1. Сигнал только по **закрытым** свечам TF ≥ 5m (EMA21-pullback в тренде / liquidity-grab во флэте).
2. Фильтры: режим рынка, паттерны, ликвидность, AI-подтверждение, реалистичность RR, дистанция SL.
3. Объём = `equity × risk% ÷ |entry − SL|`, ограничение маржой и minNotional биржи.
4. Лимитный вход → при частичном исполнении остаток отменяется → **SL `closePosition`** (3 попытки,
   иначе аварийное закрытие) → лестница TP 40/30/30.
5. Сопровождение: TP1 → безубыток, TP2 → SL на TP1, runner до TP3.
6. Закрытие: реальный PnL из userTrades → журнал, cooldown, AI-обучение.

## API

`VertexAutoTradeBinance8.API` — торговые эндпоинты требуют заголовок `X-Vertex-Token` = `Api__Token`
(≥24 символов). Без токена они отключены. `GET /health` — без авторизации.

Полный отчёт об аудите и исправлениях: [AUDIT.md](AUDIT.md).
