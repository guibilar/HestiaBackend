using System.Reflection;
using System.Xml.Linq;
using Hestia.Platform.Modules;

namespace Hestia.Architecture.Tests;

/// <summary>Reads the Hestia projects from the repository and the built assemblies from the test folder.</summary>
public static class Solution
{
    public const string SolutionFile = "Hestia.slnx";

    public static string RootDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFile)))
            {
                return directory.FullName;
            }
        }
        throw new InvalidOperationException($"{SolutionFile} not found above {AppContext.BaseDirectory}.");
    }

    /// <summary>Every project under src/, with the project references in its .csproj file.</summary>
    public static IReadOnlyList<ProjectNode> DeclaredProjects() =>
        Directory.EnumerateFiles(Path.Combine(RootDirectory(), "src"), "*.csproj", SearchOption.AllDirectories)
            .Select(path => new ProjectNode(
                Path.GetFileNameWithoutExtension(path),
                XDocument.Load(path).Descendants("ProjectReference")
                    .Select(reference => (string?)reference.Attribute("Include"))
                    .OfType<string>()
                    .Select(include => Path.GetFileNameWithoutExtension(include.Replace('\\', '/')))
                    .ToList()))
            .OrderBy(project => project.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>Every built Hestia assembly next to the tests (all of them: the host references every module).</summary>
    public static IReadOnlyList<Assembly> BuiltAssemblies() =>
        Directory.EnumerateFiles(AppContext.BaseDirectory, "Hestia.*.dll")
            .Select(path => AssemblyName.GetAssemblyName(path))
            .Where(name => name.Name is not null && ModuleRules.IsHestiaProject(name.Name))
            .Select(name => Assembly.Load(name))
            .OrderBy(assembly => assembly.GetName().Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>The assemblies of the internal module projects.</summary>
    public static IReadOnlyList<Assembly> ModuleAssemblies() =>
        BuiltAssemblies()
            .Where(assembly => ModuleRules.InternalModuleOf(assembly.GetName().Name!) is not null)
            .ToList();

    /// <summary>Each built assembly, with the Hestia assemblies that its compiled code uses.</summary>
    public static IReadOnlyList<ProjectNode> BuiltProjects() =>
        BuiltAssemblies()
            .Select(assembly => new ProjectNode(
                assembly.GetName().Name!,
                assembly.GetReferencedAssemblies().Select(reference => reference.Name).OfType<string>().ToList()))
            .ToList();

    /// <summary>The module name that an <see cref="IModule"/> in this assembly must use.</summary>
    public static string ModuleNameOf(Assembly assembly) => ModuleRules.InternalModuleOf(assembly.GetName().Name!)!;
}
