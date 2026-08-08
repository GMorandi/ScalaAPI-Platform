using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace ScalaAPI.Provider.Mock.Tests;

public sealed class MockProviderHelpersTests
{
    [Fact]
    public void Scenario_UsesExplicitSourcesInPriorityOrder()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Provider-Scenario"] = "500";
        context.Request.QueryString = new QueryString("?scenario=429");
        using var body = JsonDocument.Parse("""
            {"mock_scenario":"timeout","user":"scalaapi-mock:disconnect"}
            """);

        var scenario = MockProviderHelpers.Scenario(context, body.RootElement);

        Assert.Equal("500", scenario);
    }

    [Fact]
    public void Scenario_UsesQueryWhenHeaderIsEmpty()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?scenario=429");
        using var body = JsonDocument.Parse("{}");

        Assert.Equal("429", MockProviderHelpers.Scenario(context, body.RootElement));
    }

    [Fact]
    public void Scenario_UsesDirectMockBodyForProviderContractTests()
    {
        var context = new DefaultHttpContext();
        using var body = JsonDocument.Parse("""{"mock_scenario":"malformed_usage"}""");

        Assert.Equal("malformed_usage",
            MockProviderHelpers.Scenario(context, body.RootElement));
    }

    [Fact]
    public void Scenario_UsesStandardUserFieldAcrossGatewayConversion()
    {
        var context = new DefaultHttpContext();
        using var body = JsonDocument.Parse("""{"user":"scalaapi-mock:disconnect"}""");

        Assert.Equal("disconnect", MockProviderHelpers.Scenario(context, body.RootElement));
    }

    [Theory]
    [InlineData("customer-123")]
    [InlineData("")]
    public void Scenario_DoesNotInterpretOrdinaryUserValues(string user)
    {
        var context = new DefaultHttpContext();
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(new { user }));

        Assert.Equal("success", MockProviderHelpers.Scenario(context, body.RootElement));
    }
}
