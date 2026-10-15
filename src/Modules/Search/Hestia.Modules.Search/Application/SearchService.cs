using Hestia.Modules.Search.Contracts;
using Hestia.Platform.Exceptions;
using Hestia.Platform.Finance;
using Microsoft.Extensions.Logging;

namespace Hestia.Modules.Search.Application;

/// <summary>The settings of the Search module (section <c>Search</c>).</summary>
public sealed class SearchOptions
{
    public const string SectionName = "Search";

    /// <summary>How long the search waits for one module's source.</summary>
    public TimeSpan SourceTimeout { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// One search across all modules (T17): it calls every <see cref="ISearchSource"/> (or the one of a module) at the same
/// time, and puts their hits in module order. A source that fails or does not answer in time gives no hits, and its
/// module is marked as failed.
/// </summary>
public sealed class SearchService(
    IEnumerable<ISearchSource> sources,
    Microsoft.Extensions.Options.IOptions<SearchOptions> options,
    ILogger<SearchService> logger)
{
    public const int MinLength = 2;
    public const int MaxLength = 200;

    /// <summary>The hits of each module without <c>module</c>.</summary>
    public const int DefaultLimit = 5;

    /// <summary>The most hits of one module.</summary>
    public const int MaxLimit = 50;

    public async Task<SearchDto> SearchAsync(string? q, string? module, int? limit, CancellationToken cancellationToken)
    {
        var text = string.Join(' ', (q ?? string.Empty).Split((char[])[' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries));
        if (text.Length < MinLength)
        {
            throw new InvalidInputException("q", $"Type at least {MinLength} characters.");
        }
        if (text.Length > MaxLength)
        {
            throw new InvalidInputException("q", $"The search text can have at most {MaxLength} characters.");
        }
        var count = limit ?? (string.IsNullOrWhiteSpace(module) ? DefaultLimit : 20);
        if (count is < 1 or > MaxLimit)
        {
            throw new InvalidInputException("limit", $"limit must be from 1 to {MaxLimit}.");
        }

        var chosen = sources.ToList();
        if (!string.IsNullOrWhiteSpace(module))
        {
            chosen = chosen.Where(s => string.Equals(s.Module, module.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (chosen.Count == 0)
            {
                throw new InvalidInputException("module",
                    $"No module \"{module}\" can be searched. Modules: {string.Join(", ", sources.Select(s => s.Module).Distinct().Order())}.");
            }
        }

        var results = await Task.WhenAll(chosen.Select(source => RunAsync(source, text, count, cancellationToken)));
        var modules = results.OrderBy(r => r.Order).ThenBy(r => r.Module.Module, StringComparer.Ordinal).Select(r => r.Module).ToList();
        return new SearchDto(text, modules.Sum(m => m.Total), modules);
    }

    private sealed record Result(int Order, SearchModuleDto Module);

    private async Task<Result> RunAsync(ISearchSource source, string text, int limit, CancellationToken requestAborted)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        timeout.CancelAfter(options.Value.SourceTimeout);
        try
        {
            var hits = await source.SearchAsync(text, limit, timeout.Token);
            var own = hits.Hits.Where(hit => hit.Module == source.Module).Take(limit).ToList();
            return new Result(source.Order, new SearchModuleDto(source.Module, SearchModuleStatus.Complete, Math.Max(hits.Total, own.Count), own));
        }
        catch (OperationCanceledException) when (requestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is OperationCanceledException or FinanceRequestException)
        {
            logger.LogWarning("Search: the {Module} source gave no answer: {Message}", source.Module, exception.Message);
            return new Result(source.Order, new SearchModuleDto(source.Module, SearchModuleStatus.Failed, 0, []));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Search: the {Module} source failed", source.Module);
            return new Result(source.Order, new SearchModuleDto(source.Module, SearchModuleStatus.Failed, 0, []));
        }
    }
}
