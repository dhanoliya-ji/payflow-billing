using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PayFlow.Application.Abstractions;
using PayFlow.Domain.Common;

namespace PayFlow.Infrastructure.Persistence;

/// <summary>
/// Lets <c>dotnet ef</c> build the model without starting the web host.
/// <para>
/// Without this, adding a migration would boot the whole application - Redis
/// connection, hosted services, the lot - just to read the schema. It also means
/// migrations can be generated from the infrastructure project alone, which is what the
/// CI schema check does.
/// </para>
/// <para>
/// Point it at a different database with <c>PAYFLOW_DESIGN_CONNECTION</c>.
/// </para>
/// </summary>
public sealed class PayFlowDbContextFactory : IDesignTimeDbContextFactory<PayFlowDbContext>
{
    public PayFlowDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("PAYFLOW_DESIGN_CONNECTION")
            ?? DependencyInjection.DefaultPostgresConnectionString;

        var options = new DbContextOptionsBuilder<PayFlowDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new PayFlowDbContext(options, new DesignTimeTenantContext());
    }

    /// <summary>
    /// A fixed tenant used only while building the model. Query filters reference the
    /// tenant context, so one must exist, but no query is ever executed at design time.
    /// </summary>
    private sealed class DesignTimeTenantContext : ITenantContext
    {
        public TenantId TenantId { get; } = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

        public bool IsResolved => true;
    }
}
