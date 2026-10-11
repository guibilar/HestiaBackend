namespace Hestia.Platform;

public static class Probe
{
    public static string Name => Hestia.Contracts.HestiaContracts.Assembly.FullName ?? "";
}
