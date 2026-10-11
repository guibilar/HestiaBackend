using System.Text.RegularExpressions;

namespace Hestia.Architecture.Tests;

/// <summary>A project (or assembly) and the Hestia projects that it references.</summary>
public sealed record ProjectNode(string Name, IReadOnlyCollection<string> References);

/// <summary>
/// The module rules of decision D6, as a check over a graph of references. The tests run it on the
/// project files (declared references) and on the built assemblies (references that the code uses).
/// </summary>
public static partial class ModuleRules
{
    public const string Host = "Hestia.Api";
    public const string Platform = "Hestia.Platform";
    public const string SharedContracts = "Hestia.Contracts";

    [GeneratedRegex(@"^Hestia\.Modules\.(?<module>[A-Za-z0-9]+)$")]
    private static partial Regex ModuleInternal();

    [GeneratedRegex(@"^Hestia\.Modules\.(?<module>[A-Za-z0-9]+)\.Contracts$")]
    private static partial Regex ModuleContracts();

    /// <summary>The module of an internal module project, or null.</summary>
    public static string? InternalModuleOf(string name) =>
        ModuleInternal().Match(name) is { Success: true } match ? match.Groups["module"].Value : null;

    /// <summary>The module of a module's .Contracts project, or null.</summary>
    public static string? ContractsModuleOf(string name) =>
        ModuleContracts().Match(name) is { Success: true } match ? match.Groups["module"].Value : null;

    /// <summary>True for the projects that the rules cover (not tests, not outside libraries).</summary>
    public static bool IsHestiaProject(string name) =>
        name is Host or Platform or SharedContracts || InternalModuleOf(name) is not null || ContractsModuleOf(name) is not null;

    /// <summary>Returns one line per broken rule. An empty list means that all rules hold.</summary>
    public static IReadOnlyList<string> Check(IEnumerable<ProjectNode> projects)
    {
        var violations = new List<string>();
        foreach (var project in projects.Where(p => IsHestiaProject(p.Name)))
        {
            foreach (var reference in project.References.Where(IsHestiaProject))
            {
                if (Violation(project.Name, reference) is { } reason)
                {
                    violations.Add($"{project.Name} must not reference {reference}: {reason}");
                }
            }
        }
        return violations;
    }

    /// <summary>Null when <paramref name="from"/> may reference <paramref name="to"/>; otherwise the rule it breaks.</summary>
    private static string? Violation(string from, string to)
    {
        if (from == Host)
        {
            return null; // The composition root references everything.
        }
        if (to == Host)
        {
            return "only the host is the composition root";
        }
        if (from == Platform)
        {
            return "the platform is the bottom layer";
        }
        if (from == SharedContracts)
        {
            return to == Platform ? null : "shared contracts reference only the platform";
        }
        if (ContractsModuleOf(from) is not null)
        {
            return to is Platform or SharedContracts ? null : ".Contracts projects reference only Hestia.Platform and Hestia.Contracts";
        }
        if (InternalModuleOf(from) is { } module)
        {
            if (to is Platform or SharedContracts || ContractsModuleOf(to) is not null)
            {
                return null;
            }
            if (InternalModuleOf(to) is { } other && other != module)
            {
                return $"use Hestia.Modules.{other}.Contracts, not the internal project of another module";
            }
        }
        return null;
    }
}
