using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PayFlow.Application;
using PayFlow.Application.Abstractions;
using PayFlow.Domain.Common;
using PayFlow.Domain.Payments;
using PayFlow.Infrastructure.Caching;
using PayFlow.Infrastructure.Idempotency;
using PayFlow.Infrastructure.Payments;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Infrastructure.Tenancy;

namespace PayFlow.Integration.Tests;

/// <summary>A clock the test moves, so a year of billing takes milliseconds.</summary>
public sealed class TestClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;

    public void Advance(TimeSpan by) => UtcNow += by;

    public void AdvanceDays(double days) => UtcNow = UtcNow.AddDays(days);
}

/// <summary>
/// A gateway whose outcome the test dictates, so dunning paths can be driven
/// deterministically instead of hoping the simulator declines when needed.
/// </summary>
public sealed class ScriptedGateway : IPaymentGateway
{
    private readonly Queue<ChargeResult> _scripted = new();

    public string Name => "scripted";

    public ChargeResult Default { get; set; } = ChargeResult.Success("ref_default");

    public List<ChargeRequest> Charges { get; } = [];

    public void Enqueue(params ChargeResult[] results)
    {
        foreach (var result in results)
        {
            _scripted.Enqueue(result);
        }
    }

    public Task<ChargeResult> ChargeAsync(ChargeRequest request, CancellationToken cancellationToken = default)
    {
        Charges.Add(request);
        var result = _scripted.Count > 0 ? _scripted.Dequeue() : Default;
        return Task.FromResult(result);
    }

    public Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(RefundResult.Success("ref_refund"));
}

/// <summary>
/// Wires the real application and infrastructure services over the integration test
/// database, with a movable clock and a scripted gateway.
/// <para>
/// Each harness gets two freshly generated tenant ids, so tests share one schema without
/// sharing any data - the same isolation mechanism the product relies on in production.
/// </para>
/// </summary>
public sealed class BillingHarness : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private BillingHarness(ServiceProvider services, TestClock clock, ScriptedGateway gateway)
    {
        _services = services;
        Clock = clock;
        Gateway = gateway;
    }

    public TestClock Clock { get; }

    public ScriptedGateway Gateway { get; }

    public TenantId TenantA { get; } = TenantId.New();

    public TenantId TenantB { get; } = TenantId.New();

    public static Task<BillingHarness> CreateAsync(
        PostgresFixture postgres,
        DunningPolicy? policy = null,
        DateTimeOffset? start = null)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        var clock = new TestClock(start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var gateway = new ScriptedGateway();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));

        services.AddSingleton<IClock>(clock);
        services.AddSingleton<IPaymentGateway>(gateway);
        services.AddSingleton<ICacheStore, InMemoryCacheStore>();

        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<ITenantContextSetter>(sp => sp.GetRequiredService<TenantContext>());

        services.AddDbContext<PayFlowDbContext>(options => options.UseNpgsql(postgres.ConnectionString));
        services.AddScoped<IPayFlowDbContext>(sp => sp.GetRequiredService<PayFlowDbContext>());
        services.AddScoped<IInvoiceNumberSequence, InvoiceNumberSequence>();
        services.AddScoped<IIdempotencyStore, EfIdempotencyStore>();
        services.AddScoped<ITenantDirectory, DatabaseTenantDirectory>();

        services.AddPayFlowApplication(policy);

        // The schema is already applied by PostgresFixture; nothing to do per harness.
        return Task.FromResult(new BillingHarness(services.BuildServiceProvider(), clock, gateway));
    }

    /// <summary>Opens a scope pinned to <paramref name="tenantId"/>, as a request or job would.</summary>
    public IServiceScope Scope(TenantId? tenantId = null)
    {
        var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().SetTenant(tenantId ?? TenantA);
        return scope;
    }

    public async Task<T> AsTenantAsync<T>(TenantId tenantId, Func<IServiceProvider, Task<T>> action)
    {
        using var scope = Scope(tenantId);
        return await action(scope.ServiceProvider);
    }

    public Task<T> AsTenantAsync<T>(Func<IServiceProvider, Task<T>> action) => AsTenantAsync(TenantA, action);

    public async Task AsTenantAsync(TenantId tenantId, Func<IServiceProvider, Task> action)
    {
        using var scope = Scope(tenantId);
        await action(scope.ServiceProvider);
    }

    public Task AsTenantAsync(Func<IServiceProvider, Task> action) => AsTenantAsync(TenantA, action);

    public ValueTask DisposeAsync() => _services.DisposeAsync();
}
