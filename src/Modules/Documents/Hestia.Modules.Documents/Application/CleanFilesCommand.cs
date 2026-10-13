using Hestia.Modules.Documents.Infrastructure;
using Hestia.Platform.Modules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Modules.Documents.Application;

/// <summary>
/// <c>documents-clean-files</c> (T59): finds the files on the documents volume that no document uses, for example after
/// a stop between a document delete and its file delete. It also finds the old files in <c>tmp/</c> (uploads that
/// stopped). Only files older than <see cref="MinimumAge"/> count, so an upload in progress is safe. Without
/// <c>--delete</c>, it only prints the files.
/// In Docker: <c>docker compose run --rm -T hestia-api documents-clean-files</c>, then the same with <c>--delete</c>.
/// </summary>
public sealed class CleanFilesCommand : IModuleCommand
{
    public const string DeleteArgument = "--delete";

    /// <summary>A file must be this old before the command removes it.</summary>
    public static readonly TimeSpan MinimumAge = TimeSpan.FromHours(1);

    public string Name => "documents-clean-files";

    public string Description => $"Lists the document files that no document uses (older than one hour); {DeleteArgument} deletes them";

    public async Task<int> RunAsync(IServiceProvider services, IReadOnlyList<string> arguments, TextReader input,
        TextWriter output, CancellationToken cancellationToken)
    {
        var unknown = arguments.Where(argument => argument != DeleteArgument).ToList();
        if (unknown.Count > 0)
        {
            await output.WriteLineAsync($"Unknown argument(s): {string.Join(" ", unknown)}. Usage: {Name} [{DeleteArgument}]");
            return 1;
        }
        var delete = arguments.Contains(DeleteArgument);

        var storage = services.GetRequiredService<DocumentStorage>();
        var time = services.GetRequiredService<TimeProvider>();
        if (!Directory.Exists(storage.Root))
        {
            await output.WriteLineAsync($"The documents folder {storage.Root} does not exist.");
            return 1;
        }

        HashSet<string> used;
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocumentsDbContext>();
            used = (await db.Files.AsNoTracking().Select(file => file.StorageKey).Distinct().ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.Ordinal);
        }

        var before = time.GetUtcNow().UtcDateTime - MinimumAge;
        var found = new List<(string Key, FileInfo File)>();
        foreach (var path in Directory.EnumerateFiles(storage.Root, "*", SearchOption.AllDirectories))
        {
            var file = new FileInfo(path);
            var key = Path.GetRelativePath(storage.Root, path).Replace('\\', '/');
            var inTemp = key.StartsWith(DocumentStorage.TempFolder + "/", StringComparison.Ordinal);
            if (!inTemp && !DocumentStorage.IsKey(key))
            {
                await output.WriteLineAsync($"skipped   {key} (not a document file; the command leaves it)");
                continue;
            }
            if ((inTemp || !used.Contains(key)) && file.LastWriteTimeUtc < before)
            {
                found.Add((key, file));
            }
        }

        long bytes = 0;
        var deleted = 0;
        var kept = 0;
        foreach (var (key, file) in found.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (!delete)
            {
                bytes += file.Length;
                await output.WriteLineAsync($"unused    {key} ({file.Length} bytes, {file.LastWriteTimeUtc:yyyy-MM-dd HH:mm} UTC)");
                continue;
            }
            // The host can run at the same time: an upload of the same content gives the file a new time, then saves its
            // row. So look again just before the delete.
            if (!await StillUnusedAsync(services, key, file, before, cancellationToken))
            {
                kept++;
                await output.WriteLineAsync($"kept      {key} (it changed, or a document uses it now)");
                continue;
            }
            try
            {
                var (size, written) = (file.Length, file.LastWriteTimeUtc);
                file.Delete();
                bytes += size;
                deleted++;
                await output.WriteLineAsync($"deleted   {key} ({size} bytes, {written:yyyy-MM-dd HH:mm} UTC)");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await output.WriteLineAsync($"error     {key}: {exception.Message}");
            }
        }

        if (found.Count == 0)
        {
            await output.WriteLineAsync("No unused files.");
        }
        else if (delete)
        {
            await output.WriteLineAsync($"Deleted {deleted} of {found.Count} unused file(s), {bytes} bytes.");
        }
        else
        {
            await output.WriteLineAsync($"{found.Count} unused file(s), {bytes} bytes. Run the command with {DeleteArgument} to delete them.");
        }
        return !delete || deleted + kept == found.Count ? 0 : 1;
    }

    private static async Task<bool> StillUnusedAsync(IServiceProvider services, string key, FileInfo file, DateTime before,
        CancellationToken cancellationToken)
    {
        file.Refresh();
        if (!file.Exists || file.LastWriteTimeUtc >= before)
        {
            return false;
        }
        if (key.StartsWith(DocumentStorage.TempFolder + "/", StringComparison.Ordinal))
        {
            return true;
        }
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentsDbContext>();
        return !await db.Files.AsNoTracking().AnyAsync(f => f.StorageKey == key, cancellationToken);
    }
}
