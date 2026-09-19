using PayFlow.Domain.Common;

namespace PayFlow.Infrastructure.Idempotency;

/// <summary>
/// A claimed idempotency key and, once the work finished, the response to replay.
/// <para>
/// Keeping the request fingerprint alongside the key is what distinguishes a genuine
/// retry from a client bug. Same key, same body: replay the stored response. Same key,
/// different body: reject, because the client is about to be told that an operation it
/// never asked for succeeded.
/// </para>
/// </summary>
public sealed class IdempotencyRecord
{
    private IdempotencyRecord()
    {
    }

    public IdempotencyRecord(TenantId tenantId, string key, string requestFingerprint, DateTimeOffset claimedAt)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        Key = key;
        RequestFingerprint = requestFingerprint;
        ClaimedAt = claimedAt;
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; private set; }

    /// <summary>The caller-supplied key, unique per tenant.</summary>
    public string Key { get; private set; } = "";

    /// <summary>Hash of the request body, so key reuse with different content is detectable.</summary>
    public string RequestFingerprint { get; private set; } = "";

    public DateTimeOffset ClaimedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Serialised response replayed to a retry. Null while the work is still in flight.</summary>
    public string? ResponsePayload { get; private set; }

    public bool IsCompleted => CompletedAt is not null;

    public void Complete(string responsePayload, DateTimeOffset at)
    {
        ResponsePayload = responsePayload;
        CompletedAt = at;
    }
}
