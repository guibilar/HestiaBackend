using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Platform.Modules;

/// <summary>
/// One module in the host. Each module's internal project has exactly one class that implements this
/// interface, with a public parameterless constructor. The host finds it (see <see cref="ModuleLoader"/>),
/// then calls <see cref="AddServices"/> and <see cref="MapEndpoints"/>.
/// </summary>
public interface IModule
{
    /// <summary>The module name, for example "Documents". Its database schema is the lower-case name.</summary>
    string Name { get; }

    /// <summary>Registers the module's services, its DbContext included.</summary>
    void AddServices(IServiceCollection services, IConfiguration configuration);

    /// <summary>Maps the module's endpoints, under its own prefix (for example /api/documents).</summary>
    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
