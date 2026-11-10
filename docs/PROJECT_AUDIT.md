# Project Audit — Initial Baseline

Date: 2026-11-09

This is the first architecture and safety pass. It is not a certification for live trading and does not establish that any strategy is profitable.

## Runtime map

- `VertexAutoTradeBinance AI v5.0 Full Autonomous Mode/Program.cs` is the primary .NET 10 worker host. It registers market data, strategy, risk, journal, exchange, recovery, and supervision services.
- `TradingWorker.cs` waits for `BootGate`, maintains the symbol universe and market context, evaluates and ranks signals, applies entry gates and sizing, then dispatches approved entries to the configured exchange executor.
- `Strategy/StrategyRouter.cs` and `Strategy/StrategyEngine.cs` produce/rout signals. Several focused filters and setup engines live under `Strategy/`.
- `Services/OrderExecutor.cs` submits Binance entries. `Services/BinanceAlgoOrderService.cs` places conditional Binance Futures orders through the Algo Order REST API. `PositionSupervisorService.cs` maintains protective orders and position lifecycle.
- `src/VertexAutoTrade.*` contains separately factored core, risk, regime, execution, news/macro, and ML libraries. The main worker references these modules; their presence alone does not mean every input is supplied correctly at runtime.
- `VertexAutoTradeBinance8.Web/Program.cs` hosts the Blazor dashboard and HTTP/SignalR endpoints. It shares selected runtime settings and state files with the engine.
- `VertexAutoTradeBinance8.API/` is a separate .NET 8 API project that references the .NET 10 engine. It is not part of `VertexAutoTradeBinance8.sln` and does not currently build independently.

## Confirmed safety defects fixed in this pass

1. A failed institutional gate could previously be logged and skipped, after which the worker continued toward execution. Gate exceptions now reject the entry.
2. A rejected institutional gate could be softened to a 0.55 size multiplier for selected strategy labels. Rejections now remain rejections; shadow behavior must be expressed as an allowed decision with a size multiplier.
3. A `BLOCK` AI risk grade could be converted into a 0.50 risk multiplier for selected strategy labels, contrary to `AiRiskScalerV2`'s documented meaning. It now rejects the signal.
4. The institutional gate always received zero open positions and zero realized daily PnL. It now receives the supervisor's live position count and the daily drawdown guard's recorded PnL.
5. The institutional daily-R and anti-tilt state was not updated on trade close and was lost at restart. Trade journal close events now feed the pipeline, and today's entries are replayed after `BootGate` is ready.
6. `RiskEngine:Hard` and `RiskEngine:AntiTilt` settings were not bound in the worker host. They are now bound from the configuration providers.
7. The strategy mode `MeanReversionOnly` suppressed CORE but the router discarded every mean-reversion signal, making that mode incapable of trading. The router now forwards validated mean-reversion signals when selected (and in Auto), subject to the shared CHAOS, setup, and entry-sanity checks.
8. `StrategyRouter` and `TradingWorker` wrote the same candidate multiple times to `live_signals.json`; the router no longer writes, and the worker remains the single writer so rejected candidates still appear without duplicate rows.
9. Binance's executor performed leverage/account operations before checking `Trading:EnableExecution`. It now rejects before creating a client or touching the account, then retains the final pre-order check for a concurrent OFF toggle. It also validates side, quantity, entry, and stop geometry at the executor boundary.
10. A `DualModeTradingPolicy` rejection and evaluation exception could be reduced to a size haircut and continue toward entry. Those policy denials/errors now reject the entry.
11. Exceptions in the enabled entry-sanity, kill-switch, daily/BTC-volatility, news, and hard-enabled ML gates could fall through to execution. These now fail closed; ML shadow evaluation remains non-blocking when the skip gate is disabled.
12. Engine and Web pinned `Microsoft.Data.Sqlite 10.0.0`, which restored vulnerable SQLite native dependency `2.1.11`. Both now pin `10.0.12`, resolving `SQLitePCLRaw.lib.e_sqlite3 2.1.12` in the current restore graph.
13. Web's global AutoTrade toggle also disabled supervisor TP management. It now only controls new exchange entries; management of open Binance positions is independent. Global controls are hidden from unauthenticated layout sessions, Settings mutations check the authenticated circuit, and strategy mode selection now reflects available Engine routing modes.
14. Parallel Demo used one approved quantity for every opted-in client, calculated from the largest Demo balance. Approved entries now include the sizing equity and Binance quantity filters, and Web scales/rounds quantity per client and rejects under-minimum sizes.
15. `GetEquityForClient` previously returned the configured fallback balance whenever a client's account file was missing or unreadable. It now uses the fallback only for a genuinely new account and returns zero on read/parse errors, preventing a failed read from creating an oversized paper trade.
16. Web's strategy-mode file writer now validates enum values, normalizes the legacy `TrendOnly` alias, and serializes/atomically replaces writes.
17. The TWA controller previously accepted unauthenticated requests. Every TWA action now requires a valid active account session token. This is only authentication; global action ownership/role checks remain outstanding.
18. Removed eight redundant Microsoft.Extensions package references that were producing `NU1510` pruning warnings. The supported solution still restores and builds with the SDK/framework references.
19. The position supervisor now retains side-specific entry context across periodic calls that carry no signal, serializes overlapping per-symbol supervision, maps Binance one-way `BOTH` positions to their signed long/short side, and fails closed if the account position mode cannot be resolved. Positions without engine ownership markers remain hands-off during recovery.
20. Partial closes now use exchange-reported filled quantity and average fill price; the supervisor updates remaining position state only for actual fills. Protective-stop replacement is acknowledged before the old engine-owned stop is cancelled, and partial-close TP resizing follows place-before-cancel semantics while leaving user-owned orders alone.
21. Engine-owned protective orders carry stable client IDs. Startup recovery is awaited before the entry worker is released; marked engine positions are reconstructed and protected, while unmarked/user positions are not silently adopted. Recovery failure with configured credentials keeps the boot gate closed.
22. Fixed the Mean Reversion `TimeStopBars` clock: it now advances on distinct closed candles at the signal timeframe and accounts for missed intervals, rather than counting each fast supervisor sweep as a bar.
23. Cleared non-empty Binance key values from the tracked client configuration template. This does not remove any values from prior Git history; rotate those credentials if they were real.

## Launch posture observed

The checked-in primary engine configuration currently has `Binance:UseTestnet=false` and `Trading:EnableExecution=true`. This is a live-execution posture when valid credentials are available. The engine was not started during this audit.

These settings have separate jobs: `Binance:UseTestnet` chooses Binance's exchange endpoint; it is not the project's internal paper-trading switch. The Web account's `ParallelDemoEnabled` toggle controls virtual Demo trades from `approved_entries`. `Trading:EnableExecution` is the engine-wide gate for real exchange entries. A per-user LIVE/DEMO credential mode is also separate and chooses exchange credentials.

## Remaining audit work

These need review before treating the project as ready for live use:

- Exercise exchange position mode, order filters, quantity rounding, partial fills, idempotency, timeouts, retries, and reconciliation against Binance Futures behavior in the project's internal paper mode and then an explicitly controlled exchange demo account. The implementation now handles the main code paths, but compilation and unit tests do not establish exchange acceptance.
- Validate acknowledged protective-stop behavior under exchange rejection, process crash, partial close, hedge mode, and restart using a controlled non-live exchange environment; no exchange orders were sent in this pass.
- Confirm the Binance Algo Order service selects the correct environment and credentials for testnet/demo and live execution. Binance's current Futures docs require the Algo Order endpoints for conditional orders; legacy order routes can reject these with `-4120`.
- Trace balance/equity, daily loss, open-position count, and sizing for each configured exchange. Confirm limits remain account-wide when Binance and Bybit are both enabled.
- Audit daily drawdown recovery, account-state freshness, and behavior when exchange/user-data streams are stale or unavailable.
- Audit credential handling, auth/session boundaries, file permissions, runtime configuration writes, and API/controller exposure.
- Review strategy look-ahead, closed-candle use, sizing units, leverage/margin semantics, and performance validation. No win-rate or return claim is established by compilation.
- Reconcile successful Bybit orders with position supervision, account-wide limits, fills, and trade close journaling; the current worker only starts the Binance supervisor after Binance success.
- Finish the full Web UI visual/interaction audit and ensure every displayed strategy mode and Demo/LIVE control maps to an account-isolated runtime behavior.
- `DemoAccountService` is a mutable process-wide singleton whose active account changes through `BindClient`; simultaneous authenticated Blazor circuits can therefore contend for the same in-memory account. A per-client store/session refactor is needed before multi-user Web access is safe.
- The UI's per-account credential mode and global Engine live-entry switch are separate. Confirm the deployment model and replace process-wide credential activation with account-isolated execution before enabling multiple live clients.
- REST controllers other than `/api/twa` currently have no server-side authorization. TWA endpoints now require an active session token, but those actions still affect global state and need a role/owner check plus per-user scope before exposing the Web host to untrusted networks.
- Resolve the stale API target/reference relationship and decide whether the API should be supported in the solution or removed as obsolete.
- Review remaining compiler/analyzer warnings. A clean rebuild succeeds with 68 warnings (nullable diagnostics, uninitialized fields, analyzer warnings, and Razor diagnostics); these span numerous files and need focused remediation with coverage.
- Define a paper/demo rollout and operational runbook with kill-switch, alerting, daily limits, and restart/recovery checks before any live deployment.

## Validation in this pass

- `dotnet restore VertexAutoTradeBinance8.sln --ignore-failed-sources`: succeeded.
- `dotnet build VertexAutoTradeBinance8.sln --no-restore -t:Rebuild`: succeeded, 68 warnings, 0 errors after the position-supervisor fixes.
- `dotnet test VertexAutoTradeBinance8.sln --no-build --no-restore`: 27 passed, 0 failed.
- After the SQLite upgrade, the solution restore graph resolves `SQLitePCLRaw.lib.e_sqlite3 2.1.12`, and the `NU1903` advisory warning is absent. Redundant package-pruning references were removed; remaining warnings are primarily nullable, initialization, analyzer, and Razor diagnostics.
- Standalone API build was checked and fails because a .NET 8 project cannot reference the .NET 10 engine. The API target was left unchanged pending a proper API/runtime decision.
- `git diff --check` passed.
- No live exchange calls, orders, or application startup were performed.
- The latest supervisor time-stop change was included in the clean rebuild; the existing test suite remains 27/27 passing. There is no dedicated supervisor integration test harness in the solution.

## Runtime log investigation — 2026-11-10

The published instance logs under `C:\Vertex\Engines\client_001\logs` confirmed these incidents without reading or changing runtime credentials:

- `/market` terminated its Blazor circuit with `InvalidOperationException: Collection was modified` in `MarketSnapshot.razor` while ranking series by `Klines.Sum(Volume)`. `OnLiveKlineClosed` modified the same published `List<KlineDto>` in place. It now copy-on-writes the updated candle list before publishing the new `MarketSeries`.
- The log window contains six routed `CORE_INST_SHORT` candidates. Five were rejected at `INST_HardRisk` because the live account had 5 open positions while `RiskEngine:Hard:MaxOpenPositions` was 3. There were no `[ENTRY-PIPE] APPROVED` or `[ENTRY-SYNC]` records, and no `approved_entries.json`; consequently the Web paper service had no candidate to open. Live open-position and directional/correlation limits now block only the live order route; valid strategy candidates continue through shared quality gates and can be published to the independent paper ledger. Demo still applies its own per-client position/quantity limits.
- The published runtime had `Trading:EnableExecution=false`, which suppresses live orders, but the code's paper path should still operate. The client setting was `ParallelDemoEnabled=false` when inspected. Web logs recorded it toggled `True` and then `False` within the same session; the Dashboard toggle now disables itself while saving so rapid double clicks cannot immediately turn it back off. Re-enable Parallel Demo after deploying this fix if that account is intended to trade paper.
- Only six routed candidates appeared in the roughly eight-hour log sample, all CORE short signals. The logs prove the hard live-position gate prevented paper approvals for most of the observed candidates; they do not prove the strategy's signal rate is adequate across other market conditions. Lowering strategy thresholds blindly would change risk behavior and is not justified by this sample.

These runtime findings are from the published instance, which was not modified by this source change. Rebuild and deploy the updated Web and Engine binaries before expecting the fixes to affect that running service.
