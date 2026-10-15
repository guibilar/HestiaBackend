using Hestia.Modules.Documents.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Modules.Documents.Application;

/// <summary>A file on the volume that no document uses (see <see cref="DocumentFileCheck.FindUnusedAsync"/>).</summary>
/// <param name="Key">The path below the storage folder: a storage key, or <c>tmp/…</c>.</param>
public sealed record UnusedFile(string Key, FileInfo File);

/// <summary>The result of <see cref="DocumentFileCheck.FindUnusedAsync"/>.</summary>
/// <param name="Unused">The unused files, in key order.</param>
/// <param name="Skipped">The files that are not document files. The check leaves them.</param>
public sealed record UnusedFiles(IReadOnlyList<UnusedFile> Unused, IReadOnlyList<string> Skipped)
{
    public long Bytes => Unused.Sum(file => file.File.Length);
}

/// <summary>A <c>DocumentFiles</c> row whose file is not on the volume.</summary>
public sealed record MissingFile(Guid DocumentId, Guid FileId, string StorageKey);

/// <summary>
/// The two checks of the documents volume (T59, T64). <c>documents-clean-files</c> runs them on request, and
/// <see cref="FileCheckWorker"/> runs them on a schedule. Neither check deletes or changes anything.
/// </summary>
public sealed class DocumentFileCheck(DocumentStorage storage, IServiceScopeFactory scopes, TimeProvider time)
{
    /// <summary>A file must be this old before it counts as unused, so an upload in progress is safe.</summary>
    public static readonly TimeSpan MinimumAge = TimeSpan.FromHours(1);

    public string Root => storage.Root;

    public bool StorageExists => Directory.Exists(storage.Root);

    /// <summary>Only files that were written before this time can count as unused.</summary>
    public DateTime UnusedBefore => time.GetUtcNow().UtcDateTime - MinimumAge;

    /// <summary>
    /// The files that no document uses: a document file (<c>ab/abcdef…</c>) that no <c>DocumentFiles</c> row uses, and a
    /// file in <c>tmp/</c> (an upload that stopped). Only files older than <see cref="MinimumAge"/> count.
    /// </summary>
    public async Task<UnusedFiles> FindUnusedAsync(CancellationToken cancellationToken)
    {
        HashSet<string> used;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocumentsDbContext>();
            used = (await db.Files.AsNoTracking().Select(file => file.StorageKey).Distinct().ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.Ordinal);
        }

        var before = UnusedBefore;
        var unused = new List<UnusedFile>();
        var skipped = new List<string>();
        foreach (var path in Directory.EnumerateFiles(storage.Root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = new FileInfo(path);
            var key = Path.GetRelativePath(storage.Root, path).Replace('\\', '/');
            var inTemp = IsTemp(key);
            // A thumbnail (T61) is unused when its file is.
            var source = DocumentStorage.KeyOfThumbnail(key) ?? key;
            if (!inTemp && !DocumentStorage.IsKey(source))
            {
                skipped.Add(key);
                continue;
            }
            if ((inTemp || !used.Contains(source)) && file.LastWriteTimeUtc < before)
            {
                unused.Add(new UnusedFile(key, file));
            }
        }
        return new UnusedFiles(
            unused.OrderBy(file => file.Key, StringComparer.Ordinal).ToList(),
            skipped.Order(StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// The <c>DocumentFiles</c> rows whose file is not on the volume. Such a document fails to download, and its OCR
    /// fails. A row that a delete removed during the check does not count.
    /// </summary>
    public async Task<IReadOnlyList<MissingFile>> FindMissingAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentsDbContext>();
        var rows = await db.Files.AsNoTracking()
            .OrderBy(file => file.DocumentId).ThenBy(file => file.Position)
            .Select(file => new MissingFile(file.DocumentId, file.Id, file.StorageKey))
            .ToListAsync(cancellationToken);

        var missing = rows.Where(row => !DocumentStorage.IsKey(row.StorageKey) || !storage.Exists(row.StorageKey)).ToList();
        if (missing.Count == 0)
        {
            return missing;
        }
        // A delete at the same time removes the row first and the file next. So read the rows again.
        var ids = missing.Select(row => row.FileId).ToList();
        var still = (await db.Files.AsNoTracking().Where(file => ids.Contains(file.Id)).Select(file => file.Id)
            .ToListAsync(cancellationToken)).ToHashSet();
        return missing.Where(row => still.Contains(row.FileId)).ToList();
    }

    /// <summary>
    /// The rows of an earlier <see cref="FindMissingAsync"/> that are still missing: the row still exists, and its file
    /// is still not on the volume (T80). It looks only at these files, so it is cheap: it does not read the volume.
    /// </summary>
    public async Task<IReadOnlyList<MissingFile>> StillMissingAsync(IReadOnlyList<MissingFile> missing, CancellationToken cancellationToken)
    {
        if (missing.Count == 0)
        {
            return missing;
        }
        var ids = missing.Select(row => row.FileId).ToList();
        HashSet<Guid> rows;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocumentsDbContext>();
            rows = (await db.Files.AsNoTracking().Where(file => ids.Contains(file.Id)).Select(file => file.Id)
                .ToListAsync(cancellationToken)).ToHashSet();
        }
        return missing.Where(row => rows.Contains(row.FileId) &&
            (!DocumentStorage.IsKey(row.StorageKey) || !storage.Exists(row.StorageKey))).ToList();
    }

    /// <summary>True when the file is still unused just before a delete (it can have changed since the check).</summary>
    public async Task<bool> StillUnusedAsync(UnusedFile unused, CancellationToken cancellationToken)
    {
        var file = unused.File;
        file.Refresh();
        if (!file.Exists || file.LastWriteTimeUtc >= UnusedBefore)
        {
            return false;
        }
        if (IsTemp(unused.Key))
        {
            return true;
        }
        var source = DocumentStorage.KeyOfThumbnail(unused.Key) ?? unused.Key;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentsDbContext>();
        return !await db.Files.AsNoTracking().AnyAsync(f => f.StorageKey == source, cancellationToken);
    }

    private static bool IsTemp(string key) => key.StartsWith(DocumentStorage.TempFolder + "/", StringComparison.Ordinal);
}
