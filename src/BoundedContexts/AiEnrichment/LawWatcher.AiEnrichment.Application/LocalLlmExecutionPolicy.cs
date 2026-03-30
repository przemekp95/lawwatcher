using LawWatcher.BuildingBlocks.Configuration;

namespace LawWatcher.AiEnrichment.Application;

public sealed record LocalLlmExecutionPolicy(
    RuntimeProfile RuntimeProfile,
    AiActivationMode ActivationMode,
    int MaxConcurrency,
    TimeSpan UnloadAfterIdle)
{
    public static LocalLlmExecutionPolicy For(RuntimeProfile runtimeProfile) =>
        runtimeProfile == RuntimeProfile.Dev
            ? new LocalLlmExecutionPolicy(
                runtimeProfile,
                AiActivationMode.OnDemand,
                MaxConcurrency: 1,
                UnloadAfterIdle: TimeSpan.FromMinutes(2))
            : new LocalLlmExecutionPolicy(
                runtimeProfile,
                AiActivationMode.KeepWarm,
                MaxConcurrency: 2,
                UnloadAfterIdle: TimeSpan.FromMinutes(10));
}
