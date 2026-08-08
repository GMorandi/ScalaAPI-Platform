using SqlSugar;

namespace ScalaAPI.Admin.Data;

public class ListingRepository(ISqlSugarClient db)
{
    public async Task<List<long>> GetIntegerGrainIds(string grainType, int page, int size)
    {
        return await db.Ado.SqlQueryAsync<long>(
            """SELECT grainidn1 FROM orleansstorage WHERE graintypestring = @type AND grainidn1 IS NOT NULL AND payloadbinary IS NOT NULL ORDER BY grainidn1 LIMIT @size OFFSET @offset""",
            new { type = grainType, size, offset = page * size });
    }

    public async Task<List<string>> GetStringGrainIds(string grainType, int page, int size)
    {
        return await db.Ado.SqlQueryAsync<string>(
            """SELECT grainidextensionstring FROM orleansstorage WHERE graintypestring = @type AND grainidextensionstring IS NOT NULL AND payloadbinary IS NOT NULL ORDER BY grainidextensionstring LIMIT @size OFFSET @offset""",
            new { type = grainType, size, offset = page * size });
    }

    public async Task<int> CountGrains(string grainType)
    {
        return await db.Ado.GetIntAsync(
            """SELECT COUNT(*) FROM orleansstorage WHERE graintypestring = @type AND payloadbinary IS NOT NULL""",
            new { type = grainType });
    }
}
