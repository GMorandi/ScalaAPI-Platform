using Fido2NetLib;

namespace ScalaAPI.Admin.Auth;

public sealed class EmptyPasskeyMetadataService : IMetadataService
{
    public Task<MetadataBLOBPayloadEntry?> GetEntryAsync(
        Guid aaguid, CancellationToken cancellationToken)
        => Task.FromResult<MetadataBLOBPayloadEntry?>(null);

    public bool ConformanceTesting() => false;
}
