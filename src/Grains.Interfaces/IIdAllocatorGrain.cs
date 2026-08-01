namespace Sub2Api.Grains.Interfaces;

public interface IIdAllocatorGrain : IGrainWithStringKey
{
    Task<long> Next();
}
