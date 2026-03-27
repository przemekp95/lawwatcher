namespace LawWatcher.Notifications.Contracts;

public sealed record AlertNotificationDispatchResponse(
    Guid AlertId,
    Guid SubscriptionId,
    string ProfileName,
    string BillTitle,
    string Channel,
    string Recipient,
    DateTimeOffset DispatchedAtUtc);
