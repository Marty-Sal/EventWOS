using EventWOS.Application.Notifications.Contracts;

namespace EventWOS.Application.Notifications.Abstractions;

/// <summary>
/// What business handlers call to notify people. The entire external surface of
/// the notification platform, as far as business code is concerned.
///
/// These methods are deliberately synchronous and do NOT save. They stage
/// outbox rows on the same DbContext the handler is already using, so the
/// handler's own SaveChangesAsync commits the business change and the
/// notification work in ONE transaction. That is the whole design:
///
///   - provider down     -> the assignment still commits, message still queued
///   - transaction fails -> nothing was sent, because nothing was sent yet
///   - worker down       -> rows wait in Postgres until it returns
///
/// Handlers must therefore call these BEFORE their SaveChangesAsync, and must
/// not call SaveChangesAsync purely to flush a notification.
/// </summary>
public interface INotificationDispatcher
{
    /// <summary>Notifies one person.</summary>
    void Enqueue(NotificationRequest request);

    /// <summary>
    /// Notifies a known list of people about the same thing. Chunked internally,
    /// so a caller with hundreds of recipients does not need to think about it.
    /// </summary>
    void Enqueue(IEnumerable<NotificationRequest> requests);

    /// <summary>
    /// Notifies an audience the platform resolves later (all crew on an event,
    /// all vendors, all administrators). Use this instead of loading recipients
    /// yourself when the count could be large: it writes a single row.
    /// </summary>
    void EnqueueFanOut(NotificationFanOutRequest request);
}
