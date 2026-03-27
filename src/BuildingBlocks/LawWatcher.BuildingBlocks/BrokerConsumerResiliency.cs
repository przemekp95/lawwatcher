namespace LawWatcher.BuildingBlocks.Messaging;

public static class BrokerConsumerResiliency
{
    public static readonly TimeSpan[] DelayedRedeliveryIntervals =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1)
    ];

    public static readonly TimeSpan ImmediateRetryInterval = TimeSpan.FromSeconds(2);

    public const int ImmediateRetryCount = 3;
}
