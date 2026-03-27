using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using LawWatcher.Portal.Services;
using LawWatcher.Portal.Components;

var builder = WebApplication.CreateBuilder(args);

var dataProtectionKeysDirectory = new DirectoryInfo(Path.Combine(
    builder.Environment.ContentRootPath,
    "artifacts",
    "dataprotection",
    builder.Environment.ApplicationName));
dataProtectionKeysDirectory.Create();

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
if (builder.Environment.IsDevelopment())
{
    builder.Logging.AddDebug();
}

builder.Services
    .AddDataProtection()
    .PersistKeysToFileSystem(dataProtectionKeysDirectory);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.Configure<PortalApiOptions>(builder.Configuration.GetSection("LawWatcher:PortalApi"));
builder.Services.AddHttpClient<LawWatcherPortalApiClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<IOptions<PortalApiOptions>>().Value;
    client.BaseAddress = options.GetBaseUri();
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddScoped<LawWatcherPortalAdminClient>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
if (ShouldUseHttpsRedirection(builder.Configuration))
{
    app.UseHttpsRedirection();
}

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static bool ShouldUseHttpsRedirection(IConfiguration configuration)
{
    var configuredUrls = configuration["ASPNETCORE_URLS"] ?? configuration["urls"] ?? string.Empty;
    return configuredUrls
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Any(url => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
}
