namespace LawWatcher.BuildingBlocks.Configuration;

public sealed class HostHealthOptions
{
    public string? Urls { get; init; }

    public string LivePath { get; init; } = "/health/live";

    public string ReadyPath { get; init; } = "/health/ready";
}
