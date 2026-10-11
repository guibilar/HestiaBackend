namespace Hestia.Contracts;

/// <summary>
/// Contracts that Hestia shares with other processes, for example the finance service.
/// T04 adds the finance events here, and T08 adds the Today card contract.
/// </summary>
public static class HestiaContracts
{
    /// <summary>The assembly that holds the shared contracts.</summary>
    public static System.Reflection.Assembly Assembly => typeof(HestiaContracts).Assembly;
}
