using Hestia.Modules.Home.Application;
using Hestia.Modules.People.Application;
using Hestia.Modules.Tasks.Application;
using Hestia.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hestia.Api.Tests;

/// <summary>T85: one time zone for all modules (<c>Hestia:TimeZone</c>), with a module setting as an override.</summary>
public class TimeZoneTests
{
    [Fact]
    public async Task EveryModule_UsesTheSharedZone_AndAnOverrideWins_WithAWarning()
    {
        var logs = new LogCapture();
        await using var factory = new HestiaApiFactory(new Dictionary<string, string?>
        {
            ["Hestia:TimeZone"] = "America/Edmonton",
            ["Tasks:TimeZone"] = "",
            ["Home:TimeZone"] = "",
            ["People:TimeZone"] = "Europe/Lisbon",
        }, services => services.AddSingleton<ILoggerProvider>(logs));
        factory.CreateClient();

        Assert.Equal("America/Edmonton", factory.Services.GetRequiredService<IOptions<TasksOptions>>().Value.TimeZone);
        Assert.Equal("America/Edmonton", factory.Services.GetRequiredService<IOptions<HomeOptions>>().Value.TimeZone);
        Assert.Equal("Europe/Lisbon", factory.Services.GetRequiredService<IOptions<PeopleOptions>>().Value.TimeZone);

        Assert.Single(logs.Find("Time zone: America/Edmonton", LogLevel.Information));
        var warning = Assert.Single(logs.Find(":TimeZone is", LogLevel.Warning));
        Assert.Contains("People:TimeZone is Europe/Lisbon", warning.Message);
    }
}
