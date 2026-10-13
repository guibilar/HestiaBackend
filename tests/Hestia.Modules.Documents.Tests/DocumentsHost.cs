using System.Collections.Concurrent;
using System.Net.Http.Headers;
using Hestia.Modules.Documents.Application;
using Hestia.Modules.Documents.Application.Ocr;
using Hestia.Modules.Documents.Contracts;
using Hestia.Modules.Documents.Infrastructure;
using Hestia.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hestia.Modules.Documents.Tests;

/// <summary>A sample file for an upload.</summary>
public sealed record SampleFile(string Name, byte[] Bytes, string ContentType)
{
    /// <summary>A scanned page as a PDF without a text layer: "WARRANTY CARD", "TOASTER MODEL 2031".</summary>
    public static SampleFile Pdf => FromSamples("scanned-warranty.pdf", FileTypes.Pdf);

    /// <summary>A PNG: "HESTIA TEST RECEIPT", "KETTLE 49.99".</summary>
    public static SampleFile Png => FromSamples("receipt.png", FileTypes.Png);

    /// <summary>The same picture as <see cref="Png"/>, as WebP.</summary>
    public static SampleFile Webp => FromSamples("receipt.webp", FileTypes.Webp);

    public static string PathOf(string name) => Path.Combine(AppContext.BaseDirectory, "Samples", name);

    public static SampleFile FromSamples(string name, string contentType) => new(name, File.ReadAllBytes(PathOf(name)), contentType);

    public string Sha256 => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Bytes));
}

/// <summary>The OCR of the tests: a text for each file, or an error, without the OCR programs.</summary>
public sealed class FakeOcrEngine : IOcrEngine
{
    public Func<string, string, OcrResult> Respond { get; set; } =
        (_, contentType) => OcrResult.Succeeded($"Scanned text of a {FileTypes.Name(contentType)} file");

    public ConcurrentQueue<string> Calls { get; } = new();

    public Task<OcrResult> ExtractTextAsync(string path, string contentType, CancellationToken cancellationToken)
    {
        Calls.Enqueue(path);
        return Task.FromResult(Respond(path, contentType));
    }
}

/// <summary>
/// The host with all modules (<see cref="HestiaApiFactory"/>), a private storage folder for the files, and the
/// <see cref="FakeOcrEngine"/> (or another OCR engine).
/// </summary>
public sealed class DocumentsHost : IAsyncDisposable
{
    public const string Documents = "/api/documents";

    private HttpClient? _client;

    private DocumentsHost(string storagePath, FakeOcrEngine ocr, HestiaApiFactory factory)
    {
        StoragePath = storagePath;
        Ocr = ocr;
        Factory = factory;
    }

    public string StoragePath { get; }
    public FakeOcrEngine Ocr { get; }
    public HestiaApiFactory Factory { get; }
    public HttpClient Client => _client ??= Factory.CreateClient();

    public static DocumentsHost Start(IReadOnlyDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? services = null, IOcrEngine? engine = null, bool signedIn = true)
    {
        var storagePath = Directory.CreateTempSubdirectory("hestia-documents-test-").FullName;
        var ocr = new FakeOcrEngine();
        var factory = new HestiaApiFactory(Settings(storagePath, settings), Services(engine ?? ocr, services), signedIn);
        return new DocumentsHost(storagePath, ocr, factory);
    }

    /// <summary>The test settings: the storage folder, and a fast OCR poll.</summary>
    public static Dictionary<string, string?> Settings(string storagePath, IReadOnlyDictionary<string, string?>? more = null)
    {
        var all = new Dictionary<string, string?>
        {
            ["Documents:StoragePath"] = storagePath,
            ["Documents:Ocr:PollInterval"] = "00:00:01"
        };
        foreach (var (key, value) in more ?? new Dictionary<string, string?>())
        {
            all[key] = value;
        }
        return all;
    }

    public static Action<IServiceCollection> Services(IOcrEngine engine, Action<IServiceCollection>? more = null) => services =>
    {
        services.RemoveAll<IOcrEngine>();
        services.AddSingleton(engine);
        more?.Invoke(services);
    };

    /// <summary>The files on the storage folder (not the uploads in progress), as storage keys.</summary>
    public IReadOnlyList<string> StoredFiles() =>
        Directory.EnumerateFiles(StoragePath, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(StoragePath, path).Replace('\\', '/'))
            .Where(key => !key.StartsWith(DocumentStorage.TempFolder + "/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

    public Task<T> WithDbAsync<T>(Func<DocumentsDbContext, Task<T>> action) =>
        Factory.WithScopeAsync(services => action(services.GetRequiredService<DocumentsDbContext>()));

    public async Task<HttpResponseMessage> UploadAsync(MultipartFormDataContent form, HttpClient? client = null) =>
        await (client ?? Client).PostAsync(Documents, form);

    public async Task<DocumentDto> UploadOkAsync(string? title = "Kettle receipt", string? kind = "Receipt",
        IEnumerable<string>? tags = null, string? issuedOn = null, string? expiresOn = null, string? notes = null,
        params SampleFile[] files)
    {
        var response = await UploadAsync(Form(title, kind, tags, issuedOn, expiresOn, notes, files.Length == 0 ? new[] { SampleFile.Png } : files));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<DocumentDto>();
    }

    /// <summary>Waits until the background OCR of the document finished (the status is no longer Pending).</summary>
    public async Task<DocumentDto> WaitForOcrAsync(Guid id, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (true)
        {
            var document = await (await Client.GetAsync($"{Documents}/{id}")).ReadAsync<DocumentDto>();
            if (document.OcrStatus != OcrStatus.Pending || DateTimeOffset.UtcNow > deadline)
            {
                return document;
            }
            await Task.Delay(50);
        }
    }

    public static MultipartFormDataContent Form(string? title = null, string? kind = null, IEnumerable<string>? tags = null,
        string? issuedOn = null, string? expiresOn = null, string? notes = null, params SampleFile[] files)
    {
        var form = new MultipartFormDataContent();
        Add("title", title);
        Add("kind", kind);
        foreach (var tag in tags ?? [])
        {
            Add("tags", tag);
        }
        Add("issuedOn", issuedOn);
        Add("expiresOn", expiresOn);
        Add("notes", notes);
        foreach (var file in files)
        {
            var content = new ByteArrayContent(file.Bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType);
            form.Add(content, "files", file.Name);
        }
        return form;

        void Add(string name, string? value)
        {
            if (value is not null)
            {
                form.Add(new StringContent(value), name);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        await Factory.DisposeAsync();
        try
        {
            Directory.Delete(StoragePath, recursive: true);
        }
        catch (IOException)
        {
            // A file can still be open for a moment; the temp folder is cleaned later.
        }
    }
}

/// <summary>Small helpers for the tests.</summary>
public static class Wait
{
    /// <summary>Reads until <paramref name="done"/> is true, or 15 seconds passed. Returns the last value.</summary>
    public static async Task<T> UntilAsync<T>(Func<Task<T>> read, Func<T, bool> done)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (true)
        {
            var value = await read();
            if (done(value) || DateTimeOffset.UtcNow > deadline)
            {
                return value;
            }
            await Task.Delay(50);
        }
    }
}

public static class DocumentsDb
{
    public static Task<int> CountAsync(this DocumentsHost host) => host.WithDbAsync(db => db.Documents.CountAsync());
}
