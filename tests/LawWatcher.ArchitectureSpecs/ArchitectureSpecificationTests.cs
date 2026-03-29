using System.Xml.Linq;
using Xunit;

public sealed class ArchitectureSpecificationTests
{
    [Fact]
    public void Bounded_context_projects_follow_architecture_rules()
    {
        var repoRoot = FindRepoRoot();
        var boundedContextsRoot = Path.Combine(repoRoot, "src", "BoundedContexts");
        var failures = new List<string>();
        var layers = new[] { "Domain", "Application", "Infrastructure", "Contracts" };
        var contexts = new[]
        {
            "LegislativeIntake",
            "LegislativeProcess",
            "LegalCorpus",
            "TaxonomyAndProfiles",
            "AiEnrichment",
            "SearchAndDiscovery",
            "Notifications",
            "IdentityAndAccess",
            "IntegrationApi"
        };

        foreach (var context in contexts)
        {
            foreach (var layer in layers)
            {
                var projectPath = Path.Combine(
                    boundedContextsRoot,
                    context,
                    $"LawWatcher.{context}.{layer}",
                    $"LawWatcher.{context}.{layer}.csproj");

                if (!File.Exists(projectPath))
                {
                    failures.Add($"Missing project: {projectPath}");
                }
            }

            var domainReferences = GetProjectReferences(boundedContextsRoot, context, "Domain");
            if (domainReferences.Any(path => path.Contains(".Infrastructure", StringComparison.OrdinalIgnoreCase) || path.Contains(".Application", StringComparison.OrdinalIgnoreCase)))
            {
                failures.Add($"{context}.Domain must not reference Application or Infrastructure projects.");
            }

            var applicationReferences = GetProjectReferences(boundedContextsRoot, context, "Application");
            if (applicationReferences.Any(path => path.Contains(".Infrastructure", StringComparison.OrdinalIgnoreCase)))
            {
                failures.Add($"{context}.Application must not reference Infrastructure projects.");
            }

            var contractsReferences = GetProjectReferences(boundedContextsRoot, context, "Contracts");
            if (contractsReferences.Any(path => path.Contains(".Infrastructure", StringComparison.OrdinalIgnoreCase)))
            {
                failures.Add($"{context}.Contracts must not reference Infrastructure projects.");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static string[] GetProjectReferences(string boundedContextsRoot, string context, string layer)
    {
        var projectPath = Path.Combine(
            boundedContextsRoot,
            context,
            $"LawWatcher.{context}.{layer}",
            $"LawWatcher.{context}.{layer}.csproj");

        if (!File.Exists(projectPath))
        {
            return [];
        }

        var document = XDocument.Load(projectPath);
        return document
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value ?? string.Empty)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "LawWatcher.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root from the test output directory.");
    }
}
