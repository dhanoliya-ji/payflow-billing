using System.Diagnostics;
using PayFlow.Application.Abstractions;
using PayFlow.Application.Analytics;
using PayFlow.Application.Billing;
using PayFlow.Application.Invoicing;
using PayFlow.Application.Payments;
using PayFlow.Domain.Invoicing;
using PayFlow.Domain.Payments;
using PayFlow.Web.Diagnostics;

namespace PayFlow.Web.Endpoints;

public static class InvoiceEndpoints
{
    public static IEndpointRouteBuilder MapInvoiceEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/v1/invoices").WithTags("Invoices");

        group.MapPost("/", async (
            GenerateInvoiceRequest request,
            IInvoiceService invoices,
            PayFlowMetrics metrics,
            CancellationToken cancellationToken) =>
        {
            var invoice = await invoices.GenerateForSubscriptionAsync(request, cancellationToken);
            metrics.InvoicesIssued.Add(1, new KeyValuePair<string, object?>("source", "api"));
            return Results.Created($"/v1/invoices/{invoice.Id}", invoice);
        })
        .WithName("GenerateInvoice")
        .WithSummary("Bills a subscription's current period on demand.");

        group.MapGet("/", async (
            IInvoiceService invoices,
            CancellationToken cancellationToken,
            int? page = null,
            int? pageSize = null,
            InvoiceStatus? status = null,
            Guid? customerId = null) =>
            Results.Ok(await invoices.ListAsync(
                PageRequest.Of(page, pageSize), status, customerId, cancellationToken)))
        .WithName("ListInvoices");

        group.MapGet("/{id:guid}", async (Guid id, IInvoiceService invoices, CancellationToken cancellationToken) =>
            Results.Ok(await invoices.GetAsync(id, cancellationToken)))
        .WithName("GetInvoice");

        group.MapPost("/{id:guid}/void", async (
            Guid id,
            IInvoiceService invoices,
            CancellationToken cancellationToken,
            string? reason = null) =>
            Results.Ok(await invoices.VoidAsync(id, reason, cancellationToken)))
        .WithName("VoidInvoice")
        .WithSummary("Cancels an unpaid invoice and releases its usage and adjustments for re-billing.");

        group.MapPost("/{id:guid}/collect", async (
            Guid id,
            IPaymentService payments,
            PayFlowMetrics metrics,
            CancellationToken cancellationToken) =>
        {
            var result = await payments.CollectAsync(id, cancellationToken);

            metrics.PaymentsAttempted.Add(
                1,
                new KeyValuePair<string, object?>("outcome", result.Succeeded ? "succeeded" : "failed"),
                new KeyValuePair<string, object?>("attempt", result.Payment.AttemptNumber));

            if (result.Succeeded)
            {
                metrics.AmountCollected.Add(
                    (double)result.Payment.Amount,
                    new KeyValuePair<string, object?>("currency", result.Payment.Currency));
            }

            if (result.DunningExhausted)
            {
                metrics.InvoicesWrittenOff.Add(1);
            }

            // A decline is a valid, fully-processed outcome, not a request error, so it
            // returns 200 with the outcome in the body. 402 is reserved for the caller
            // needing to act, which is not what a dunning retry represents.
            return Results.Ok(result);
        })
        .WithName("CollectInvoice")
        .WithSummary("Attempts to collect an invoice through the payment gateway.");

        return app;
    }
}

public static class PaymentEndpoints
{
    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/v1/payments").WithTags("Payments");

        group.MapGet("/", async (
            IPaymentService payments,
            CancellationToken cancellationToken,
            int? page = null,
            int? pageSize = null,
            Guid? invoiceId = null,
            PaymentStatus? status = null) =>
            Results.Ok(await payments.ListAsync(
                PageRequest.Of(page, pageSize), invoiceId, status, cancellationToken)))
        .WithName("ListPayments")
        .WithSummary("Lists collection attempts, including failures.");

        group.MapPost("/{id:guid}/refund", async (
            Guid id,
            RefundPaymentRequest request,
            IPaymentService payments,
            CancellationToken cancellationToken) =>
            Results.Ok(await payments.RefundAsync(id, request, cancellationToken)))
        .WithName("RefundPayment")
        .WithSummary("Refunds a captured payment in part or in full, reopening the invoice.");

        return app;
    }
}

public static class OperationsEndpoints
{
    public static IEndpointRouteBuilder MapOperationsEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/v1/billing").WithTags("Billing operations");

        group.MapPost("/run-cycle", async (
            IBillingCycleService cycle,
            PayFlowMetrics metrics,
            CancellationToken cancellationToken) =>
        {
            var stopwatch = Stopwatch.StartNew();
            var report = await cycle.RunAsync(cancellationToken);
            metrics.BillingCycleDuration.Record(
                stopwatch.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("trigger", "api"));

            metrics.SubscriptionsRenewed.Add(report.SubscriptionsRenewed);
            metrics.InvoicesIssued.Add(report.InvoicesIssued, new KeyValuePair<string, object?>("source", "cycle"));

            return Results.Ok(report);
        })
        .WithName("RunBillingCycle")
        .WithSummary("Runs one billing cycle immediately. The worker does this on a schedule.");

        group.MapPost("/run-dunning", async (IBillingCycleService cycle, CancellationToken cancellationToken) =>
            Results.Ok(await cycle.RunDunningAsync(cancellationToken)))
        .WithName("RunDunning")
        .WithSummary("Retries every invoice whose dunning schedule has come due.");

        return app;
    }
}

public static class AnalyticsEndpoints
{
    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/v1/analytics").WithTags("Analytics");

        group.MapGet("/mrr", async (IAnalyticsService analytics, CancellationToken cancellationToken) =>
            Results.Ok(await analytics.GetMrrSnapshotAsync(cancellationToken)))
        .WithName("GetMrr")
        .WithSummary("Monthly recurring revenue, split by plan. Trials are excluded.");

        group.MapGet("/revenue", async (
            IAnalyticsService analytics,
            CancellationToken cancellationToken,
            int months = 12) =>
            Results.Ok(await analytics.GetRevenueByMonthAsync(months, cancellationToken)))
        .WithName("GetRevenueByMonth");

        group.MapGet("/dunning", async (IAnalyticsService analytics, CancellationToken cancellationToken) =>
            Results.Ok(await analytics.GetDunningFunnelAsync(cancellationToken)))
        .WithName("GetDunningFunnel")
        .WithSummary("Recovery rate by dunning attempt number.");

        group.MapGet("/aging", async (IAnalyticsService analytics, CancellationToken cancellationToken) =>
            Results.Ok(await analytics.GetInvoiceAgingAsync(cancellationToken)))
        .WithName("GetInvoiceAging");

        group.MapGet("/declines", async (IAnalyticsService analytics, CancellationToken cancellationToken) =>
            Results.Ok(await analytics.GetDeclineReasonsAsync(cancellationToken)))
        .WithName("GetDeclineReasons");

        return app;
    }
}
