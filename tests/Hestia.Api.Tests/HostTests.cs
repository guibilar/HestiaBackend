using System.Net;
using Hestia.Platform.Modules;
using Hestia.Platform.Persistence;
using Hestia.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Api.Tests;

public class HostTests
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthChecks_Answer200(string path)
    {
        // /health/ready also asks finance (see FinanceProxyTests).
        await using var finance = await FakeFinance.StartAsync();
        await using var factory = new HestiaApiFactory(finance.HostSettings());

        var response = await factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void Discover_FindsTheModules_AndSkipsContractsAndTests()
    {
        var modules = ModuleLoader.Discover();

        Assert.Contains(modules, module => module.Name == "Sample");
        Assert.All(modules, module =>
            Assert.Matches(ModuleLoader.ModuleAssemblyName(), module.GetType().Assembly.GetName().Name));
    }

    [Fact]
    public async Task EachModuleDatabase_IsRegistered_WithItsSchema()
    {
        await using var factory = new HestiaApiFactory();

        var databases = factory.Services.GetServices<ModuleDatabase>().ToList();

        var sample = Assert.Single(databases, database => database.Schema == "sample");
        Assert.Equal("SampleDbContext", sample.ContextType.Name);
    }

    [Fact]
    public async Task UnknownPath_Is404()
    {
        await using var factory = new HestiaApiFactory();

        var response = await factory.CreateClient().GetAsync("/api/no-such-module");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
