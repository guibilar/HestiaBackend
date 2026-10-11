using Microsoft.AspNetCore.Builder;

namespace Hestia.Platform.Auth;

/// <summary>
/// Marks an endpoint that only reads, but does not use GET (for example a search with its criteria in the body).
/// A read-only user (the read-only token) can call it. See <see cref="HestiaUser.MayProceed"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class ReadOnlyQueryAttribute : Attribute;

public static class ReadOnlyQueryExtensions
{
    /// <summary>Marks a minimal API endpoint as a read-only query (<see cref="ReadOnlyQueryAttribute"/>).</summary>
    public static TBuilder ReadOnlyQuery<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new ReadOnlyQueryAttribute());
}
