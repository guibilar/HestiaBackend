using System.Security.Claims;

namespace Hestia.Modules.Today.Contracts;

/// <summary>
/// The cards of one module on Today. A module that shows cards implements it, and registers it in its
/// <c>AddServices</c> as a scoped service: <c>services.AddScoped&lt;ITodayCardProvider, DocumentsTodayCards&gt;()</c>.
/// The Today module calls all providers in parallel for each <c>GET /api/today</c>, inside the HTTP request of the user.
/// </summary>
/// <remarks>
/// A provider that throws, or does not answer in time (<c>Today:ProviderTimeout</c>), gives no cards; the other
/// modules still show theirs. A provider that loads several things can return the cards that it has, with
/// <see cref="TodayModuleCards.Complete"/> false.
/// </remarks>
public interface ITodayCardProvider
{
    /// <summary>The module name, for example <c>Documents</c>. The same in every card.</summary>
    string Module { get; }

    /// <summary>The place of the module on Today: lower first. Finance (Money) is 0 (D11). Other modules use 100 or more.</summary>
    int Order { get; }

    /// <summary>The cards for <paramref name="context"/>: tiles and "Needs attention" items, in display order.</summary>
    Task<TodayModuleCards> GetCardsAsync(TodayContext context, CancellationToken cancellationToken);
}

/// <param name="Date">The day of the Today screen, in the time zone of the host (<c>Today:TimeZone</c>).</param>
/// <param name="TimeZone">That time zone, to change a moment (UTC) to a local day.</param>
/// <param name="User">The signed-in user of the request. A read-only user can only read.</param>
public sealed record TodayContext(DateOnly Date, TimeZoneInfo TimeZone, ClaimsPrincipal User);

/// <param name="Cards">The cards of the module.</param>
/// <param name="Complete">False when some data did not load, so some cards are missing or have no value.</param>
public sealed record TodayModuleCards(IReadOnlyList<TodayCard> Cards, bool Complete = true);
