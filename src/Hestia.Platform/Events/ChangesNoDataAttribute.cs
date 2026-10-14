namespace Hestia.Platform.Events;

/// <summary>
/// Marks a consumer method that changes no data, so it needs no inbox (T52). For example a method that only asks for a
/// check in the background: a second delivery gives the same result.
/// </summary>
/// <remarks>
/// A test checks every consumer method in the modules: it must use an inbox (<c>ModuleDbContext.TryReceiveAsync</c>, or an
/// <c>AlertTrigger</c> for the Alerts module), or have this attribute. Do not put it on a method that saves anything.
/// </remarks>
/// <param name="reason">Why the method changes no data, for the reader.</param>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ChangesNoDataAttribute(string reason) : Attribute
{
    public string Reason { get; } = reason;
}
