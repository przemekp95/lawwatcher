using LawWatcher.BuildingBlocks.Configuration;

namespace LawWatcher.Api.Runtime;

public static class ConfiguredBootstrapHostedServicesPolicy
{
    public static bool ShouldRegister(RuntimeProfile runtimeProfile, BootstrapOptions bootstrapOptions)
    {
        return runtimeProfile != RuntimeProfile.Production
            || bootstrapOptions.EnableDemoData
            || bootstrapOptions.EnableInitialOperator
            || bootstrapOptions.EnableInitialApiClient;
    }
}
