using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hestia.Testing;

namespace Hestia.Api.Tests;

/// <summary>
/// T48: the OpenAPI document of the module endpoints is in the repository (<c>docs/openapi/hestia.json</c>), so the
/// clients can read it without a running host. This test fails when the file is not current. To update it, run the
/// tests with <c>HESTIA_UPDATE_OPENAPI=1</c> and commit the file.
/// </summary>
public class OpenApiDocumentTests
{
    public const string UpdateVariable = "HESTIA_UPDATE_OPENAPI";
    public const string RelativePath = "docs/openapi/hestia.json";

    private static readonly JsonSerializerOptions Write = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    [Fact]
    public async Task TheCommittedDocument_IsCurrent()
    {
        await using var factory = new HestiaApiFactory();
        var response = await factory.CreateClient().GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        var current = Normalize(await response.Content.ReadAsStringAsync());

        var path = Path.Combine(RepositoryRoot(), RelativePath);
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            await File.WriteAllTextAsync(path, current);
            return;
        }

        var committed = File.Exists(path) ? await File.ReadAllTextAsync(path) : "";
        Assert.True(committed.ReplaceLineEndings("\n") == current,
            $"{RelativePath} is not current. Run `{UpdateVariable}=1 dotnet test` and commit the file.");
    }

    /// <summary>The document without the server address of the test host, indented, with a final new line.</summary>
    private static string Normalize(string json)
    {
        var document = JsonNode.Parse(json)!.AsObject();
        document.Remove("servers");
        return document.ToJsonString(Write).ReplaceLineEndings("\n") + "\n";
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
