using PayFlow.Application;
using PayFlow.Infrastructure;
using PayFlow.Workers;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
    options.UseUtcTimestamp = true;
});

builder.Services.AddPayFlowInfrastructure(builder.Configuration);
builder.Services.AddPayFlowApplication();

builder.Services.AddOptions<WorkerOptions>()
    .Bind(builder.Configuration.GetSection(WorkerOptions.SectionName))
    .Validate(
        x => x.BillingCycleInterval > TimeSpan.Zero
             && x.DunningInterval > TimeSpan.Zero
             && x.OutboxInterval > TimeSpan.Zero,
        "Worker intervals must be positive.")
    .ValidateOnStart();

builder.Services.AddHostedService<BillingCycleWorker>();
builder.Services.AddHostedService<DunningWorker>();
builder.Services.AddHostedService<OutboxDispatcherWorker>();

// The web host owns the schema. Running migrations from two processes at once races on
// the migration history table, so the workers only ever read a schema someone else applied.
await builder.Build().RunAsync();
