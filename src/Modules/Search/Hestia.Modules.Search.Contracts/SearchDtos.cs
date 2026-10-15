namespace Hestia.Modules.Search.Contracts;

/// <summary>The answer of <c>GET /api/search</c>: the hits of each module that has some, in module order.</summary>
/// <param name="Query">The text that was searched (trimmed).</param>
/// <param name="Total">The matches of all modules (can be more than the hits shown).</param>
/// <param name="Modules">One entry for each module, with hits or not, so a client can tell "no match" from "failed".</param>
public sealed record SearchDto(string Query, int Total, IReadOnlyList<SearchModuleDto> Modules);

/// <summary>The hits of one module.</summary>
/// <param name="Total">All matches of the module; more than <see cref="Hits"/> when there are more to show.</param>
public sealed record SearchModuleDto(string Module, SearchModuleStatus Status, int Total, IReadOnlyList<SearchHit> Hits);

public enum SearchModuleStatus
{
    /// <summary>The module searched.</summary>
    Complete,

    /// <summary>The module gave no hits: its source failed or did not answer in time.</summary>
    Failed
}
