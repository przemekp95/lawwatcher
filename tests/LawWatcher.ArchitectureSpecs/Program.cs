using System.Xml.Linq;

var repoRoot = Directory.GetCurrentDirectory();
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

    var domainReferences = GetProjectReferences(context, "Domain");
    if (domainReferences.Any(path => path.Contains(".Infrastructure", StringComparison.OrdinalIgnoreCase) || path.Contains(".Application", StringComparison.OrdinalIgnoreCase)))
    {
        failures.Add($"{context}.Domain must not reference Application or Infrastructure projects.");
    }

    var applicationReferences = GetProjectReferences(context, "Application");
    if (applicationReferences.Any(path => path.Contains(".Infrastructure", StringComparison.OrdinalIgnoreCase)))
    {
        failures.Add($"{context}.Application must not reference Infrastructure projects.");
    }

    var contractsReferences = GetProjectReferences(context, "Contracts");
    if (contractsReferences.Any(path => path.Contains(".Infrastructure", StringComparison.OrdinalIgnoreCase)))
    {
        failures.Add($"{context}.Contracts must not reference Infrastructure projects.");
    }
}

if (failures.Count != 0)
{
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($"FAIL: {failure}");
    }

    return 1;
}

Console.WriteLine("Architecture specifications passed.");
return 0;

string[] GetProjectReferences(string context, string layer)
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
