using Npgsql;

namespace Sub2Api.Admin.Data;

public class ListingRepository(string connectionString)
{
    public async Task<List<long>> GetIntegerGrainIds(string grainType, int page, int size)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await using var cmd = new NpgsqlCommand(
            """SELECT grainidn1 FROM orleansstorage WHERE graintypestring = @type AND grainidn1 IS NOT NULL AND payloadbinary IS NOT NULL ORDER BY grainidn1 LIMIT @size OFFSET @offset""",
            conn);
        cmd.Parameters.AddWithValue("type", grainType);
        cmd.Parameters.AddWithValue("size", size);
        cmd.Parameters.AddWithValue("offset", page * size);

        await conn.OpenAsync();
        await using var reader = await cmd.ExecuteReaderAsync();
        var ids = new List<long>();
        while (await reader.ReadAsync())
            ids.Add(reader.GetInt64(0));
        return ids;
    }

    public async Task<List<string>> GetStringGrainIds(string grainType, int page, int size)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await using var cmd = new NpgsqlCommand(
            """SELECT grainidextensionstring FROM orleansstorage WHERE graintypestring = @type AND grainidextensionstring IS NOT NULL AND payloadbinary IS NOT NULL ORDER BY grainidextensionstring LIMIT @size OFFSET @offset""",
            conn);
        cmd.Parameters.AddWithValue("type", grainType);
        cmd.Parameters.AddWithValue("size", size);
        cmd.Parameters.AddWithValue("offset", page * size);

        await conn.OpenAsync();
        await using var reader = await cmd.ExecuteReaderAsync();
        var ids = new List<string>();
        while (await reader.ReadAsync())
            ids.Add(reader.GetString(0));
        return ids;
    }

    public async Task<int> CountGrains(string grainType)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await using var cmd = new NpgsqlCommand(
            """SELECT COUNT(*) FROM orleansstorage WHERE graintypestring = @type AND payloadbinary IS NOT NULL""",
            conn);
        cmd.Parameters.AddWithValue("type", grainType);

        await conn.OpenAsync();
        return (int)(long)(await cmd.ExecuteScalarAsync())!;
    }
}
