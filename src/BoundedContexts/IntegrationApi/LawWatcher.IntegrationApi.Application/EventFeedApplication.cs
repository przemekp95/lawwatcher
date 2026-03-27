using System.Security.Cryptography;
using System.Text;
using LawWatcher.IntegrationApi.Contracts;

namespace LawWatcher.IntegrationApi.Application;

public sealed record EventFeedItem(
    string Id,
    string Type,
    string SubjectType,
    string SubjectId,
    string Title,
    string Summary,
    DateTimeOffset OccurredAtUtc);

public interface IEventFeedSource
{
    Task<IReadOnlyCollection<EventFeedItem>> GetEventsAsync(CancellationToken cancellationToken);
}

public interface IEventFeedReadRepository
{
    Task<IReadOnlyCollection<EventFeedItem>> GetEventsAsync(CancellationToken cancellationToken);
}

public interface IEventFeedProjection
{
    Task ReplaceAllAsync(IReadOnlyCollection<EventFeedItem> events, CancellationToken cancellationToken);
}

public sealed record EventFeedProjectionRefreshResult(int EventCount, bool HasRebuilt);

public sealed class EventFeedProjectionRefreshService(
    IEnumerable<IEventFeedSource> sources,
    IEventFeedProjection projection)
{
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private string? _lastFingerprint;

    public async Task<EventFeedProjectionRefreshResult> RefreshAsync(CancellationToken cancellationToken)
    {
        var events = new List<EventFeedItem>();

        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var items = await source.GetEventsAsync(cancellationToken);
            events.AddRange(items);
        }

        var orderedEvents = OrderEvents(events).ToArray();
        var currentFingerprint = ComputeFingerprint(orderedEvents);
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            if (string.Equals(_lastFingerprint, currentFingerprint, StringComparison.Ordinal))
            {
                return new EventFeedProjectionRefreshResult(orderedEvents.Length, HasRebuilt: false);
            }

            await projection.ReplaceAllAsync(orderedEvents, cancellationToken);
            _lastFingerprint = currentFingerprint;
            return new EventFeedProjectionRefreshResult(orderedEvents.Length, HasRebuilt: true);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private static IEnumerable<EventFeedItem> OrderEvents(IEnumerable<EventFeedItem> events) =>
        events
            .OrderByDescending(item => item.OccurredAtUtc)
            .ThenBy(item => item.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase);

    private static string ComputeFingerprint(IReadOnlyCollection<EventFeedItem> events)
    {
        var builder = new StringBuilder();
        foreach (var item in events)
        {
            builder
                .Append(item.Id).Append('|')
                .Append(item.Type).Append('|')
                .Append(item.SubjectType).Append('|')
                .Append(item.SubjectId).Append('|')
                .Append(item.Title).Append('|')
                .Append(item.Summary).Append('|')
                .Append(item.OccurredAtUtc.ToString("O"))
                .AppendLine();
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}

public sealed class EventFeedQueryService(IEventFeedReadRepository repository)
{
    public async Task<IReadOnlyList<EventFeedResponse>> GetEventsAsync(CancellationToken cancellationToken)
    {
        var events = await repository.GetEventsAsync(cancellationToken);

        return OrderEvents(events)
            .Select(item => new EventFeedResponse(
                item.Id,
                item.Type,
                item.SubjectType,
                item.SubjectId,
                item.Title,
                item.Summary,
                item.OccurredAtUtc))
            .ToArray();
    }

    private static IEnumerable<EventFeedItem> OrderEvents(IEnumerable<EventFeedItem> events) =>
        events
            .OrderByDescending(item => item.OccurredAtUtc)
            .ThenBy(item => item.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase);
}
