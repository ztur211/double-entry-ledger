// Ledger class for health check
using Npgsql;
internal static class Ledger
{
    public static async Task<bool> PingAsync(NpgsqlDataSource db)
    {
        try
        {
            await using var cmd = db.CreateCommand("SELECT 1;");
            await cmd.ExecuteScalarAsync();
            return true;
        }
        catch (NpgsqlException)
        {
            return false;
        }
    }
}
