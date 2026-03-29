using Xunit;

public sealed class RuntimeInfrastructureSpecificationTests
{
    [Fact]
    public async Task Runtime_infrastructure_scenarios_pass()
    {
        var failures = new List<string>();

        await RuntimeInfrastructureScenarios.RunAsync(failures);

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}
