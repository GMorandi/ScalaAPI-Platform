namespace ScalaAPI.Host.Services;

public static class GarnetKeyspace
{
    private const string Prefix = "scalaapi:v1:";

    public static string Auth(string keyHash) => $"{Prefix}auth:{keyHash}";
    public static string AccountProjection(long accountId) => $"{Prefix}acct:{accountId}:proj";
    public static string GroupRoutes(long groupId) => $"{Prefix}group:{groupId}:routes";
    public static string GroupConfig(long groupId) => $"{Prefix}group:{groupId}:config";
    public static string StickySession(long groupId, string sessionHash) =>
        $"{Prefix}sticky:{groupId}:{sessionHash}";
    public static string InvalidationVersion => $"{Prefix}invalidation:version";
    public static string ContentPolicyRevision => $"{Prefix}content-policy:revision";
    public static string ConfigRevision => $"{Prefix}config:revision";
    public static string ModelsList => $"{Prefix}models:list";
}
