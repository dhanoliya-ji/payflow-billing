using Microsoft.EntityFrameworkCore;
using PayFlow.Domain;
using PayFlow.Infrastructure;
using PayFlow.Workers;

var builder = Host.CreateApplicationBuilder(args);
var defaultTenantId = new TenantId(Guid.Parse("11111111-1111-1111-1111-111111111111"));

builder.Services.AddScoped<ITenantContext>(_ => new TenantContext { TenantId = defaultTenantId });
builder.Services.AddDbContext<PayFlowDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("Postgres") ?? "Host=localhost;Port=55433;Database=payflow;Username=payflow;Password=payflow";
    options.UseNpgsql(connectionString);
});
builder.Services.AddHostedService<BillingWorker>();
await builder.Build().RunAsync();
