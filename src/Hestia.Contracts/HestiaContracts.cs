namespace Hestia.Contracts;

/// <summary>
/// Contracts that Hestia shares with other processes: the finance events (namespace <c>Hestia.Contracts.Finance</c>),
/// and later the Today card contract (T08).
/// </summary>
public static class HestiaContracts
{
    /// <summary>The assembly that holds the shared contracts.</summary>
    public static System.Reflection.Assembly Assembly => typeof(HestiaContracts).Assembly;
}
