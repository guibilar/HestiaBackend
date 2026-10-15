using Hestia.Modules.Sample.Contracts;
using Hestia.Testing;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Services;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Runtime.Handlers;

namespace Hestia.Api.Tests;

/// <summary>
/// T39: the Wolverine handler code is pre-generated and compiled into <c>Hestia.Api</c>
/// (<c>src/Hestia.Api/Internal/Generated</c>). The host loads these types (<see cref="TypeLoadMode.Auto"/>), so it does
/// not compile a handler with Roslyn at the first message of each type. This test fails when the files are not current
/// (a new or changed consumer). To update them, run the tests with <c>HESTIA_UPDATE_HANDLER_CODE=1</c>, build, and commit
/// the files.
/// </summary>
public class GeneratedHandlerCodeTests
{
    public const string UpdateVariable = "HESTIA_UPDATE_HANDLER_CODE";
    public const string RelativePath = "src/Hestia.Api/Internal/Generated";

    [Fact]
    public async Task TheCommittedHandlerCode_IsCurrent()
    {
        // The consumers of the real host: no test recorder.
        await using var factory = new HestiaApiFactory(recordEvents: false);
        factory.CreateClient();
        var current = Generate(factory.Services);
        // A guard against an empty list: the module consumers are there.
        Assert.Contains(current.Keys, name => name.Contains("SampleItemCreated", StringComparison.Ordinal));

        var folder = Path.Combine(RepositoryRoot(), RelativePath);
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
            foreach (var (name, code) in current)
            {
                var path = Path.Combine(folder, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, code);
            }
            return;
        }

        var committed = Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories).ToDictionary(
                path => Path.GetRelativePath(folder, path).Replace('\\', '/'),
                path => File.ReadAllText(path).ReplaceLineEndings("\n"))
            : new Dictionary<string, string>();
        var different = current.Keys.Union(committed.Keys)
            .Where(name => !current.TryGetValue(name, out var code) || !committed.TryGetValue(name, out var file) || code != file)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.True(different.Count == 0,
            $"{RelativePath} is not current ({string.Join(", ", different)}). Run `{UpdateVariable}=1 dotnet test`, then commit the files.");
    }

    [Fact]
    public async Task TheHost_LoadsThePreGeneratedTypes_AndATestHostCompilesItsOwn()
    {
        await using var real = new HestiaApiFactory(recordEvents: false);
        real.CreateClient();
        await using var test = new HestiaApiFactory();
        test.CreateClient();

        Assert.Equal(TypeLoadMode.Auto, real.Services.GetRequiredService<WolverineOptions>().CodeGeneration.TypeLoadMode);
        Assert.Equal(TypeLoadMode.Dynamic, test.Services.GetRequiredService<WolverineOptions>().CodeGeneration.TypeLoadMode);
        // The real host uses the type compiled into Hestia.Api; the test host, a type compiled at run time (with the
        // test recorder).
        var realHandler = real.Services.GetRequiredService<HandlerGraph>().HandlerFor(typeof(SampleItemCreated))!;
        var testHandler = test.Services.GetRequiredService<HandlerGraph>().HandlerFor(typeof(SampleItemCreated))!;
        Assert.Same(typeof(Program).Assembly, realHandler.GetType().Assembly);
        Assert.NotSame(typeof(Program).Assembly, testHandler.GetType().Assembly);
    }

    /// <summary>The generated files (path under Internal/Generated, code), as the Wolverine "codegen write" command writes them.</summary>
    private static Dictionary<string, string> Generate(IServiceProvider services)
    {
        var rules = services.GetRequiredService<WolverineOptions>().CodeGeneration;
        var folder = Path.Combine(Path.GetTempPath(), $"hestia-codegen-{Guid.NewGuid():N}");
        var before = rules.GeneratedCodeOutputPath;
        rules.GeneratedCodeOutputPath = folder;
        try
        {
            // As the Wolverine "codegen write" command: the services of the host give the handler dependencies.
            var builder = new DynamicCodeBuilder(services, services.GetServices<ICodeFileCollection>().ToArray())
            {
                ServiceVariableSource = new ServiceCollectionServerVariableSource(services.GetRequiredService<IServiceContainer>())
            };
            builder.WriteGeneratedCode(_ => { });
            return Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories).ToDictionary(
                path => Path.GetRelativePath(folder, path).Replace('\\', '/'),
                path => File.ReadAllText(path).ReplaceLineEndings("\n"));
        }
        finally
        {
            rules.GeneratedCodeOutputPath = before;
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Hestia.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Hestia.slnx not found above the test folder.");
    }
}
