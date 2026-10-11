namespace Hestia.Platform.Modules;

/// <summary>
/// A one-time command of a module, for example a data import. The host runs it in place of the web server when the
/// first command-line argument is its <see cref="Name"/>: <c>dotnet Hestia.Api.dll &lt;name&gt; [arguments]</c>.
/// In Docker: <c>docker compose run --rm -T hestia-api &lt;name&gt;</c>. The host applies the migrations first
/// (Database:MigrateOnStartup). Register a command in the module's <c>AddServices</c> as a singleton.
/// </summary>
public interface IModuleCommand
{
    /// <summary>The command name, in lower case with dashes, for example "import-finance-user".</summary>
    string Name { get; }

    /// <summary>A one-line description for the help text.</summary>
    string Description { get; }

    /// <summary>Runs the command. Returns the process exit code: 0 for success.</summary>
    /// <param name="services">The host services. Create a scope for scoped services, for example a DbContext.</param>
    /// <param name="arguments">The arguments after the command name.</param>
    /// <param name="input">Standard input.</param>
    /// <param name="output">Standard output, for messages to the user.</param>
    Task<int> RunAsync(IServiceProvider services, IReadOnlyList<string> arguments, TextReader input, TextWriter output,
        CancellationToken cancellationToken);
}
