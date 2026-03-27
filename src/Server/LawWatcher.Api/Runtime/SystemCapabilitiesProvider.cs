using LawWatcher.BuildingBlocks.Configuration;
using Microsoft.Extensions.Options;

namespace LawWatcher.Api.Runtime;

public sealed record AiInfrastructureCapabilities(bool SupportsConfiguredLocalLlm);

public sealed record OcrInfrastructureCapabilities(bool SupportsConfiguredDocumentPipeline);

public interface ISystemCapabilitiesProvider
{
    SystemCapabilities Current { get; }
}

public sealed class ConfigurationSystemCapabilitiesProvider(
    IOptionsMonitor<LawWatcherRuntimeOptions> optionsMonitor,
    SearchInfrastructureCapabilities searchInfrastructureCapabilities,
    AiInfrastructureCapabilities aiInfrastructureCapabilities,
    OcrInfrastructureCapabilities ocrInfrastructureCapabilities)
    : ISystemCapabilitiesProvider
{
    public SystemCapabilities Current
    {
        get
        {
            var options = optionsMonitor.CurrentValue;
            var runtimeProfile = RuntimeProfile.Parse(options.Profile);
            var configuredCapabilities = SystemCapabilities.FromOptions(runtimeProfile, options.Capabilities);
            var effectiveSearchCapabilities = SearchCapabilitiesRuntimeResolver.Resolve(
                configuredCapabilities.Search,
                searchInfrastructureCapabilities);
            var effectiveAiCapabilities = configuredCapabilities.Ai.Enabled && aiInfrastructureCapabilities.SupportsConfiguredLocalLlm
                ? configuredCapabilities.Ai
                : configuredCapabilities.Ai with
                {
                    Enabled = false,
                    ActivationMode = AiActivationMode.Disabled,
                    MaxConcurrency = 0,
                    UnloadAfterIdle = TimeSpan.Zero
                };

            return configuredCapabilities with
            {
                Ai = effectiveAiCapabilities,
                Search = effectiveSearchCapabilities,
                OcrEnabled = configuredCapabilities.OcrEnabled && ocrInfrastructureCapabilities.SupportsConfiguredDocumentPipeline
            };
        }
    }
}
