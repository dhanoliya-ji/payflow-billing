using PayFlow.Application.Abstractions;
using PayFlow.Application.Plans;
using PayFlow.Application.Subscriptions;
using PayFlow.Application.Usage;
using PayFlow.Domain.Subscriptions;

namespace PayFlow.Web.Endpoints;

public static class PlanEndpoints
{
    public static IEndpointRouteBuilder MapPlanEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/v1/plans").WithTags("Plans");

        group.MapPost("/", async (CreatePlanRequest request, IPlanService plans, CancellationToken cancellationToken) =>
        {
            var created = await plans.CreateAsync(request, cancellationToken);
            return Results.Created($"/v1/plans/{created.Id}", created);
        })
        .WithName("CreatePlan")
        .WithSummary("Creates a plan, optionally with metered rates.");

        group.MapGet("/", async (IPlanService plans, CancellationToken cancellationToken, bool includeArchived = false) =>
            Results.Ok(await plans.ListAsync(includeArchived, cancellationToken)))
        .WithName("ListPlans");

        group.MapGet("/{id:guid}", async (Guid id, IPlanService plans, CancellationToken cancellationToken) =>
            Results.Ok(await plans.GetAsync(id, cancellationToken)))
        .WithName("GetPlan");

        group.MapDelete("/{id:guid}", async (Guid id, IPlanService plans, CancellationToken cancellationToken) =>
            Results.Ok(await plans.ArchiveAsync(id, cancellationToken)))
        .WithName("ArchivePlan")
        .WithSummary("Retires a plan from the catalogue. Existing subscriptions keep billing against it.");

        return app;
    }
}

public static class SubscriptionEndpoints
{
    public static IEndpointRouteBuilder MapSubscriptionEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/v1/subscriptions").WithTags("Subscriptions");

        group.MapPost("/", async (
            CreateSubscriptionRequest request,
            ISubscriptionService subscriptions,
            CancellationToken cancellationToken) =>
        {
            var created = await subscriptions.CreateAsync(request, cancellationToken);
            return Results.Created($"/v1/subscriptions/{created.Id}", created);
        })
        .WithName("CreateSubscription");

        group.MapGet("/", async (
            ISubscriptionService subscriptions,
            CancellationToken cancellationToken,
            int? page = null,
            int? pageSize = null,
            SubscriptionStatus? status = null,
            Guid? customerId = null) =>
            Results.Ok(await subscriptions.ListAsync(
                PageRequest.Of(page, pageSize), status, customerId, cancellationToken)))
        .WithName("ListSubscriptions");

        group.MapGet("/{id:guid}", async (
            Guid id,
            ISubscriptionService subscriptions,
            CancellationToken cancellationToken) =>
            Results.Ok(await subscriptions.GetAsync(id, cancellationToken)))
        .WithName("GetSubscription");

        group.MapPost("/{id:guid}/plan", async (
            Guid id,
            ChangePlanRequest request,
            ISubscriptionService subscriptions,
            CancellationToken cancellationToken) =>
            Results.Ok(await subscriptions.ChangePlanAsync(id, request, cancellationToken)))
        .WithName("ChangeSubscriptionPlan")
        .WithSummary("Moves a subscription to another plan, prorating the rest of the period.");

        group.MapPost("/{id:guid}/quantity", async (
            Guid id,
            ChangeQuantityRequest request,
            ISubscriptionService subscriptions,
            CancellationToken cancellationToken) =>
            Results.Ok(await subscriptions.ChangeQuantityAsync(id, request, cancellationToken)))
        .WithName("ChangeSubscriptionQuantity")
        .WithSummary("Changes the seat count on a per-seat plan, prorating the difference.");

        group.MapPost("/{id:guid}/pause", async (
            Guid id,
            ISubscriptionService subscriptions,
            CancellationToken cancellationToken) =>
            Results.Ok(await subscriptions.PauseAsync(id, cancellationToken)))
        .WithName("PauseSubscription");

        group.MapPost("/{id:guid}/resume", async (
            Guid id,
            ISubscriptionService subscriptions,
            CancellationToken cancellationToken) =>
            Results.Ok(await subscriptions.ResumeAsync(id, cancellationToken)))
        .WithName("ResumeSubscription")
        .WithSummary("Restarts a paused subscription, or withdraws a pending cancellation.");

        group.MapDelete("/{id:guid}", async (
            Guid id,
            ISubscriptionService subscriptions,
            CancellationToken cancellationToken,
            bool immediately = false,
            string? reason = null) =>
            Results.Ok(await subscriptions.CancelAsync(
                id, new CancelSubscriptionRequest(immediately, reason), cancellationToken)))
        .WithName("CancelSubscription")
        .WithSummary("Cancels at the end of the paid period, or immediately with ?immediately=true.");

        group.MapGet("/{id:guid}/usage", async (
            Guid id,
            IUsageService usage,
            CancellationToken cancellationToken) =>
            Results.Ok(await usage.SummarizeCurrentPeriodAsync(id, cancellationToken)))
        .WithName("GetCurrentPeriodUsage")
        .WithSummary("Current-period usage with what it would cost if invoiced now.");

        return app;
    }
}

public static class UsageEndpoints
{
    public static IEndpointRouteBuilder MapUsageEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/v1/usage").WithTags("Usage");

        group.MapPost("/", async (
            RecordUsageRequest request,
            IUsageService usage,
            CancellationToken cancellationToken) =>
        {
            var recorded = await usage.RecordAsync(request, cancellationToken);
            return Results.Created($"/v1/usage/{recorded.Id}", recorded);
        })
        .WithName("RecordUsage")
        .WithSummary("Records metered usage. Supply an idempotencyKey so retries do not double-bill.");

        group.MapGet("/", async (
            IUsageService usage,
            CancellationToken cancellationToken,
            int? page = null,
            int? pageSize = null,
            Guid? subscriptionId = null,
            string? metric = null,
            bool? invoiced = null) =>
            Results.Ok(await usage.ListAsync(
                PageRequest.Of(page, pageSize), subscriptionId, metric, invoiced, cancellationToken)))
        .WithName("ListUsage");

        return app;
    }
}
