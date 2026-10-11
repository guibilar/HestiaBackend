using Hestia.Platform.Modules;

namespace Hestia.Api;

/// <summary>
/// Runs a module command (<see cref="IModuleCommand"/>) when the first command-line argument is its name, for example
/// <c>dotnet Hestia.Api.dll import-finance-user</c>. The host then does not start the web server.
/// </summary>
public static class ModuleCommands
{
    /// <summary>The command that the first argument names, or null (start the web server).</summary>
    public static IModuleCommand? Find(IServiceProvider services, IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0].StartsWith('-') || args[0].Contains('='))
        {
            return null;
        }
        var commands = services.GetServices<IModuleCommand>().ToList();
        return commands.FirstOrDefault(command => command.Name == args[0])
            ?? throw new InvalidOperationException(
                $"Unknown command '{args[0]}'. Commands: {string.Join(", ", commands.Select(c => $"{c.Name} ({c.Description})"))}.");
    }

    public static async Task<int> RunAsync(IServiceProvider services, IModuleCommand command, IReadOnlyList<string> args)
    {
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };
        return await command.RunAsync(services, args.Skip(1).ToList(), Console.In, Console.Out, cancel.Token);
    }
}
