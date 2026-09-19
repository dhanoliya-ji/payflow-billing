using Microsoft.Extensions.DependencyInjection;
using PayFlow.Application.Analytics;
using PayFlow.Application.Billing;
using PayFlow.Application.Customers;
using PayFlow.Application.Invoicing;
using PayFlow.Application.Payments;
using PayFlow.Application.Plans;
using PayFlow.Application.Subscriptions;
using PayFlow.Application.Usage;
using PayFlow.Domain.Payments;

namespace PayFlow.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the use-case services. The ports they depend on - persistence, the
    /// clock, the gateway, the cache - are supplied by the infrastructure layer, so the
    /// host must call <c>AddPayFlowInfrastructure</c> as well.
    /// </summary>
    /// <param name="dunningPolicy">
    /// Retry schedule for failed collections. Defaults to <see cref="DunningPolicy.Default"/>.
    /// </param>
    public static IServiceCollection AddPayFlowApplication(
        this IServiceCollection services,
        DunningPolicy? dunningPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton((dunningPolicy ?? DunningPolicy.Default).Validate());

        services.AddScoped<ICustomerService, CustomerService>();
        services.AddScoped<IPlanService, PlanService>();
        services.AddScoped<ISubscriptionService, SubscriptionService>();
        services.AddScoped<IUsageService, UsageService>();
        services.AddScoped<IAnalyticsService, AnalyticsService>();

        // One InvoiceService instance serves both roles, so an invoice issued by the
        // billing cycle and one issued through the API share all their state.
        services.AddScoped<InvoiceService>();
        services.AddScoped<IInvoiceService>(sp => sp.GetRequiredService<InvoiceService>());
        services.AddScoped<IInvoiceIssuer>(sp => sp.GetRequiredService<InvoiceService>());

        services.AddScoped<IPaymentService, PaymentService>();
        services.AddScoped<IBillingCycleService, BillingCycleService>();

        return services;
    }
}
