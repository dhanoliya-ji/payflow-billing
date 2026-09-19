using PayFlow.Application.Abstractions;
using PayFlow.Domain.Common;

namespace PayFlow.Web.Authentication;

/// <summary>
/// Authenticates the request against the API key registry and pins the resolved tenant
/// onto the request scope, where the DbContext's query filters pick it up.
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next, ILogger<TenantResolutionMiddleware> logger)
{
    public const string ApiKeyHeader = "X-Api-Key";

    /// <summary>Paths served before a tenant exists: health, metrics and the OpenAPI document.</summary>
    private static readonly string[] AnonymousPaths =
    [
        "/health",
        "/metrics",
        "/openapi",
        "/favicon.ico",
    ];

    public async Task InvokeAsync(HttpContext context, ApiKeyRegistry registry, ITenantContextSetter tenantSetter)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(tenantSetter);

        var path = context.Request.Path.Value ?? "";

        if (AnonymousPaths.Any(x => path.StartsWith(x, StringComparison.OrdinalIgnoreCase)))
        {
            await next(context);
            return;
        }

        var presentedKey = ExtractKey(context.Request);

        if (registry.TryResolve(presentedKey, out var tenantId, out var tenantName))
        {
            tenantSetter.SetTenant(tenantId);
            context.Items["TenantName"] = tenantName;
            await next(context);
            return;
        }

        if (string.IsNullOrWhiteSpace(presentedKey) && registry.AllowAnonymousDevelopmentTenant)
        {
            tenantSetter.SetTenant(new TenantId(ApiKeyOptions.DevelopmentTenantId));
            context.Items["TenantName"] = "Development";
            await next(context);
            return;
        }

        // Deliberately identical for "no key" and "wrong key": distinguishing them tells
        // an attacker whether a guessed key is a real one for some other tenant.
        logger.LogWarning(
            "Rejected unauthenticated request to {Path} from {RemoteIp}",
            path,
            context.Connection.RemoteIpAddress);

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = $"ApiKey realm=\"payflow\", header=\"{ApiKeyHeader}\"";

        await context.Response.WriteAsJsonAsync(new
        {
            type = "https://payflow.dev/problems/unauthenticated",
            title = "Unauthenticated",
            status = StatusCodes.Status401Unauthorized,
            detail = $"Supply a tenant API key in the {ApiKeyHeader} header or as a Bearer token.",
        });
    }

    /// <summary>Accepts the key in <c>X-Api-Key</c> or as a bearer token.</summary>
    private static string? ExtractKey(HttpRequest request)
    {
        if (request.Headers.TryGetValue(ApiKeyHeader, out var header) && header.Count > 0)
        {
            return header[0];
        }

        var authorization = request.Headers.Authorization.ToString();
        return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization["Bearer ".Length..].Trim()
            : null;
    }
}
