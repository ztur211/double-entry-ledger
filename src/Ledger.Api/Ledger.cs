using Npgsql;
using System.Data;
using NpgsqlTypes;

namespace Ledger.Api;

// Ledger class for health check
internal static class Ledger
{
    // DB health check
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

    // Each statement in schema is IF NOT EXISTS so running on startup is idempotent
    private const string Schema = """
        -- Accounts hold the id, currency, original and cached balance
        CREATE TABLE IF NOT EXISTS accounts (
            id UUID PRIMARY KEY,
            currency TEXT NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
            -- Original balance is the sum of all transactions
            opening_minor bigint NOT NULL CHECK (opening_minor >= 0),
            balance_minor numeric(30, 0) NOT NULL,
            is_system boolean NOT NULL,
            -- Needs to be within 64-bit integer range
            CHECK (is_system OR (balance_minor >= 0 AND balance_minor <= 9223372036854775807))
        );

        -- One account per currency
        CREATE UNIQUE INDEX IF NOT EXISTS unique_account_currency
            ON accounts(currency) WHERE is_system;

        -- Transfers are immutable, only insert into table
        CREATE TABLE IF NOT EXISTS transfers (
            id UUID PRIMARY KEY,
            kind TEXT NOT NULL CHECK (kind IN ('opening', 'transfer', 'reversal')),
            source_id uuid NOT NULL REFERENCES accounts(id),
            destination_id uuid NOT NULL REFERENCES accounts(id),
            amount_minor bigint NOT NULL CHECK (amount_minor > 0),
            currency TEXT NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
            reversal_of uuid REFERENCES transfers(id),
            -- Transfer cannot move money from an account to the same account
            CHECK (source_id <> destination_id),
            -- reversal_of must be present if kind is reversal
            CHECK ((kind = 'reversal') = (reversal_of IS NOT NULL))
        );

        -- Postings are the double entry, amounts sum to zero, transfers write two rows
        CREATE TABLE IF NOT EXISTS postings (
            sequence bigint GENERATED ALWAYS AS IDENTITY UNIQUE,
            id UUID PRIMARY KEY,
            transfer_id UUID NOT NULL REFERENCES transfers(id),
            account_id UUID NOT NULL REFERENCES accounts(id),
            amount_minor bigint NOT NULL CHECK (amount_minor <> 0),
            UNIQUE (transfer_id, account_id)
        );

        -- Transfer can only be reversed once
        CREATE UNIQUE INDEX IF NOT EXISTS uq_transfers_reversal_of
            ON transfers(reversal_of)
            WHERE reversal_of IS NOT NULL;

        -- Create index for faster queries
        CREATE INDEX IF NOT EXISTS postings_account_sequence_idx ON postings(account_id, sequence);
    """;

    // Initialize the database schema
    private const int SchemaSetupId = 781346210;
    public static async Task EnsureSchemaAsync(NpgsqlDataSource db)
    {
        await using var connection = await db.OpenConnectionAsync();
        await using var lockCmd = connection.CreateCommand();
        lockCmd.CommandText = $"SELECT pg_advisory_lock({SchemaSetupId});";
        await lockCmd.ExecuteNonQueryAsync();
        try
        {
            await using var schemaCmd = connection.CreateCommand();
            schemaCmd.CommandText = Schema;
            await schemaCmd.ExecuteNonQueryAsync();
        }
        finally
        {
            await using var unlockCmd = connection.CreateCommand();
            unlockCmd.CommandText = $"SELECT pg_advisory_unlock({SchemaSetupId});";
            await unlockCmd.ExecuteNonQueryAsync();
        }
    }

    // Create a user account with opening balance, returns the account and whether it was created or already existed
    public static async Task<(AccountResponse Account, bool Created)> CreateAccountAsync(NpgsqlDataSource db, Guid id, string currency, long openingMinor)
    {
        await using var connection = await db.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);

        await using (var insert = Command(connection, transaction,
            """
            INSERT INTO accounts (id, currency, opening_minor, balance_minor, is_system)
            VALUES (@id, @currency, @opening, 0, false)
            ON CONFLICT DO NOTHING
            RETURNING id;
            """))

        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("currency", currency);
            insert.Parameters.AddWithValue("opening", openingMinor);

            if (await insert.ExecuteScalarAsync() is null)
            {
                await using var select = Command(connection, transaction,
                    """
                    SELECT currency, opening_minor
                    FROM accounts
                    WHERE id = @id AND NOT is_system;
                    """);
                select.Parameters.AddWithValue("id", id);

                bool matches;
                await using (var reader = await select.ExecuteReaderAsync())
                {
                    matches = await reader.ReadAsync() &&
                        reader.GetString(0) == currency &&
                        reader.GetInt64(1) == openingMinor;
                }

                if (!matches)
                {
                    throw Problem.AccountConflict();
                }

                await transaction.CommitAsync();
                return (new AccountResponse(id, currency, openingMinor), false);
            }

            // Positive opening balance is required for new accounts
            if (openingMinor > 0)
            {
                Guid equityId = await EquityAccountAsync(connection, transaction, currency);
                await InsertMovementAsync(connection, transaction, new Transfer(Guid.NewGuid(), "opening", equityId, id, openingMinor, currency, null));
            }
            await transaction.CommitAsync();
            return (new AccountResponse(id, currency, openingMinor), true);
        }
    }
    public static async Task<BalanceResponse> GetBalanceAsync(NpgsqlDataSource db, Guid id)
    {
        // Cast to bigint, table CHECK ensures balance is within 64-bit integer range
        await using var cmd = db.CreateCommand(
            """
                SELECT currency, balance_minor::bigint
                FROM accounts
                WHERE id = @id AND NOT is_system;
                """);
        cmd.Parameters.AddWithValue("id", id);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw Problem.AccountNotFound();
        }
        return new BalanceResponse(id, reader.GetString(0), decimal.ToInt64(reader.GetDecimal(1)));
    }

    // One hidden equity account per currency, used for opening balances and reversals
    // Invisible through API
    private static async Task<Guid> EquityAccountAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string currency)
    {
        await using (var insert = Command(connection, transaction,
            """
            INSERT INTO accounts (id, currency, opening_minor, balance_minor, is_system)
            VALUES (@id, @currency, 0, 0, true)
            ON CONFLICT DO NOTHING;
            """))
        {
            insert.Parameters.AddWithValue("id", Guid.NewGuid());
            insert.Parameters.AddWithValue("currency", currency);
            await insert.ExecuteNonQueryAsync();
        }

        await using var select = Command(connection, transaction,
            """
            SELECT id
            FROM accounts
            WHERE currency = @currency AND is_system FOR UPDATE;
            """);
        select.Parameters.AddWithValue("currency", currency);
        return (Guid)(await select.ExecuteScalarAsync() ?? throw new InvalidOperationException("Equity account not created"));
    }

    // Writes an immutable transfer and its two postings, updates balances, and returns the transfer
    private static async Task InsertMovementAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Transfer transfer)
    {
        await using var cmd = Command(connection, transaction,
            """
            SELECT id
            FROM accounts
            WHERE id IN (@source_id, @destination_id)
            ORDER BY id
            FOR UPDATE;

            INSERT INTO transfers (id, kind, source_id, destination_id, amount_minor, currency, reversal_of)
            VALUES (@id, @kind, @source_id, @destination_id, @amount_minor, @currency, @reversal_of);

            INSERT INTO postings (id, transfer_id, account_id, amount_minor)
            VALUES (@source_posting_id, @id, @source_id, -@amount_minor),
                   (@destination_posting_id, @id, @destination_id, @amount_minor);

            UPDATE accounts
            SET balance_minor = balance_minor - @amount_minor
            WHERE id = @source_id;

            UPDATE accounts
            SET balance_minor = balance_minor + @amount_minor
            WHERE id = @destination_id;
            """);

        cmd.Parameters.AddWithValue("id", transfer.Id);
        cmd.Parameters.AddWithValue("kind", transfer.Kind);
        cmd.Parameters.AddWithValue("source_id", transfer.SourceId);
        cmd.Parameters.AddWithValue("destination_id", transfer.DestinationId);
        cmd.Parameters.AddWithValue("amount_minor", transfer.AmountMinor);
        cmd.Parameters.AddWithValue("currency", transfer.Currency);
        cmd.Parameters.AddWithValue("source_posting_id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("destination_posting_id", Guid.NewGuid());
        cmd.Parameters.Add("reversal_of", NpgsqlDbType.Uuid).Value = (object?)transfer.ReversalOf ?? DBNull.Value;
        await cmd.ExecuteNonQueryAsync();
    }

    // Helper method to create a command
    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        return cmd;
    }
}

// Storage shape of transfer row
internal sealed record Transfer(
    Guid Id,
    string Kind,
    Guid SourceId,
    Guid DestinationId,
    long AmountMinor,
    string Currency,
    Guid? ReversalOf);