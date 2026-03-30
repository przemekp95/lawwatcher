using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using LawWatcher.Api;
using LawWatcher.IdentityAndAccess.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class IntegrationApiAuthorizationSpecificationTests
{
    private static readonly string[] ReadEndpointPaths =
    [
        "/v1/system/capabilities",
        "/v1/search?q=vat",
        "/v1/ai/tasks",
        "/v1/bills",
        "/v1/processes",
        "/v1/acts",
        "/v1/events",
        "/v1/profiles",
        "/v1/subscriptions",
        "/v1/alerts",
        "/v1/webhooks",
        "/v1/backfills",
        "/v1/replays"
    ];

    [Theory]
    [MemberData(nameof(GetReadEndpointPaths))]
    public async Task Integration_reads_require_bearer_token(string path)
    {
        using var factory = new IntegrationApiWebApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(GetReadEndpointPaths))]
    public async Task Integration_reads_require_integration_read_scope(string path)
    {
        using var factory = new IntegrationApiWebApplicationFactory();
        await factory.SeedApiClientAsync(
            "write-only-client",
            "Write Only Client",
            "write-only-token",
            ["profiles:write"]);

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "write-only-token");

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(GetReadEndpointPaths))]
    public async Task Integration_reads_allow_integration_read_scope(string path)
    {
        using var factory = new IntegrationApiWebApplicationFactory();
        await factory.SeedApiClientAsync(
            "read-client",
            "Read Client",
            "read-client-token",
            ["integration:read"]);

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "read-client-token");

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Integration_openapi_document_marks_read_operations_with_bearer_security()
    {
        using var factory = new IntegrationApiWebApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        using var response = await client.GetAsync("/openapi/integration-v1.json");
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.True(
            root.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("Bearer", out _),
            "Integration OpenAPI document should declare the bearer security scheme.");

        Assert.True(
            root.TryGetProperty("security", out var securityRequirements) && securityRequirements.GetArrayLength() != 0,
            "Integration OpenAPI document should advertise bearer security for the integration surface.");
    }

    public static IEnumerable<object[]> GetReadEndpointPaths()
    {
        return ReadEndpointPaths.Select(path => new object[] { path });
    }

    private sealed class IntegrationApiWebApplicationFactory : WebApplicationFactory<ApiAssemblyMarker>
    {
        private readonly string _artifactsRoot = Path.Combine(Path.GetTempPath(), "lawwatcher-api-specs", Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, string?> _previousEnvironmentVariables = new(StringComparer.Ordinal);
        private int _disposeState;

        public IntegrationApiWebApplicationFactory()
        {
            SetEnvironmentVariable("LAWWATCHER__RUNTIME__PROFILE", "dev");
            SetEnvironmentVariable("LAWWATCHER__BOOTSTRAP__ENABLEDEMODATA", "false");
            SetEnvironmentVariable("LAWWATCHER__BOOTSTRAP__ENABLEINITIALOPERATOR", "false");
            SetEnvironmentVariable("LAWWATCHER__BOOTSTRAP__ENABLEINITIALAPICLIENT", "false");
            SetEnvironmentVariable("LAWWATCHER__BOOTSTRAP__SECRET", "spec-bootstrap-secret");
            SetEnvironmentVariable("LAWWATCHER__STORAGE__PROVIDER", "files");
            SetEnvironmentVariable("LAWWATCHER__STORAGE__STATEROOT", Path.Combine(_artifactsRoot, "state"));
            SetEnvironmentVariable("STORAGE__LOCALDOCUMENTSROOT", Path.Combine(_artifactsRoot, "documents"));
            SetEnvironmentVariable("CONNECTIONSTRINGS__RABBITMQ", string.Empty);
            SetEnvironmentVariable("CONNECTIONSTRINGS__LAWWATCHERSQLSERVER", string.Empty);
            SetEnvironmentVariable("SEARCH__OPENSEARCH__BASEURL", string.Empty);
        }

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            Directory.CreateDirectory(_artifactsRoot);

            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["LawWatcher:Runtime:Profile"] = "dev",
                    ["LawWatcher:Bootstrap:EnableDemoData"] = "false",
                    ["LawWatcher:Bootstrap:EnableInitialOperator"] = "false",
                    ["LawWatcher:Bootstrap:EnableInitialApiClient"] = "false",
                    ["LawWatcher:Bootstrap:Secret"] = "spec-bootstrap-secret",
                    ["LawWatcher:Storage:Provider"] = "files",
                    ["LawWatcher:Storage:StateRoot"] = Path.Combine(_artifactsRoot, "state"),
                    ["Storage:LocalDocumentsRoot"] = Path.Combine(_artifactsRoot, "documents"),
                    ["ConnectionStrings:RabbitMq"] = string.Empty,
                    ["ConnectionStrings:LawWatcherSqlServer"] = string.Empty,
                    ["Search:OpenSearch:BaseUrl"] = string.Empty
                });
            });
        }

        public async Task SeedApiClientAsync(string clientIdentifier, string name, string token, IReadOnlyCollection<string> scopes)
        {
            using var scope = Services.CreateScope();
            var tokenFingerprintService = scope.ServiceProvider.GetRequiredService<IApiTokenFingerprintService>();
            var commandService = scope.ServiceProvider.GetRequiredService<ApiClientsCommandService>();

            await commandService.RegisterAsync(
                new RegisterApiClientCommand(
                    Guid.NewGuid(),
                    name,
                    clientIdentifier,
                    tokenFingerprintService.CreateFingerprint(token),
                    scopes),
                CancellationToken.None);
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposing)
            {
                base.Dispose(disposing);
                return;
            }

            if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            {
                return;
            }

            try
            {
                foreach (var (name, value) in _previousEnvironmentVariables)
                {
                    Environment.SetEnvironmentVariable(name, value);
                }

                if (Directory.Exists(_artifactsRoot))
                {
                    Directory.Delete(_artifactsRoot, recursive: true);
                }
            }
            finally
            {
                base.Dispose(disposing);
            }
        }

        private void SetEnvironmentVariable(string name, string value)
        {
            _previousEnvironmentVariables[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}
