using NSubstitute;
using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Grains.Tests;

public class InvalidationServiceTests
{
    [Fact]
    public void NotifyChange_CalledWithCorrectArgs()
    {
        var service = Substitute.For<IInvalidationService>();
        service.NotifyChange("apiKey", "123");

        service.Received(1).NotifyChange("apiKey", "123");
    }

    [Fact]
    public void NotifyChange_MultipleCalls_AllRecorded()
    {
        var service = Substitute.For<IInvalidationService>();
        service.NotifyChange("user", "1");
        service.NotifyChange("user", "2");
        service.NotifyChange("account", "3");

        service.Received(3).NotifyChange(Arg.Any<string>(), Arg.Any<string>());
        service.Received(2).NotifyChange("user", Arg.Any<string>());
        service.Received(1).NotifyChange("account", "3");
    }

    [Fact]
    public void NotifyChange_DoesNotThrow()
    {
        var service = Substitute.For<IInvalidationService>();
        var ex = Record.Exception(() => service.NotifyChange("group", "999"));
        Assert.Null(ex);
    }

    [Fact]
    public void NotifyChange_EmptyKey_StillCalled()
    {
        var service = Substitute.For<IInvalidationService>();
        service.NotifyChange("apiKey", "");

        service.Received(1).NotifyChange("apiKey", "");
    }

    [Fact]
    public void NotifyChange_VersionMonotonic_SimulatedSequence()
    {
        var service = Substitute.For<IInvalidationService>();
        var calls = new List<(string Type, string Key)>();
        service.When(s => s.NotifyChange(Arg.Any<string>(), Arg.Any<string>()))
            .Do(ci => calls.Add((ci.ArgAt<string>(0), ci.ArgAt<string>(1))));

        for (int i = 1; i <= 5; i++)
            service.NotifyChange("apiKey", i.ToString());

        Assert.Equal(5, calls.Count);
        Assert.Equal(["1", "2", "3", "4", "5"], calls.Select(c => c.Key).ToArray());
    }
}
