using Microsoft.AspNetCore.SignalR;

/// <summary>
/// Канал рассылки «журнал исполнения обновился».
///
/// Раньше конструктор подписывался на статическое событие
/// ExecutedSignalService.ExecutedSignalsChanged через флаг _hooked. Первый же
/// созданный хаб оставался в подписчиках навсегда, хотя его соединение давно
/// закрыто, и рассылка шла через мёртвый экземпляр. Плюс Program.cs подписывался
/// на то же событие — получалась двойная отправка.
///
/// Подписка теперь одна и живёт в Program.cs через IHubContext.
/// </summary>
public class ExecutedSignalsHub : Hub
{
}
