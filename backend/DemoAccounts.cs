using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using ClaimIdentityValue = System.Security.Claims.Claim;

namespace Claims.Api;

public sealed record DemoUser(string Id, string Name, string Role)
{
    public ClaimsPrincipal ToPrincipal() => new(new ClaimsIdentity(
        [new ClaimIdentityValue(ClaimTypes.NameIdentifier, Id), new ClaimIdentityValue(ClaimTypes.Name, Name),
            new ClaimIdentityValue(ClaimTypes.Role, Role)], CookieAuthenticationDefaults.AuthenticationScheme));
}

public static class DemoAccounts
{
    public const string Claimant = "claimant";
    public const string Officer = "officer";

    private const string Password = "claims-demo";
    private static readonly DemoUser[] Users =
    [
        new("claimant-a", "Amina Hassan", Claimant),
        new("claimant-b", "Daniel Lim", Claimant),
        new("officer-a", "Maya Tan", Officer),
        new("officer-b", "Amir Rahman", Officer)
    ];

    public static DemoUser? Authenticate(string? account, string? password) =>
        password == Password ? Users.SingleOrDefault(user => user.Id == account) : null;

    public static DemoUser FromPrincipal(ClaimsPrincipal principal) =>
        Users.Single(user => user.Id == principal.FindFirstValue(ClaimTypes.NameIdentifier));
}

public sealed record SignInRequest(string? Account, string? Password);
