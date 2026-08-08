using SqlSugar;

namespace ScalaAPI.Admin.Data;

public class ListingRepository(ISqlSugarClient db)
{
    public async Task<List<long>> GetIntegerGrainIds(string grainType, int page, int size)
    {
        return await db.Ado.SqlQueryAsync<long>(
            """SELECT entity_id FROM entity_registry WHERE entity_type = @type AND entity_id IS NOT NULL AND status = 'active' ORDER BY entity_id LIMIT @size OFFSET @offset""",
            new { type = grainType, size, offset = page * size });
    }

    public async Task<List<string>> GetStringGrainIds(string grainType, int page, int size)
    {
        return await db.Ado.SqlQueryAsync<string>(
            """SELECT entity_key FROM entity_registry WHERE entity_type = @type AND status = 'active' ORDER BY entity_key LIMIT @size OFFSET @offset""",
            new { type = grainType, size, offset = page * size });
    }

    public async Task<int> CountGrains(string grainType)
    {
        return await db.Ado.GetIntAsync(
            """SELECT COUNT(*) FROM entity_registry WHERE entity_type = @type AND status = 'active'""",
            new { type = grainType });
    }

    public async Task RegisterInteger(string entityType, long entityId)
    {
        await db.Ado.ExecuteCommandAsync("""
            INSERT INTO entity_registry(entity_type, entity_key, entity_id)
            VALUES (@type, @key, @id)
            ON CONFLICT (entity_type, entity_key) DO UPDATE SET
                entity_id = EXCLUDED.entity_id, status = 'active', updated_at = now()
            """, new { type = entityType, key = entityId.ToString(), id = entityId });
    }

    public async Task RegisterString(string entityType, string entityKey, long? entityId = null)
    {
        await db.Ado.ExecuteCommandAsync("""
            INSERT INTO entity_registry(entity_type, entity_key, entity_id)
            VALUES (@type, @key, @id)
            ON CONFLICT (entity_type, entity_key) DO UPDATE SET
                entity_id = EXCLUDED.entity_id, status = 'active', updated_at = now()
            """, new { type = entityType, key = entityKey, id = entityId });
    }

    public async Task Unregister(string entityType, string entityKey)
    {
        await db.Ado.ExecuteCommandAsync(
            "DELETE FROM entity_registry WHERE entity_type = @type AND entity_key = @key",
            new { type = entityType, key = entityKey });
    }
}
