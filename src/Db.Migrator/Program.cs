using System.Security.Cryptography;
using Npgsql;

if (args.Length != 1 || !Directory.Exists(args[0]))
    throw new ArgumentException("Usage: Db.Migrator <migration-directory>");

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings__Postgres is required");

await using var connection = new NpgsqlConnection(connectionString);
await connection.OpenAsync();

await using (var command = connection.CreateCommand())
{
    command.CommandText = """
        CREATE TABLE IF NOT EXISTS schema_migrations (
            version text PRIMARY KEY,
            checksum text NOT NULL,
            applied_at timestamptz NOT NULL DEFAULT now()
        )
        """;
    await command.ExecuteNonQueryAsync();
}

foreach (var path in Directory.EnumerateFiles(args[0], "*.sql").Order(StringComparer.Ordinal))
{
    var version = Path.GetFileName(path);
    var sql = await File.ReadAllTextAsync(path);
    var checksum = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sql)))
        .ToLowerInvariant();

    await using var check = connection.CreateCommand();
    check.CommandText = "SELECT checksum FROM schema_migrations WHERE version = $1";
    check.Parameters.AddWithValue(version);
    var appliedChecksum = (string?)await check.ExecuteScalarAsync();
    if (appliedChecksum is not null)
    {
        if (!string.Equals(appliedChecksum, checksum, StringComparison.Ordinal))
            throw new InvalidOperationException($"Applied migration {version} has changed");
        Console.WriteLine($"skip {version}");
        continue;
    }

    await using var transaction = await connection.BeginTransactionAsync();
    await using var apply = connection.CreateCommand();
    apply.Transaction = transaction;
    apply.CommandText = sql;
    await apply.ExecuteNonQueryAsync();

    await using var record = connection.CreateCommand();
    record.Transaction = transaction;
    record.CommandText = "INSERT INTO schema_migrations(version, checksum) VALUES ($1, $2)";
    record.Parameters.AddWithValue(version);
    record.Parameters.AddWithValue(checksum);
    await record.ExecuteNonQueryAsync();
    await transaction.CommitAsync();
    Console.WriteLine($"apply {version}");
}
