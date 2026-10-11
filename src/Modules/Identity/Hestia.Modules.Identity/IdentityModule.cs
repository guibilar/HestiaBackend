using System.Security.Claims;
using Hestia.Modules.Identity.Application;
using Hestia.Modules.Identity.Endpoints;
using Hestia.Modules.Identity.Infrastructure;
using Hestia.Platform.Auth;
using Hestia.Platform.Modules;
using Hestia.Platform.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Modules.Identity;

/// <summary>
/// The Identity module: sign-in for the whole host (D5, T03). It sets the authentication and the default authorization
/// of every endpoint: a signed-in user is necessary, and a read-only user can only read. Other modules use the normal
/// ASP.NET Core user (<see cref="HestiaUser"/>).
/// </summary>
public sealed class IdentityModule : IModule
{
    public string Name => "Identity";

    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddIdentityModule(configuration);

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapIdentityModule();
}

public static class IdentityModuleExtensions
{
    /// <summary>The application name of the data protection keys (the cookie encryption).</summary>
    public const string DataProtectionApplicationName = "Hestia";

    /// <summary>A request with a bearer token uses the read-only token; any other request uses the session cookie.</summary>
    private const string SessionOrTokenScheme = "SessionOrToken";

    /// <summary>Registers everything the Identity module needs, with one call.</summary>
    public static IServiceCollection AddIdentityModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<IdentityDbContext>(IdentityDbContext.SchemaName);

        services.AddOptions<IdentityModuleOptions>()
            .Bind(configuration.GetSection(IdentityModuleOptions.SectionName))
            .Validate(options => options.IsValid(),
                $"{IdentityModuleOptions.SectionName}:ReadOnlyToken must be at least {IdentityModuleOptions.MinimumTokenLength} characters.")
            .ValidateOnStart();

        services.AddSignInGuard();
        services.AddHostedService<InitialUser>();
        services.AddSingleton<IModuleCommand, FinanceUserImport>();

        var authentication = services.AddAuthentication(SessionOrTokenScheme);
        authentication.AddIdentityCookies();
        authentication.AddScheme<AuthenticationSchemeOptions, ReadOnlyTokenHandler>(ReadOnlyTokenHandler.SchemeName, _ => { });
        authentication.AddPolicyScheme(SessionOrTokenScheme, null, options => options.ForwardDefaultSelector = context =>
            ReadOnlyTokenHandler.HasBearerToken(context.Request) ? ReadOnlyTokenHandler.SchemeName : IdentityConstants.ApplicationScheme);

        services.AddIdentityCore<IdentityUser>(options =>
            {
                // Length over composition rules (NIST SP 800-63B).
                options.Password.RequiredLength = 12;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Lockout.MaxFailedAccessAttempts = SignInGuard.MaxFailedAttempts;
                options.Lockout.DefaultLockoutTimeSpan = SignInGuard.LockoutTime;
            })
            .AddEntityFrameworkStores<IdentityDbContext>()
            .AddSignInManager()
            // For the password reset of Identity:InitialUser:ResetPassword.
            .AddDefaultTokenProviders();

        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = HestiaUser.SessionCookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.ExpireTimeSpan = TimeSpan.FromDays(14);
            options.SlidingExpiration = true;
            // An API answers with status codes; the clients show their own sign-in page.
            options.Events.OnRedirectToLogin = context => StatusCode(context.Response, StatusCodes.Status401Unauthorized);
            options.Events.OnRedirectToAccessDenied = context => StatusCode(context.Response, StatusCodes.Status403Forbidden);
        });

        // Every 30 minutes, Identity checks the security stamp and makes a new signed-in user, without the session key
        // that the store added. Copy the key to the new user, or "sign out other devices" and a password change would
        // take this session for another one and end it too.
        services.Configure<SecurityStampValidatorOptions>(options => options.OnRefreshingPrincipal = context =>
        {
            var key = context.CurrentPrincipal?.FindFirst(DbTicketStore.SessionKeyClaim);
            if (key is not null && context.NewPrincipal?.Identity is ClaimsIdentity identity && !identity.HasClaim(c => c.Type == key.Type))
            {
                identity.AddClaim(new Claim(key.Type, key.Value));
            }
            return Task.CompletedTask;
        });

        // Sessions are in the database, so sign-out, a password change, or "sign out other devices" really ends them.
        // The cookie carries only the session key.
        services.AddSingleton<DbTicketStore>();
        services.AddOptions<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme)
            .Configure<DbTicketStore>((options, store) => options.SessionStore = store);

        services.AddDataProtection()
            .SetApplicationName(DataProtectionApplicationName)
            .PersistKeysToDbContext<IdentityDbContext>();

        // Every endpoint needs a signed-in user, unless it allows anonymous users (sign-in, health checks).
        // A read-only user can only read (HestiaUser.MayProceed).
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireAssertion(HestiaUser.MayProceed)
                .Build());

        return services;
    }

    /// <summary>Maps the sign-in endpoints under /api/Auth.</summary>
    public static IEndpointRouteBuilder MapIdentityModule(this IEndpointRouteBuilder endpoints)
    {
        AuthEndpoints.Map(endpoints);
        return endpoints;
    }

    private static Task StatusCode(HttpResponse response, int statusCode)
    {
        response.StatusCode = statusCode;
        return Task.CompletedTask;
    }
}
