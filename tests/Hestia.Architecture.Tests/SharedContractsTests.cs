using System.Xml.Linq;
using Hestia.Contracts;

namespace Hestia.Architecture.Tests;

/// <summary>
/// Hestia.Contracts is a NuGet package for other services (the finance backend). It must not bring ASP.NET Core,
/// EF Core, Wolverine, or other Hestia projects with it.
/// </summary>
public class SharedContractsTests
{
    [Fact]
    public void SharedContracts_HaveNoPackageOrProjectReferences()
    {
        var project = XDocument.Load(Path.Combine(Solution.RootDirectory(), "src", "Hestia.Contracts", "Hestia.Contracts.csproj"));

        Assert.Empty(project.Descendants("PackageReference"));
        Assert.Empty(project.Descendants("ProjectReference"));
        Assert.Empty(project.Descendants("FrameworkReference"));
    }

    [Fact]
    public void SharedContracts_UseOnlyTheBaseLibrary()
    {
        var references = HestiaContracts.Assembly.GetReferencedAssemblies().Select(reference => reference.Name!).ToList();

        Assert.All(references, name => Assert.True(name == "netstandard" || name == "System" || name.StartsWith("System.", StringComparison.Ordinal),
            $"Hestia.Contracts references {name}."));
    }

    [Fact]
    public void EveryEvent_IsARecord_WithAVersionThatTravels()
    {
        var events = HestiaContracts.Assembly.GetExportedTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IIntegrationEvent).IsAssignableFrom(type))
            .ToList();

        Assert.NotEmpty(events);
        Assert.All(events, type =>
        {
            // Records, so a consumer can compare and log them.
            Assert.NotNull(type.GetMethod("<Clone>$"));
            var version = type.GetProperty(nameof(IIntegrationEvent.Version))!;
            Assert.True(version.CanWrite, $"{type.Name}.Version must have an init accessor, so it travels with the event.");
        });
    }
}
