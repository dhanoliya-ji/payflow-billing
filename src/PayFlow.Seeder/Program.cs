using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PayFlow.Application;
using PayFlow.Application.Abstractions;
using PayFlow.Infrastructure;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Seeder;

// Simple positional/flag parsing; this is a developer tool, not a CLI product.
var months = ArgValue("--months", 12);
var customers = ArgValue("--customers", 60);
var seed = ArgValue("--seed", 42);
var reset = args.Contains("--reset", StringComparer.Ordinal);

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    // Redis adds nothing to a seed run and its absence should not slow startup.
    ["ConnectionStrings:Redis"] = null,
});

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);

builder.Services.AddPayFlowInfrastructure(builder.Configuration);
builder.Services.AddPayFlowApplication();

// The simulation needs to move time, so the real clock is replaced wholesale. Everything
// downstream - renewal dates, dunning retry schedules, invoice due dates - follows.
var start = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddMonths(-months);
var clock = new SimulationClock(start);
builder.Services.AddSingleton(clock);
builder.Services.AddSingleton<IClock>(clock);

builder.Services.AddSingleton<BillingSimulation>();

using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Seeder");

using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PayFlowDbContext>();

    if (reset)
    {
        logger.LogWarning("--reset given: dropping the payflow database and recreating it");
        await db.Database.EnsureDeletedAsync();
    }

    await db.Database.MigrateAsync();
}

var simulation = host.Services.GetRequiredService<BillingSimulation>();

// Two tenants, matching the development API keys in appsettings.Development.json, so the
// seeded data is reachable through the API and the tenant isolation is visible in the
// analytics: querying as one tenant must never surface the other's invoices.
var tenants = new[]
{
    new SimulationSettings(
        Guid.Parse("11111111-1111-1111-1111-111111111111"), "Acme Corp", months, customers, seed),
    new SimulationSettings(
        Guid.Parse("22222222-2222-2222-2222-222222222222"), "Globex", months, Math.Max(10, customers / 3), seed + 1),
};

logger.LogInformation(
    "Simulating {Months} months of billing from {Start:yyyy-MM-dd}, seed {Seed}",
    months,
    start,
    seed);

foreach (var tenant in tenants)
{
    // Each tenant replays the same window, so both end at "now" rather than the second
    // starting where the first finished.
    clock.Reset(start);
    await simulation.RunAsync(tenant);
}

logger.LogInformation("Done. Explore it at /dashboard, or point analytics/payflow_analytics.ipynb at the database.");

int ArgValue(string name, int fallback)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var value)
        ? value
        : fallback;
}
