using Hestia.Modules.Documents.Contracts;
using Hestia.Modules.Documents.Infrastructure;
using Hestia.Platform.Modules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Modules.Documents.Application.Ocr;

/// <summary>
/// <c>documents-retry-ocr</c>: puts every document whose OCR failed, or had no OCR (it was off), back to Pending. The host
/// runs the OCR after its next start. Use it after a fix on the host, for example a new Tesseract language.
/// In Docker: <c>docker compose run --rm -T hestia-api documents-retry-ocr</c>, then <c>docker compose restart hestia-api</c>.
/// </summary>
public sealed class RetryOcrCommand : IModuleCommand
{
    public string Name => "documents-retry-ocr";

    public string Description => "Runs the OCR again for the documents whose OCR failed or was off";

    public async Task<int> RunAsync(IServiceProvider services, IReadOnlyList<string> arguments, TextReader input,
        TextWriter output, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentsDbContext>();
        var documents = await db.Documents
            .Where(d => d.OcrStatus == OcrStatus.Failed || d.OcrStatus == OcrStatus.NotNeeded)
            .ToListAsync(cancellationToken);
        foreach (var document in documents)
        {
            document.OcrStatus = OcrStatus.Pending;
            document.OcrError = null;
        }
        await db.SaveChangesAsync(cancellationToken);
        await output.WriteLineAsync($"{documents.Count} document(s) wait for the OCR. The host runs it when it starts.");
        return 0;
    }
}
