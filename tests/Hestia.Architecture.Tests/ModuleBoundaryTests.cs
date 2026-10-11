using Hestia.Platform.Modules;

namespace Hestia.Architecture.Tests;

/// <summary>The module rules of D6, on the real projects and on deliberate violations.</summary>
public class ModuleBoundaryTests
{
    [Fact]
    public void DeclaredProjectReferences_FollowTheModuleRules()
    {
        var projects = Solution.DeclaredProjects();

        Assert.Contains(projects, project => project.Name == "Hestia.Modules.Sample");
        Assert.Empty(ModuleRules.Check(projects));
    }

    [Fact]
    public void CompiledAssemblyReferences_FollowTheModuleRules()
    {
        var assemblies = Solution.BuiltProjects();

        Assert.Contains(assemblies, assembly => assembly.Name == "Hestia.Modules.Sample.Contracts");
        Assert.Empty(ModuleRules.Check(assemblies));
    }

    [Fact]
    public void EveryModuleProject_HasAContractsProject()
    {
        var names = Solution.DeclaredProjects().Select(project => project.Name).ToList();

        Assert.All(names.Where(name => ModuleRules.InternalModuleOf(name) is not null),
            name => Assert.Contains($"{name}.Contracts", names));
    }

    [Fact]
    public void EveryModuleAssembly_HasExactlyOneModuleClass_WithItsName()
    {
        Assert.All(Solution.ModuleAssemblies(), assembly =>
        {
            var module = Assert.Single(ModuleLoader.FindModules(assembly));
            Assert.Equal(Solution.ModuleNameOf(assembly), module.Name);
        });
    }

    // The proof for "a deliberate illegal reference makes the tests fail": the same check, on graphs with one
    // illegal reference each. A real project with such a reference fails the two tests above in the same way.

    [Fact]
    public void Violation_AModuleReferencesTheInternalProjectOfAnotherModule()
    {
        var violations = ModuleRules.Check([
            new ProjectNode("Hestia.Modules.Documents", ["Hestia.Platform", "Hestia.Modules.Sample"]),
            new ProjectNode("Hestia.Modules.Sample", ["Hestia.Platform"])
        ]);

        var violation = Assert.Single(violations);
        Assert.StartsWith("Hestia.Modules.Documents must not reference Hestia.Modules.Sample:", violation);
    }

    [Fact]
    public void Allowed_AModuleReferencesTheContractsOfAnotherModule()
    {
        var violations = ModuleRules.Check([
            new ProjectNode("Hestia.Modules.Documents", ["Hestia.Platform", "Hestia.Contracts", "Hestia.Modules.Sample.Contracts"]),
            new ProjectNode("Hestia.Modules.Documents.Contracts", ["Hestia.Platform", "Hestia.Contracts"])
        ]);

        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("Hestia.Modules.Sample.Contracts", "Hestia.Modules.Sample")]
    [InlineData("Hestia.Modules.Sample.Contracts", "Hestia.Modules.Documents.Contracts")]
    [InlineData("Hestia.Modules.Sample", "Hestia.Api")]
    [InlineData("Hestia.Platform", "Hestia.Contracts")]
    [InlineData("Hestia.Contracts", "Hestia.Modules.Sample.Contracts")]
    public void Violation_OtherIllegalReferences(string from, string to)
    {
        var violations = ModuleRules.Check([new ProjectNode(from, [to])]);

        Assert.Single(violations);
    }

    [Fact]
    public void ReferencesOutsideHestia_AreNotChecked()
    {
        var violations = ModuleRules.Check([
            new ProjectNode("Hestia.Modules.Sample", ["Microsoft.EntityFrameworkCore", "System.Runtime"]),
            new ProjectNode("Hestia.Modules.Sample.Tests", ["Hestia.Modules.Sample", "Hestia.Api"])
        ]);

        Assert.Empty(violations);
    }
}
