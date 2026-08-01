INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'CleanupDefunctSiloEntriesKey','
    DELETE FROM OrleansMembershipTable
    WHERE DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL
    AND IAmAliveTime < @IAmAliveTime AND @IAmAliveTime IS NOT NULL
    AND Status != @Status AND @Status IS NOT NULL;
');
