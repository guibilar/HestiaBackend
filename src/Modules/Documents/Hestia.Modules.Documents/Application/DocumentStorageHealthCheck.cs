using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Hestia.Modules.Documents.Application;

/// <summary>
/// The readiness check <c>documents-storage</c> (T59), part of <c>/health/ready</c>: the storage folder
/// (<c>Documents:StoragePath</c>) exists, and the host can write and delete a small file in its <c>tmp/</c> folder. Without
/// it, uploads fail with 500. In Docker, the cause is a volume that is missing or that belongs to another user.
/// </summary>
public sealed class DocumentStorageHealthCheck(DocumentStorage storage) : IHealthCheck
{
    public const string Name = "documents-storage";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(storage.Root))
        {
            return HealthCheckResult.Unhealthy(
                $"The documents folder {storage.Root} does not exist (Documents:StoragePath; in Docker, the volume hestia_documents).");
        }
        var probe = Path.Combine(storage.Root, DocumentStorage.TempFolder, $"health-{Guid.NewGuid():N}.check");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(probe)!);
            await File.WriteAllBytesAsync(probe, "ready"u8.ToArray(), cancellationToken);
            File.Delete(probe);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return HealthCheckResult.Unhealthy(
                $"The host cannot write and delete a file in {Path.GetDirectoryName(probe)}. Check the owner of the documents volume.",
                exception);
        }
    }
}
