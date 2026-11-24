using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SosaFam.Api.Tenancy;

/// <summary>
/// Copies the request's tenant into SQL Server SESSION_CONTEXT whenever EF opens a
/// connection. Keys are set read-only, so nothing later on the same connection can change
/// them; pooled connections are reset before reuse, which clears them.
/// </summary>
public class SessionContextInterceptor(TenantContext tenant) : DbConnectionInterceptor
{
    private const string Sql =
        "EXEC sp_set_session_context @key = N'UserId', @value = @userId, @read_only = 1; " +
        "EXEC sp_set_session_context @key = N'HouseholdId', @value = @householdId, @read_only = 1; " +
        "EXEC sp_set_session_context @key = N'IsSystem', @value = @isSystem, @read_only = 1;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var cmd = BuildCommand(connection);
        cmd.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken ct = default)
    {
        await using var cmd = BuildCommand(connection);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private DbCommand BuildCommand(DbConnection connection)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = Sql;
        cmd.Parameters.Add(new SqlParameter("@userId", SqlDbType.UniqueIdentifier) { Value = (object?)tenant.UserId ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@householdId", SqlDbType.UniqueIdentifier) { Value = (object?)tenant.HouseholdId ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@isSystem", SqlDbType.Bit) { Value = tenant.IsSystem ? true : DBNull.Value });
        return cmd;
    }
}
