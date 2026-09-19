using Microsoft.EntityFrameworkCore;
using PayFlow.Application.Abstractions;
using PayFlow.Domain.Customers;

namespace PayFlow.Application.Customers;

public sealed record CustomerDto(
    Guid Id,
    string Email,
    string DisplayName,
    string Currency,
    string? ExternalReference,
    bool HasPaymentMethod,
    bool IsArchived,
    DateTimeOffset CreatedAt);

public sealed record CreateCustomerRequest(
    string Email,
    string DisplayName,
    string? Currency = null,
    string? ExternalReference = null,
    string? PaymentMethodToken = null);

public sealed record UpdateCustomerRequest(string? Email, string? DisplayName);

public interface ICustomerService
{
    Task<CustomerDto> CreateAsync(CreateCustomerRequest request, CancellationToken cancellationToken = default);

    Task<CustomerDto> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<PagedResult<CustomerDto>> ListAsync(
        PageRequest page,
        string? search = null,
        bool includeArchived = false,
        CancellationToken cancellationToken = default);

    Task<CustomerDto> UpdateAsync(Guid id, UpdateCustomerRequest request, CancellationToken cancellationToken = default);

    Task<CustomerDto> AttachPaymentMethodAsync(Guid id, string token, CancellationToken cancellationToken = default);

    Task<CustomerDto> ArchiveAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class CustomerService(IPayFlowDbContext db, ITenantContext tenant) : ICustomerService
{
    public async Task<CustomerDto> CreateAsync(CreateCustomerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var email = request.Email?.Trim().ToLowerInvariant() ?? "";
        if (await db.Customers.AnyAsync(x => x.Email == email, cancellationToken))
        {
            throw new Domain.Common.DomainException($"A customer with email '{email}' already exists.");
        }

        var customer = new Customer(
            tenant.TenantId,
            request.Email ?? "",
            request.DisplayName ?? "",
            request.Currency ?? Domain.Common.Money.DefaultCurrency,
            request.ExternalReference);

        if (!string.IsNullOrWhiteSpace(request.PaymentMethodToken))
        {
            customer.AttachPaymentMethod(request.PaymentMethodToken);
        }

        db.Customers.Add(customer);
        await db.SaveChangesAsync(cancellationToken);
        return Map(customer);
    }

    public async Task<CustomerDto> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        Map(await FindAsync(id, cancellationToken));

    public async Task<PagedResult<CustomerDto>> ListAsync(
        PageRequest page,
        string? search = null,
        bool includeArchived = false,
        CancellationToken cancellationToken = default)
    {
        var query = db.Customers.AsNoTracking().AsQueryable();

        if (!includeArchived)
        {
            query = query.Where(x => !x.IsArchived);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(x => x.Email.Contains(term) || x.DisplayName.ToLower().Contains(term));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(x => x.DisplayName)
            .ThenBy(x => x.Id)
            .Skip(page.Skip)
            .Take(page.Take)
            .ToListAsync(cancellationToken);

        return new PagedResult<CustomerDto>([.. items.Select(Map)], total, page.Page, page.Take);
    }

    public async Task<CustomerDto> UpdateAsync(Guid id, UpdateCustomerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var customer = await FindAsync(id, cancellationToken);

        if (!string.IsNullOrWhiteSpace(request.Email))
        {
            customer.ChangeEmail(request.Email);
        }

        if (!string.IsNullOrWhiteSpace(request.DisplayName))
        {
            customer.Rename(request.DisplayName);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Map(customer);
    }

    public async Task<CustomerDto> AttachPaymentMethodAsync(Guid id, string token, CancellationToken cancellationToken = default)
    {
        var customer = await FindAsync(id, cancellationToken);
        customer.AttachPaymentMethod(token);
        await db.SaveChangesAsync(cancellationToken);
        return Map(customer);
    }

    public async Task<CustomerDto> ArchiveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var customer = await FindAsync(id, cancellationToken);
        customer.Archive();
        await db.SaveChangesAsync(cancellationToken);
        return Map(customer);
    }

    internal static CustomerDto Map(Customer customer) => new(
        customer.Id,
        customer.Email,
        customer.DisplayName,
        customer.Currency,
        customer.ExternalReference,
        customer.HasPaymentMethod,
        customer.IsArchived,
        customer.CreatedAt);

    private async Task<Customer> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Customers.FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
        ?? throw new NotFoundException(nameof(Customer), id);
}
