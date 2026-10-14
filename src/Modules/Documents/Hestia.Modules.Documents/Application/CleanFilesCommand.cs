using Hestia.Platform.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Modules.Documents.Application;

/// <summary>
/// <c>documents-clean-files</c> (T59, T64): finds the files on the documents volume that no document uses, for example
/// after a stop between a document delete and its file delete. It also finds the old files in <c>tmp/</c> (uploads that
/// stopped). Only files older than <see cref="MinimumAge"/> count, so an upload in progress is safe. Without
/// <c>--delete</c>, it only prints the files. With <c>--check-missing</c>, it does the other check: it prints the
/// <c>DocumentFiles</c> rows whose file is not on the volume (it deletes nothing).
/// In Docker: <c>docker compose run --rm -T hestia-api documents-clean-files</c>, then the same with <c>--delete</c>.
/// </summary>
public sealed class CleanFilesCommand : IModuleCommand
{
    public const string DeleteArgument = "--delete";

    public const string CheckMissingArgument = "--check-missing";

    /// <summary>A file must be this old before the command removes it.</summary>
    public static readonly TimeSpan MinimumAge = DocumentFileCheck.MinimumAge;

    public string Name => "documents-clean-files";

    public string Description =>
        $"Lists the document files that no document uses (older than one hour); {DeleteArgument} deletes them; " +
        $"{CheckMissingArgument} lists the documents whose file is missing";

    private string Usage => $"Usage: {Name} [{DeleteArgument} | {CheckMissingArgument}]";

    public async Task<int> RunAsync(IServiceProvider services, IReadOnlyList<string> arguments, TextReader input,
        TextWriter output, CancellationToken cancellationToken)
    {
        var unknown = arguments.Where(argument => argument is not (DeleteArgument or CheckMissingArgument)).ToList();
        if (unknown.Count > 0)
        {
            await output.WriteLineAsync($"Unknown argument(s): {string.Join(" ", unknown)}. {Usage}");
            return 1;
        }
        var delete = arguments.Contains(DeleteArgument);
        var checkMissing = arguments.Contains(CheckMissingArgument);
        if (delete && checkMissing)
        {
            await output.WriteLineAsync($"Use {DeleteArgument} or {CheckMissingArgument}, not both. {Usage}");
            return 1;
        }

        var check = services.GetRequiredService<DocumentFileCheck>();
        if (!check.StorageExists)
        {
            await output.WriteLineAsync($"The documents folder {check.Root} does not exist.");
            return 1;
        }

        return checkMissing
            ? await CheckMissingAsync(check, output, cancellationToken)
            : await CleanAsync(check, delete, output, cancellationToken);
    }

    private static async Task<int> CheckMissingAsync(DocumentFileCheck check, TextWriter output, CancellationToken cancellationToken)
    {
        var missing = await check.FindMissingAsync(cancellationToken);
        foreach (var row in missing)
        {
            await output.WriteLineAsync($"missing   {row.StorageKey} (document {row.DocumentId}, file {row.FileId})");
        }
        if (missing.Count == 0)
        {
            await output.WriteLineAsync("No missing files.");
            return 0;
        }
        await output.WriteLineAsync(
            $"{missing.Count} file(s) of {missing.Select(row => row.DocumentId).Distinct().Count()} document(s) are not on the volume. " +
            "Restore them from a backup (the hestia-<time>.documents folder), or delete these documents.");
        // Not 0, so a script sees the problem.
        return 2;
    }

    private static async Task<int> CleanAsync(DocumentFileCheck check, bool delete, TextWriter output, CancellationToken cancellationToken)
    {
        var found = await check.FindUnusedAsync(cancellationToken);
        foreach (var key in found.Skipped)
        {
            await output.WriteLineAsync($"skipped   {key} (not a document file; the command leaves it)");
        }

        long bytes = 0;
        var deleted = 0;
        var kept = 0;
        foreach (var unused in found.Unused)
        {
            var file = unused.File;
            if (!delete)
            {
                bytes += file.Length;
                await output.WriteLineAsync($"unused    {unused.Key} ({file.Length} bytes, {file.LastWriteTimeUtc:yyyy-MM-dd HH:mm} UTC)");
                continue;
            }
            // The host can run at the same time: an upload of the same content gives the file a new time, then saves its
            // row. So look again just before the delete.
            if (!await check.StillUnusedAsync(unused, cancellationToken))
            {
                kept++;
                await output.WriteLineAsync($"kept      {unused.Key} (it changed, or a document uses it now)");
                continue;
            }
            try
            {
                var (size, written) = (file.Length, file.LastWriteTimeUtc);
                file.Delete();
                bytes += size;
                deleted++;
                await output.WriteLineAsync($"deleted   {unused.Key} ({size} bytes, {written:yyyy-MM-dd HH:mm} UTC)");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await output.WriteLineAsync($"error     {unused.Key}: {exception.Message}");
            }
        }

        var count = found.Unused.Count;
        if (count == 0)
        {
            await output.WriteLineAsync("No unused files.");
        }
        else if (delete)
        {
            await output.WriteLineAsync($"Deleted {deleted} of {count} unused file(s), {bytes} bytes.");
        }
        else
        {
            await output.WriteLineAsync($"{count} unused file(s), {bytes} bytes. Run the command with {DeleteArgument} to delete them.");
        }
        return !delete || deleted + kept == count ? 0 : 1;
    }
}
