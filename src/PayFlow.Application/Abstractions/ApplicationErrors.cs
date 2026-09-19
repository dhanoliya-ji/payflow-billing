using PayFlow.Domain.Common;

namespace PayFlow.Application.Abstractions;

/// <summary>
/// The referenced entity does not exist, or exists in another tenant. Those two cases
/// are deliberately indistinguishable to the caller: telling a tenant "that id exists
/// but is not yours" leaks the existence of other tenants' records.
/// </summary>
public sealed class NotFoundException(string entity, object id)
    : DomainException($"{entity} '{id}' was not found.")
{
    public string Entity { get; } = entity;

    public object Id { get; } = id;
}

/// <summary>An idempotency key was replayed with a different request body.</summary>
public sealed class IdempotencyConflictException(string key)
    : DomainException($"Idempotency key '{key}' was already used with a different request.")
{
    public string Key { get; } = key;
}

/// <summary>A page of results plus enough metadata for a caller to fetch the rest.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public bool HasNextPage => Page < TotalPages;

    public static PagedResult<T> Empty(int pageSize) => new([], 0, 1, pageSize);
}

/// <summary>Paging request, clamped so a caller cannot ask for a million rows.</summary>
public readonly record struct PageRequest(int Page, int PageSize)
{
    public const int MaxPageSize = 200;
    public const int DefaultPageSize = 50;

    public static PageRequest Of(int? page, int? pageSize) =>
        new(Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize));

    public int Skip => (Math.Max(1, Page) - 1) * Take;

    public int Take => Math.Clamp(PageSize <= 0 ? DefaultPageSize : PageSize, 1, MaxPageSize);
}
