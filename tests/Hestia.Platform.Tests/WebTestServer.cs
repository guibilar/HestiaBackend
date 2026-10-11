using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hestia.Platform.Tests;

/// <summary>A small in-memory web host, to test one piece of the platform without the full Hestia host.</summary>
internal static class WebTestServer
{
    public static async Task<IHost> StartAsync(Action<IServiceCollection> services, Action<IApplicationBuilder> app)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services)
                .Configure(app))
            .Build();
        await host.StartAsync();
        return host;
    }
}
