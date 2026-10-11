using System.Reflection;
using System.Text.RegularExpressions;

namespace Hestia.Platform.Modules;

/// <summary>
/// Finds the modules next to the host: each assembly named Hestia.Modules.&lt;Name&gt; (not .Contracts, not .Tests)
/// in the folder of this assembly. The host project references each module project, so the build copies the
/// module there. No list of modules must be kept by hand.
/// </summary>
public static partial class ModuleLoader
{
    [GeneratedRegex(@"^Hestia\.Modules\.[A-Za-z0-9]+$", RegexOptions.CultureInvariant)]
    public static partial Regex ModuleAssemblyName();

    public static IReadOnlyList<IModule> Discover(string? directory = null)
    {
        directory ??= Path.GetDirectoryName(typeof(IModule).Assembly.Location);
        if (string.IsNullOrEmpty(directory))
        {
            directory = AppContext.BaseDirectory;
        }

        var modules = new List<IModule>();
        foreach (var path in Directory.EnumerateFiles(directory, "Hestia.Modules.*.dll").Order(StringComparer.Ordinal))
        {
            var name = AssemblyName.GetAssemblyName(path);
            if (name.Name is null || !ModuleAssemblyName().IsMatch(name.Name))
            {
                continue;
            }
            modules.AddRange(FindModules(Assembly.Load(name)));
        }
        return modules;
    }

    /// <summary>Creates the <see cref="IModule"/> classes of one assembly.</summary>
    public static IEnumerable<IModule> FindModules(Assembly assembly) =>
        assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IModule).IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .Select(type => (IModule)Activator.CreateInstance(type)!);
}
