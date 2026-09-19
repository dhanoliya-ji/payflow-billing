using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PayFlow.Application.Abstractions;
using PayFlow.Infrastructure.Caching;
using PayFlow.Infrastructure.Idempotency;
using PayFlow.Infrastructure.Payments;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Infrastructure.Tenancy;
using PayFlow.Infrastructure.Time;
using StackExchange.Redis;

namespace PayFlow.Infrastructure;

public static class DependencyInjection
{
    public const string DefaultPostgresConnectionString =
        "Host=localhost;Port=55433;Database=payflow;Username=payflow;Password=payflow";

    /// <summary>
    /// Registers persistence, tenancy, the clock, the cache and the payment gateway.
    /// Redis is optional: when no connection string is configured, or the connection
    /// cannot be established at startup, the application falls back to a process-local
    /// cache rather than refusing to start over an optional dependency.
    /// </summary>
    public static IServiceCollection AddPayFlowInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<IClock, SystemClock>();

        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<ITenantContextSetter>(sp => sp.GetRequiredService<TenantContext>());

        var postgres = configuration.GetConnectionString("Postgres") ?? DefaultPostgresConnectionString;
        services.AddDbContext<PayFlowDbContext>(options => options.UseNpgsql(
            postgres,
            npgsql => npgsql.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(10),
                errorCodesToAdd: null)));

        services.AddScoped<IPayFlowDbContext>(sp => sp.GetRequiredService<PayFlowDbContext>());
        services.AddScoped<IInvoiceNumberSequence, InvoiceNumberSequence>();
        services.AddScoped<ITenantDirectory, DatabaseTenantDirectory>();
        services.AddScoped<IIdempotencyStore, EfIdempotencyStore>();

        services.AddOptions<PaymentGatewayOptions>()
            .Bind(configuration.GetSection(PaymentGatewayOptions.SectionName))
            .Validate(
                x => x.FirstAttemptSuccessRate is >= 0 and <= 1 && x.RetrySuccessRate is >= 0 and <= 1,
                "Gateway success rates must be between 0 and 1.")
            .ValidateOnStart();

        services.AddSingleton<IPaymentGateway, SimulatedPaymentGateway>();

        AddCache(services, configuration);

        return services;
    }

    private static void AddCache(IServiceCollection services, IConfiguration configuration)
    {
        var redisConnectionString = configuration.GetConnectionString("Redis");

        if (string.IsNullOrWhiteSpace(redisConnectionString))
        {
            services.AddSingleton<ICacheStore, InMemoryCacheStore>();
            return;
        }

        services.AddSingleton<ICacheStore>(sp =>
        {
            var logger = sp.GetRequiredService<ILoggerFactory>();

            try
            {
                var configurationOptions = ConfigurationOptions.Parse(redisConnectionString);

                // Without this, a Redis outage at startup throws and takes the whole
                // application down over a cache.
                configurationOptions.AbortOnConnectFail = false;
                configurationOptions.ConnectTimeout = 3000;

                var multiplexer = ConnectionMultiplexer.Connect(configurationOptions);
                return new RedisCacheStore(multiplexer, logger.CreateLogger<RedisCacheStore>());
            }
            catch (RedisConnectionException ex)
            {
                logger.CreateLogger(typeof(DependencyInjection)).LogWarning(
                    ex,
                    "Could not connect to Redis; falling back to an in-process cache");

                return new InMemoryCacheStore(sp.GetRequiredService<IClock>());
            }
        });
    }
}
