using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PayFlow.Application.Abstractions;
using PayFlow.Domain.Common;
using PayFlow.Infrastructure.Persistence;
using Xunit;

namespace PayFlow.Integration.Tests;

/// <summary>
/// Creates the integration test database once per run and applies the real migrations
/// to it.
/// <para>
/// These tests run against PostgreSQL rather than an in-memory provider on purpose.
/// The first attempt used SQLite and it could not translate the billing cycle's central
/// query - <c>where CurrentPeriod.End &lt;= now</c>, a comparison against a member of a
/// complex type - which Npgsql translates without trouble. A test suite that cannot
/// execute the one query the whole billing cycle depends on is not testing the billing
/// cycle. Running the migrations here also means the migration SQL itself is covered.
/// </para>
/// <para>
/// Tests share the database and isolate themselves by tenant, which is exactly the
/// isolation the product claims to provide - so the suite exercises it continuously
/// rather than only in the test that asserts it.
/// </para>
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string DatabaseName = "payflow_integration_tests";

    /// <summary>Points at the docker-compose Postgres by default; override in CI.</summary>
    private static string AdminConnectionString =>
        Environment.GetEnvironmentVariable("PAYFLOW_TEST_POSTGRES")
        ?? "Host=localhost;Port=55433;Database=postgres;Username=payflow;Password=payflow";

    /// <summary>
    /// The database is created and migrated exactly once per test process. Both the
    /// service-level harness and the API factory need it, and a second instance dropping
    /// and rebuilding the schema mid-run would pull the database out from under whatever
    /// else is using it.
    /// </summary>
    private static readonly SemaphoreSlim InitializationGate = new(1, 1);
    private static string? _connectionString;

    public string ConnectionString => _connectionString
        ?? throw new InvalidOperationException("The Postgres fixture has not been initialized.");

    public async Task InitializeAsync()
    {
        await InitializationGate.WaitAsync();
        try
        {
            if (_connectionString is null)
            {
                await SetUpAsync();
            }
        }
        finally
        {
            InitializationGate.Release();
        }
    }

    private static async Task SetUpAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(AdminConnectionString);
        var adminDatabase = builder.Database;
        builder.Database = DatabaseName;
        var connectionString = builder.ConnectionString;

        try
        {
            await CreateDatabaseIfMissingAsync(adminDatabase);
        }
        catch (NpgsqlException error)
        {
            throw new InvalidOperationException(
                "The integration tests need a PostgreSQL instance. Start one with " +
                "`docker compose up -d postgres`, or point PAYFLOW_TEST_POSTGRES at another server. " +
                $"Tried: {AdminConnectionString}",
                error);
        }

        var options = new DbContextOptionsBuilder<PayFlowDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new PayFlowDbContext(options, new FixedTenantContext(TenantId.New()));

        // A previous run's schema may be stale, and these tests own this database
        // outright, so it is rebuilt from the migrations every run.
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();

        _connectionString = connectionString;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task CreateDatabaseIfMissingAsync(string? adminDatabase)
    {
        var builder = new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = adminDatabase };

        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();

        await using var exists = new NpgsqlCommand(
            "select 1 from pg_database where datname = @name", connection);
        exists.Parameters.AddWithValue("name", DatabaseName);

        if (await exists.ExecuteScalarAsync() is not null)
        {
            return;
        }

        // Database names cannot be parameterised; the name is a compile-time constant.
        await using var create = new NpgsqlCommand($"create database \"{DatabaseName}\"", connection);
        await create.ExecuteNonQueryAsync();
    }

    private sealed class FixedTenantContext(TenantId tenantId) : ITenantContext
    {
        public TenantId TenantId { get; } = tenantId;

        public bool IsResolved => true;
    }
}

/// <summary>
/// Puts every integration test class in one collection, so they share the database and
/// run one at a time. Tenant isolation makes concurrent runs safe in principle, but
/// serialising removes any doubt about a shared schema being rebuilt underneath a test.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
