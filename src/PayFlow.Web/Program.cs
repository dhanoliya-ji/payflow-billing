using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using PayFlow.Application;
using PayFlow.Infrastructure;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Web.Authentication;
using PayFlow.Web.Dashboard;
using PayFlow.Web.Diagnostics;
using PayFlow.Web.Endpoints;

var builder = WebApplication.CreateBuilder(args);

// Structured JSON logs: these are read by a log pipeline far more often than by a human
// tailing a console, and the fields the billing path logs (subscription id, tenant,
// invoice number) are only queryable if they survive as fields.
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
    options.UseUtcTimestamp = true;
});

builder.Services.AddPayFlowInfrastructure(builder.Configuration);
builder.Services.AddPayFlowApplication();

var apiKeyOptions = new ApiKeyOptions();
builder.Configuration.GetSection(ApiKeyOptions.SectionName).Bind(apiKeyOptions);
builder.Services.AddSingleton(new ApiKeyRegistry(apiKeyOptions));

builder.Services.AddSingleton<PayFlowMetrics>();
builder.Services.AddSingleton<PrometheusExporter>();
builder.Services.AddMetrics();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DomainExceptionHandler>();

builder.Services.AddOpenApi();

builder.Services.AddHealthChecks()
    .AddDbContextCheck<PayFlowDbContext>("database", tags: ["ready"]);

var app = builder.Build();

// Refusing to start beats starting insecure: with no tenants configured and anonymous
// development access off, every request would 401 and the cause would not be obvious
// from the logs.
if (apiKeyOptions.Tenants.Count == 0 && !apiKeyOptions.AllowAnonymousDevelopmentTenant)
{
    throw new InvalidOperationException(
        $"No tenants are configured under '{ApiKeyOptions.SectionName}:Tenants', and " +
        $"'{ApiKeyOptions.SectionName}:AllowAnonymousDevelopmentTenant' is false. " +
        "The service would reject every request. Configure at least one tenant API key.");
}

if (apiKeyOptions.AllowAnonymousDevelopmentTenant && !app.Environment.IsDevelopment())
{
    throw new InvalidOperationException(
        "AllowAnonymousDevelopmentTenant is enabled outside the Development environment. " +
        "That would let unauthenticated callers read and write the development tenant's billing data.");
}

await ApplyMigrationsAsync(app);

app.UseExceptionHandler();
app.UseMiddleware<TenantResolutionMiddleware>();

app.MapHealthChecks("/health");
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

app.MapGet("/metrics", (PrometheusExporter exporter) =>
        Results.Text(exporter.Render(), "text/plain; version=0.0.4; charset=utf-8"))
    .ExcludeFromDescription();

app.MapOpenApi();

app.MapDashboard();
app.MapCustomerEndpoints();
app.MapPlanEndpoints();
app.MapSubscriptionEndpoints();
app.MapUsageEndpoints();
app.MapInvoiceEndpoints();
app.MapPaymentEndpoints();
app.MapOperationsEndpoints();
app.MapAnalyticsEndpoints();

await app.RunAsync();

/// <summary>
/// Brings the schema up to date at startup.
/// <para>
/// This replaces the previous <c>EnsureCreated</c> plus inline seed data. EnsureCreated
/// creates the schema but records no migration history, so the first real migration then
/// fails against a database it did not create. Migrating is also what makes a container
/// deploy work without a separate schema step.
/// </para>
/// </summary>
static async Task ApplyMigrationsAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<PayFlowDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

    var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
    if (pending.Count == 0)
    {
        logger.LogInformation("Database schema is up to date");
        return;
    }

    logger.LogInformation("Applying {Count} pending migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
    await db.Database.MigrateAsync();
}

/// <summary>Exposed so the integration tests can drive the host with WebApplicationFactory.</summary>
public partial class Program;
