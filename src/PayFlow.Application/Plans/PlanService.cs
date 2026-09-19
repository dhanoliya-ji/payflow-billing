using Microsoft.EntityFrameworkCore;
using PayFlow.Application.Abstractions;
using PayFlow.Domain.Common;
using PayFlow.Domain.Plans;

namespace PayFlow.Application.Plans;

public sealed record MeteredRateDto(string Metric, decimal UnitPrice, decimal IncludedQuantity);

public sealed record PlanDto(
    Guid Id,
    string Code,
    string Name,
    decimal Amount,
    string Currency,
    BillingInterval Interval,
    PricingModel PricingModel,
    int TrialDays,
    bool IsActive,
    decimal MonthlyRecurringValue,
    IReadOnlyList<MeteredRateDto> MeteredRates);

public sealed record CreateMeteredRateRequest(string Metric, decimal UnitPrice, decimal IncludedQuantity = 0m);

public sealed record CreatePlanRequest(
    string Code,
    string Name,
    decimal Amount,
    string Interval,
    string? Currency = null,
    string? PricingModel = null,
    int TrialDays = 0,
    IReadOnlyList<CreateMeteredRateRequest>? MeteredRates = null);

public interface IPlanService
{
    Task<PlanDto> CreateAsync(CreatePlanRequest request, CancellationToken cancellationToken = default);

    Task<PlanDto> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PlanDto>> ListAsync(bool includeArchived = false, CancellationToken cancellationToken = default);

    Task<PlanDto> ArchiveAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class PlanService(IPayFlowDbContext db, ITenantContext tenant, IClock clock) : IPlanService
{
    public async Task<PlanDto> CreateAsync(CreatePlanRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var interval = ParseEnum<BillingInterval>(request.Interval, nameof(request.Interval));
        var pricingModel = string.IsNullOrWhiteSpace(request.PricingModel)
            ? PricingModel.FlatRate
            : ParseEnum<PricingModel>(request.PricingModel, nameof(request.PricingModel));

        var code = request.Code?.Trim().ToUpperInvariant() ?? "";
        if (await db.Plans.AnyAsync(x => x.Code == code, cancellationToken))
        {
            throw new DomainException($"Plan code '{code}' is already in use.");
        }

        var plan = new Plan(
            tenant.TenantId,
            request.Code ?? "",
            request.Name ?? "",
            new Money(request.Amount, request.Currency ?? Money.DefaultCurrency),
            interval,
            pricingModel,
            request.TrialDays);

        foreach (var rate in request.MeteredRates ?? [])
        {
            plan.AddMeteredRate(rate.Metric, new Money(rate.UnitPrice, plan.Currency), rate.IncludedQuantity);
        }

        db.Plans.Add(plan);
        await db.SaveChangesAsync(cancellationToken);
        return Map(plan);
    }

    public async Task<PlanDto> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        Map(await FindAsync(id, cancellationToken));

    public async Task<IReadOnlyList<PlanDto>> ListAsync(bool includeArchived = false, CancellationToken cancellationToken = default)
    {
        var query = db.Plans.AsNoTracking().Include(x => x.MeteredRates).AsQueryable();

        if (!includeArchived)
        {
            query = query.Where(x => x.IsActive);
        }

        var plans = await query.OrderBy(x => x.Code).ToListAsync(cancellationToken);
        return [.. plans.Select(Map)];
    }

    public async Task<PlanDto> ArchiveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var plan = await FindAsync(id, cancellationToken);
        plan.Archive(clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return Map(plan);
    }

    internal static PlanDto Map(Plan plan) => new(
        plan.Id,
        plan.Code,
        plan.Name,
        plan.Price.Amount,
        plan.Currency,
        plan.Interval,
        plan.PricingModel,
        plan.TrialDays,
        plan.IsActive,
        plan.MonthlyRecurringValue.Amount,
        [.. plan.MeteredRates.Select(x => new MeteredRateDto(x.Metric, x.UnitPrice.Amount, x.IncludedQuantity))]);

    internal static TEnum ParseEnum<TEnum>(string? value, string parameter)
        where TEnum : struct, Enum
    {
        if (Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
        {
            return parsed;
        }

        var allowed = string.Join(", ", Enum.GetNames<TEnum>());
        throw new DomainValidationException(parameter, $"'{value}' is not valid. Expected one of: {allowed}.");
    }

    private async Task<Plan> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Plans.Include(x => x.MeteredRates).FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
        ?? throw new NotFoundException(nameof(Plan), id);
}
