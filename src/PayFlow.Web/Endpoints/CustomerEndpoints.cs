using PayFlow.Application.Abstractions;
using PayFlow.Application.Customers;

namespace PayFlow.Web.Endpoints;

public static class CustomerEndpoints
{
    public static IEndpointRouteBuilder MapCustomerEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/v1/customers").WithTags("Customers");

        group.MapPost("/", async (
            CreateCustomerRequest request,
            ICustomerService customers,
            CancellationToken cancellationToken) =>
        {
            var created = await customers.CreateAsync(request, cancellationToken);
            return Results.Created($"/v1/customers/{created.Id}", created);
        })
        .WithName("CreateCustomer")
        .WithSummary("Creates a customer.");

        group.MapGet("/", async (
            ICustomerService customers,
            CancellationToken cancellationToken,
            int? page = null,
            int? pageSize = null,
            string? search = null,
            bool includeArchived = false) =>
            Results.Ok(await customers.ListAsync(
                PageRequest.Of(page, pageSize), search, includeArchived, cancellationToken)))
        .WithName("ListCustomers")
        .WithSummary("Lists customers in this tenant.");

        group.MapGet("/{id:guid}", async (
            Guid id,
            ICustomerService customers,
            CancellationToken cancellationToken) =>
            Results.Ok(await customers.GetAsync(id, cancellationToken)))
        .WithName("GetCustomer");

        group.MapPatch("/{id:guid}", async (
            Guid id,
            UpdateCustomerRequest request,
            ICustomerService customers,
            CancellationToken cancellationToken) =>
            Results.Ok(await customers.UpdateAsync(id, request, cancellationToken)))
        .WithName("UpdateCustomer");

        group.MapPut("/{id:guid}/payment-method", async (
            Guid id,
            AttachPaymentMethodRequest request,
            ICustomerService customers,
            CancellationToken cancellationToken) =>
            Results.Ok(await customers.AttachPaymentMethodAsync(id, request.Token, cancellationToken)))
        .WithName("AttachPaymentMethod")
        .WithSummary("Stores the gateway token used to charge this customer.");

        group.MapDelete("/{id:guid}", async (
            Guid id,
            ICustomerService customers,
            CancellationToken cancellationToken) =>
            Results.Ok(await customers.ArchiveAsync(id, cancellationToken)))
        .WithName("ArchiveCustomer")
        .WithSummary("Archives a customer. Invoices and payments are retained.");

        return app;
    }
}

/// <param name="Token">Opaque gateway token. PayFlow never receives card details.</param>
public sealed record AttachPaymentMethodRequest(string Token);
