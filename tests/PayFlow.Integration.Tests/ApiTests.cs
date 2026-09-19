using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace PayFlow.Integration.Tests;

/// <summary>
/// Boots the real web host against the integration test database, so the middleware
/// pipeline, authentication, routing and problem-details mapping are the production ones.
/// </summary>
public sealed class PayFlowApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgresFixture _postgres = new();

    public const string TenantAKey = "test-key-tenant-a";
    public const string TenantBKey = "test-key-tenant-b";
    public const string TenantAId = "aaaaaaaa-0000-0000-0000-00000000000a";
    public const string TenantBId = "bbbbbbbb-0000-0000-0000-00000000000b";

    public Task InitializeAsync() => _postgres.InitializeAsync();

    public new Task DisposeAsync() => base.DisposeAsync().AsTask();

    public HttpClient ClientFor(string? apiKey)
    {
        var client = CreateClient();
        if (apiKey is not null)
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        }

        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Production, not Development: anonymous access to the development tenant must
        // stay off, or these tests would not be exercising authentication at all.
        builder.UseEnvironment(Environments.Production);

        builder.UseSetting("Authentication:Tenants:0:TenantId", TenantAId);
        builder.UseSetting("Authentication:Tenants:0:Name", "Tenant A");
        builder.UseSetting("Authentication:Tenants:0:ApiKey", TenantAKey);
        builder.UseSetting("Authentication:Tenants:1:TenantId", TenantBId);
        builder.UseSetting("Authentication:Tenants:1:Name", "Tenant B");
        builder.UseSetting("Authentication:Tenants:1:ApiKey", TenantBKey);

        // No Redis in tests; the host falls back to its in-process cache.
        builder.UseSetting("ConnectionStrings:Redis", "");

        // The host reads its connection string from configuration, so pointing it at the
        // integration test database needs no service surgery.
        builder.UseSetting("ConnectionStrings:Postgres", _postgres.ConnectionString);

        builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
    }
}

[Collection(PostgresCollection.Name)]
public sealed class ApiTests(PayFlowApiFactory factory) : IClassFixture<PayFlowApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task A_request_with_no_api_key_is_rejected()
    {
        using var client = factory.ClientFor(null);

        var response = await client.GetAsync(new Uri("/v1/customers", UriKind.Relative));

        // The original service fell back to a hard-coded tenant here and served the data.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_request_with_a_wrong_api_key_is_rejected_the_same_way()
    {
        using var client = factory.ClientFor("not-a-real-key");

        var response = await client.GetAsync(new Uri("/v1/customers", UriKind.Relative));

        // Identical to the missing-key case: distinguishing them would confirm to an
        // attacker whether a guessed key belongs to some other tenant.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_bearer_token_is_accepted_as_well_as_the_header()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", PayFlowApiFactory.TenantAKey);

        var response = await client.GetAsync(new Uri("/v1/plans", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_and_metrics_are_reachable_without_a_key()
    {
        using var client = factory.ClientFor(null);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("/health", UriKind.Relative))).StatusCode);

        var metrics = await client.GetAsync(new Uri("/metrics", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, metrics.StatusCode);
        Assert.Equal("text/plain", metrics.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_customer_can_be_created_and_read_back()
    {
        using var client = factory.ClientFor(PayFlowApiFactory.TenantAKey);

        var email = $"{Guid.NewGuid():N}@example.com";
        var create = await client.PostAsJsonAsync(
            new Uri("/v1/customers", UriKind.Relative),
            new { email, displayName = "Created By Test" });

        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var created = await create.Content.ReadFromJsonAsync<JsonElement>(Json);
        var id = created.GetProperty("id").GetGuid();

        var fetched = await client.GetFromJsonAsync<JsonElement>(
            new Uri($"/v1/customers/{id}", UriKind.Relative), Json);

        Assert.Equal(email, fetched.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Invalid_input_is_a_400_problem_document()
    {
        using var client = factory.ClientFor(PayFlowApiFactory.TenantAKey);

        var response = await client.PostAsJsonAsync(
            new Uri("/v1/customers", UriKind.Relative),
            new { email = "not-an-email", displayName = "Nope" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("Invalid request", problem.GetProperty("title").GetString());
        Assert.True(problem.TryGetProperty("traceId", out _), "A problem document should carry a traceId.");
    }

    [Fact]
    public async Task A_missing_record_is_a_404_problem_document()
    {
        using var client = factory.ClientFor(PayFlowApiFactory.TenantAKey);

        var response = await client.GetAsync(new Uri($"/v1/customers/{Guid.NewGuid()}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task One_tenant_s_key_cannot_read_another_tenant_s_record()
    {
        using var clientA = factory.ClientFor(PayFlowApiFactory.TenantAKey);
        using var clientB = factory.ClientFor(PayFlowApiFactory.TenantBKey);

        var create = await clientA.PostAsJsonAsync(
            new Uri("/v1/customers", UriKind.Relative),
            new { email = $"{Guid.NewGuid():N}@tenant-a.example", displayName = "Tenant A Customer" });

        var created = await create.Content.ReadFromJsonAsync<JsonElement>(Json);
        var id = created.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.OK,
            (await clientA.GetAsync(new Uri($"/v1/customers/{id}", UriKind.Relative))).StatusCode);

        // 404, not 403: telling tenant B that the id exists but belongs to someone else
        // leaks the existence of another tenant's records.
        Assert.Equal(HttpStatusCode.NotFound,
            (await clientB.GetAsync(new Uri($"/v1/customers/{id}", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task An_operation_the_current_state_forbids_is_a_409_not_a_400()
    {
        using var client = factory.ClientFor(PayFlowApiFactory.TenantAKey);

        var email = $"{Guid.NewGuid():N}@example.com";
        var body = new { email, displayName = "Duplicate Test" };

        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync(new Uri("/v1/customers", UriKind.Relative), body)).StatusCode);

        var duplicate = await client.PostAsJsonAsync(new Uri("/v1/customers", UriKind.Relative), body);

        // A client can act on the difference: a 409 may succeed later, a 400 never will.
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task The_openapi_document_is_served()
    {
        using var client = factory.ClientFor(null);

        var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var document = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        var paths = document.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/v1/customers", out _));
        Assert.True(paths.TryGetProperty("/v1/subscriptions", out _));
    }

    [Fact]
    public async Task The_dashboard_renders_for_an_authenticated_tenant()
    {
        using var client = factory.ClientFor(PayFlowApiFactory.TenantAKey);

        var response = await client.GetAsync(new Uri("/dashboard", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Monthly recurring revenue", html, StringComparison.Ordinal);
        Assert.Contains("Tenant A", html, StringComparison.Ordinal);
    }
}
