using System.Security.Cryptography;
using System.Text;
using PayFlow.Domain.Common;

namespace PayFlow.Web.Authentication;

/// <summary>One tenant and the key that authenticates as it.</summary>
public sealed class TenantApiKeyOptions
{
    /// <summary>Tenant id, as a GUID.</summary>
    public string TenantId { get; set; } = "";

    /// <summary>Display name, used in logs and on the dashboard.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// The bearer key. In configuration for this project; in production this belongs in a
    /// secret store, and only a hash of it should ever be at rest.
    /// </summary>
    public string ApiKey { get; set; } = "";
}

public sealed class ApiKeyOptions
{
    public const string SectionName = "Authentication";

    public IList<TenantApiKeyOptions> Tenants { get; } = [];

    /// <summary>
    /// When true, requests with no key are accepted as <see cref="DevelopmentTenantId"/>.
    /// Intended for local runs and refused outside the Development environment.
    /// </summary>
    public bool AllowAnonymousDevelopmentTenant { get; set; }

    /// <summary>The tenant anonymous development requests are attributed to.</summary>
    public static Guid DevelopmentTenantId { get; } = Guid.Parse("11111111-1111-1111-1111-111111111111");
}

/// <summary>
/// Resolves the caller's tenant from an API key.
/// <para>
/// The original service trusted an <c>X-Tenant-Id</c> header and fell back to a hard-coded
/// tenant when it was absent or unparseable, which meant any caller could read any
/// tenant's billing data by guessing a GUID. Here the tenant is derived from a secret the
/// caller proves it holds, and an unauthenticated request resolves to no tenant at all -
/// at which point every query filter has nothing to match and the request is rejected.
/// </para>
/// </summary>
public sealed class ApiKeyRegistry
{
    /// <summary>Keyed by the SHA-256 of the API key, so the plaintext is not held in a lookup structure.</summary>
    private readonly Dictionary<string, (TenantId TenantId, string Name)> _byKeyHash;

    public ApiKeyRegistry(ApiKeyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _byKeyHash = new Dictionary<string, (TenantId, string)>(StringComparer.Ordinal);

        foreach (var tenant in options.Tenants)
        {
            if (string.IsNullOrWhiteSpace(tenant.ApiKey) || string.IsNullOrWhiteSpace(tenant.TenantId))
            {
                continue;
            }

            if (!TenantId.TryParse(tenant.TenantId, out var tenantId))
            {
                throw new InvalidOperationException(
                    $"Configured tenant '{tenant.Name}' has an invalid TenantId '{tenant.TenantId}'.");
            }

            _byKeyHash[Hash(tenant.ApiKey)] = (tenantId, tenant.Name);
        }

        AllowAnonymousDevelopmentTenant = options.AllowAnonymousDevelopmentTenant;
    }

    public bool AllowAnonymousDevelopmentTenant { get; }

    public int ConfiguredTenantCount => _byKeyHash.Count;

    /// <summary>
    /// Resolves a presented key. Comparison is over the SHA-256 digest with a
    /// fixed-time equality check, so the time taken to reject a key does not reveal how
    /// much of it was correct.
    /// </summary>
    public bool TryResolve(string? presentedKey, out TenantId tenantId, out string tenantName)
    {
        tenantId = default;
        tenantName = "";

        if (string.IsNullOrWhiteSpace(presentedKey))
        {
            return false;
        }

        var presentedHash = Hash(presentedKey);

        foreach (var (storedHash, tenant) in _byKeyHash)
        {
            if (CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(storedHash),
                Encoding.UTF8.GetBytes(presentedHash)))
            {
                tenantId = tenant.TenantId;
                tenantName = tenant.Name;
                return true;
            }
        }

        return false;
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
