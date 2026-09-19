using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using PayFlow.Application.Abstractions;
using PayFlow.Domain.Common;

namespace PayFlow.Web.Diagnostics;

/// <summary>
/// Turns domain failures into RFC 9457 problem responses.
/// <para>
/// Three cases, deliberately distinguished, because a client can act on the difference:
/// 404 means the thing is not there, 400 means the request was malformed, and 409 means
/// the request was well-formed but the current state forbids it - retrying a 409 after
/// the state changes can succeed, retrying a 400 never will.
/// </para>
/// <para>
/// Anything that is not a domain exception is an unhandled bug, and its message is not
/// echoed to the caller: internal messages leak schema names, connection strings and
/// stack shapes.
/// </para>
/// </summary>
public sealed class DomainExceptionHandler(ILogger<DomainExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var problem = exception switch
        {
            NotFoundException notFound => new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Not found",
                Detail = notFound.Message,
                Type = "https://payflow.dev/problems/not-found",
            },

            IdempotencyConflictException conflict => new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Idempotency key reused",
                Detail = conflict.Message,
                Type = "https://payflow.dev/problems/idempotency-conflict",
            },

            DomainValidationException validation => new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid request",
                Detail = validation.Message,
                Type = "https://payflow.dev/problems/validation",
            },

            DomainException domain => new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Operation not allowed in the current state",
                Detail = domain.Message,
                Type = "https://payflow.dev/problems/conflict",
            },

            InvalidOperationException invalid when invalid.Message.Contains("tenant", StringComparison.OrdinalIgnoreCase)
                => new ProblemDetails
                {
                    Status = StatusCodes.Status401Unauthorized,
                    Title = "Unauthenticated",
                    Detail = "No tenant was resolved for this request.",
                    Type = "https://payflow.dev/problems/unauthenticated",
                },

            _ => null,
        };

        if (problem is null)
        {
            logger.LogError(exception, "Unhandled exception while processing {Path}", httpContext.Request.Path);

            problem = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "Internal server error",
                Detail = "The request could not be completed. The failure has been logged.",
                Type = "https://payflow.dev/problems/internal",
            };
        }
        else
        {
            logger.LogInformation(
                "Rejected {Path} with {Status}: {Detail}",
                httpContext.Request.Path,
                problem.Status,
                problem.Detail);
        }

        // Correlating a reported failure with the logs is the first thing anyone needs.
        problem.Extensions["traceId"] = httpContext.TraceIdentifier;
        problem.Instance = httpContext.Request.Path;

        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
        return true;
    }
}
