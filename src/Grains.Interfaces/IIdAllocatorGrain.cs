namespace ScalaAPI.Grains.Interfaces;

public interface IIdAllocatorGrain : IGrainWithStringKey
{
    Task<long> Next();
}
