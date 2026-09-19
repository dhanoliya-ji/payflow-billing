using Microsoft.EntityFrameworkCore;
using PayFlow.Domain;
using PayFlow.Infrastructure;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddDbContextCheck<PayFlowDbContext>(tags: ["ready"]);
builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
builder.Services.AddDbContext<PayFlowDbContext>(options => options.UseNpgsql(
    builder.Configuration.GetConnectionString("Postgres") ?? "Host=localhost;Port=55433;Database=payflow;Username=payflow;Password=payflow"));
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(
    builder.Configuration.GetConnectionString("Redis") ?? "localhost:6380"));
builder.Services.AddSingleton<ICache, RedisCache>();

var app = builder.Build();
// MVP startup initialization: EnsureCreated is intentional until a first migration is introduced.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PayFlowDbContext>();
    db.Database.EnsureCreated();
    if (!db.Customers.IgnoreQueryFilters().Any())
    {
        var tenant = new TenantId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        db.Customers.Add(new Customer(tenant, "alex@example.com", "Alex Johnson"));
        db.Customers.Add(new Customer(tenant, "sam@example.com", "Sam Rivera"));
        db.Plans.AddRange(new Plan(tenant, "STARTER", "Starter", 29m, BillingInterval.Monthly),
            new Plan(tenant, "GROWTH", "Growth", 99m, BillingInterval.Monthly));
        db.SaveChanges();
    }
}
app.Use(async (context, next) =>
{
    var tenant = context.Request.Headers["X-Tenant-Id"].FirstOrDefault();
    var id = Guid.TryParse(tenant, out var parsed) ? parsed : Guid.Parse("11111111-1111-1111-1111-111111111111");
    context.RequestServices.GetRequiredService<TenantContext>().TenantId = new TenantId(id);
    await next();
});
app.MapOpenApi();
app.MapHealthChecks("/health");
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.MapGet("/", () => Results.Redirect("/dashboard"));

app.MapGet("/dashboard", async (PayFlowDbContext db) =>
{
    var customers = await db.Customers.AsNoTracking().OrderBy(x => x.DisplayName).ToListAsync();
    var plans = await db.Plans.AsNoTracking().Where(x => x.IsActive).ToListAsync();
    var subscriptions = await db.Subscriptions.AsNoTracking().ToListAsync();
    var invoices = await db.Invoices.AsNoTracking().OrderByDescending(x => x.IssuedAt).Take(20).ToListAsync();
    var customerOptions = string.Join("", customers.Select(x => $"<option value='{x.Id}'>{E(x.DisplayName)} ({E(x.Email)})</option>"));
    var planOptions = string.Join("", plans.Select(x => $"<option value='{x.Id}'>{E(x.Name)} - {x.Amount:C}</option>"));
    var invoiceOptions = string.Join("", invoices.Where(x => x.Status != Subscription.InvoiceStatus.Paid).Select(x => $"<option value='{x.Id}'>{x.Id.ToString()[..8]} - {x.Total:C}</option>"));
    var rows = string.Join("", subscriptions.Select(x => $"<tr><td>{x.Id.ToString()[..8]}</td><td>{x.Status}</td><td>{x.CurrentPeriodEnd:d}</td></tr>"));
    var invoiceRows = string.Join("", invoices.Select(x => $"<tr><td>{x.Id.ToString()[..8]}</td><td>{x.Status}</td><td>{x.Total:C}</td></tr>"));
    var subscriptionOptions = string.Join("", subscriptions
        .Where(x => x.Status is SubscriptionStatus.Active or SubscriptionStatus.Trialing)
        .Select(x => $"<option value='{x.Id}'>{x.Id.ToString()[..8]}</option>"));

    var html = "<!doctype html><html><head><meta charset='utf-8'><title>PayFlow</title>" +
        "<script src='https://unpkg.com/htmx.org@2.0.4'></script>" +
        "<style>body{font:16px system-ui;max-width:1100px;margin:2rem auto;color:#182230}section{border:1px solid #ddd;border-radius:8px;padding:1rem;margin:1rem 0}form{display:flex;gap:.5rem;flex-wrap:wrap}input,select,button{padding:.55rem}table{width:100%;border-collapse:collapse}td,th{padding:.5rem;border-bottom:1px solid #ddd;text-align:left}.grid{display:grid;grid-template-columns:1fr 1fr;gap:1rem}</style>" +
        "</head><body><h1>PayFlow</h1>" +
        $"<p>Tenant-scoped billing MVP · customers {customers.Count} · subscriptions {subscriptions.Count}</p>" +
        "<div class='grid'><section><h2>Customer</h2><form hx-post='/customers' hx-target='#notice'><input name='Email' type='email' placeholder='Email' required><input name='DisplayName' placeholder='Name' required><button>Create</button></form></section>" +
        "<section><h2>Plan</h2><form hx-post='/plans' hx-target='#notice'><input name='Code' placeholder='Code' required><input name='Name' placeholder='Name' required><input name='Amount' type='number' step='.01' min='0' placeholder='Price' required><select name='Interval'><option>Monthly</option><option>Yearly</option></select><button>Create</button></form></section>" +
        "<section><h2>Subscription</h2><form hx-post='/subscriptions' hx-target='#notice'><select name='CustomerId' required>" + customerOptions + "</select><select name='PlanId' required>" + planOptions + "</select><label><input type='checkbox' name='Trial'> Trial</label><button>Create</button></form></section>" +
        "<section><h2>Usage</h2><form hx-post='/usage' hx-target='#notice'><select name='CustomerId' required>" + customerOptions + "</select><input name='Metric' placeholder='Metric' required><input name='Quantity' type='number' step='.01' min='.01' placeholder='Quantity' required><button>Record</button></form></section>" +
        "<section><h2>Invoice</h2><form hx-post='/invoices' hx-target='#notice'><select name='SubscriptionId' required>" + subscriptionOptions + "</select><button>Generate</button></form></section>" +
        "<section><h2>Mock payment</h2><form hx-post='/payments' hx-target='#notice'><select name='InvoiceId' required>" + invoiceOptions + "</select><input name='Reference' placeholder='Reference'><button>Record payment</button></form></section></div>" +
        "<p id='notice'></p><section><h2>Subscriptions</h2><table><tr><th>ID</th><th>Status</th><th>Period end</th></tr>" + rows + "</table></section>" +
        "<section><h2>Invoices</h2><table><tr><th>ID</th><th>Status</th><th>Total</th></tr>" + invoiceRows + "</table></section></body></html>";
    return Results.Content(html, "text/html");
});

app.MapPost("/customers", async (CustomerForm form, PayFlowDbContext db, ITenantContext tenant) =>
{
    try { db.Customers.Add(new Customer(tenant.TenantId, form.Email, form.DisplayName)); await db.SaveChangesAsync(); return Notice("Customer created."); }
    catch (ArgumentException e) { return Results.BadRequest(Notice(e.Message)); }
});
app.MapGet("/customers", async (PayFlowDbContext db) => Results.Ok(await db.Customers.AsNoTracking().ToListAsync()));
app.MapPost("/plans", async (PlanForm form, PayFlowDbContext db, ITenantContext tenant) =>
{
    if (!Enum.TryParse<BillingInterval>(form.Interval, true, out var interval)) return Results.BadRequest(Notice("Interval must be Monthly or Yearly."));
    try { db.Plans.Add(new Plan(tenant.TenantId, form.Code, form.Name, form.Amount, interval)); await db.SaveChangesAsync(); return Notice("Plan created."); }
    catch (ArgumentException e) { return Results.BadRequest(Notice(e.Message)); }
});
app.MapGet("/plans", async (PayFlowDbContext db) => Results.Ok(await db.Plans.AsNoTracking().ToListAsync()));
app.MapPost("/subscriptions", async (SubscriptionForm form, PayFlowDbContext db, ITenantContext tenant) =>
{
    var plan = await db.Plans.SingleOrDefaultAsync(x => x.Id == form.PlanId);
    if (!await db.Customers.AnyAsync(x => x.Id == form.CustomerId) || plan is null)
        return Results.BadRequest(Notice("Customer and active plan must belong to this tenant."));

    db.Subscriptions.Add(new Subscription(tenant.TenantId, form.CustomerId, form.PlanId, DateTimeOffset.UtcNow, plan.Interval, form.Trial));
    await db.SaveChangesAsync(); return Notice("Subscription created.");
});
app.MapGet("/subscriptions", async (PayFlowDbContext db) => Results.Ok(await db.Subscriptions.AsNoTracking().ToListAsync()));
app.MapPost("/usage", async (UsageForm form, PayFlowDbContext db, ITenantContext tenant) =>
{
    if (!await db.Customers.AnyAsync(x => x.Id == form.CustomerId)) return Results.BadRequest(Notice("Customer not found."));
    try { db.UsageRecords.Add(new Subscription.UsageRecord(tenant.TenantId, form.CustomerId, form.Metric, form.Quantity, DateTimeOffset.UtcNow)); await db.SaveChangesAsync(); return Notice("Usage recorded."); }
    catch (ArgumentException e) { return Results.BadRequest(Notice(e.Message)); }
});
app.MapGet("/usage", async (PayFlowDbContext db) => Results.Ok(await db.UsageRecords.AsNoTracking().OrderByDescending(x => x.RecordedAt).ToListAsync()));
app.MapPost("/invoices", async (InvoiceForm form, PayFlowDbContext db, ITenantContext tenant) =>
{
    await using var tx = await db.Database.BeginTransactionAsync();
    var sub = await db.Subscriptions.SingleOrDefaultAsync(x => x.Id == form.SubscriptionId);
    if (sub is null || sub.Status is not (SubscriptionStatus.Active or SubscriptionStatus.Trialing)) return Results.BadRequest(Notice("Only active subscriptions can be invoiced."));
    var plan = await db.Plans.SingleAsync(x => x.Id == sub.PlanId);
    var invoice = new Subscription.Invoice(tenant.TenantId, sub.CustomerId, sub.CurrentPeriodStart, sub.CurrentPeriodEnd);
    invoice.AddLine($"{plan.Name} ({plan.Interval})", 1, plan.Amount);
    var usage = await db.UsageRecords.Where(x => x.CustomerId == sub.CustomerId && !x.Invoiced && x.RecordedAt >= sub.CurrentPeriodStart && x.RecordedAt < sub.CurrentPeriodEnd).ToListAsync();
    foreach (var record in usage) { invoice.AddLine($"{record.Metric} usage", record.Quantity, 0); record.MarkInvoiced(); }
    db.Invoices.Add(invoice); await db.SaveChangesAsync(); await tx.CommitAsync();
    return Notice($"Invoice {invoice.Id.ToString()[..8]} generated: {invoice.Total:C}");
});
app.MapGet("/invoices", async (PayFlowDbContext db) => Results.Ok(await db.Invoices.AsNoTracking().Include(x => x.Lines).ToListAsync()));
app.MapPost("/payments", async (PaymentForm form, PayFlowDbContext db, ITenantContext tenant) =>
{
    await using var tx = await db.Database.BeginTransactionAsync();
    var invoice = await db.Invoices.SingleOrDefaultAsync(x => x.Id == form.InvoiceId);
    if (invoice is null || invoice.Status == Subscription.InvoiceStatus.Paid) return Results.BadRequest(Notice("Invoice not found or already paid."));
    var payment = new Subscription.Payment(tenant.TenantId, invoice.Id, invoice.Total, form.Reference ?? "");
    invoice.MarkPaid(); db.Payments.Add(payment); await db.SaveChangesAsync(); await tx.CommitAsync();
    return Notice("Mock payment recorded.");
});
app.MapGet("/payments", async (PayFlowDbContext db) => Results.Ok(await db.Payments.AsNoTracking().ToListAsync()));
app.Run();

static IResult Notice(string message) => Results.Content($"<span>{System.Net.WebUtility.HtmlEncode(message)}</span>", "text/html");
static string E(string value) => System.Net.WebUtility.HtmlEncode(value);
record CustomerForm(string Email, string DisplayName);
record PlanForm(string Code, string Name, decimal Amount, string Interval);
record SubscriptionForm(Guid CustomerId, Guid PlanId, bool Trial);
record UsageForm(Guid CustomerId, string Metric, decimal Quantity);
record InvoiceForm(Guid SubscriptionId);
record PaymentForm(Guid InvoiceId, string? Reference);
