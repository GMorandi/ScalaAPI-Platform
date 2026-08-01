namespace Sub2Api.Grains.Interfaces;

public interface IInvalidationService
{
    void NotifyChange(string entityType, string entityKey);
}
