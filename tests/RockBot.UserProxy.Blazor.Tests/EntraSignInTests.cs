using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using RockBot.UserProxy.Blazor.Auth;

namespace RockBot.UserProxy.Blazor.Tests;

[TestClass]
public class EntraSignInTests
{
    private const string TenantId = "29fb468d-99f0-435b-b75d-89b90137422a";

    private static AuthOptions Options(string tenantId = TenantId, bool clientSecret = true)
    {
        var options = new AuthOptions { Enabled = true };
        options.Providers["Microsoft"] = new OAuthProviderOptions
        {
            ClientId = "client-id",
            ClientSecret = clientSecret ? "client-secret" : "",
            TenantId = tenantId,
        };
        options.AllowedEmails.Add("me@example.com");
        return options;
    }

    [TestMethod]
    public void TenantGuid_IsValid()
    {
        CollectionAssert.AreEqual(Array.Empty<string>(), Options().Validate().ToArray());
    }

    [TestMethod]
    [DataRow("common")]
    [DataRow("organizations")]
    [DataRow("consumers")]
    [DataRow("")]
    [DataRow("example.onmicrosoft.com")]
    public void NonGuidTenant_IsRefused(string tenantId)
    {
        var problems = Options(tenantId).Validate().ToList();

        Assert.AreEqual(1, problems.Count);
        StringAssert.Contains(problems[0], "TenantId must be your Entra tenant's GUID");
    }

    [TestMethod]
    public void ConfiguredMicrosoftProvider_IsAdvertised()
    {
        var registry = new AuthProviderRegistry(Options());

        Assert.AreEqual(1, registry.Enabled.Count);
        Assert.AreEqual(AuthProviderRegistry.MicrosoftScheme, registry.Enabled[0].Scheme);
        Assert.AreEqual("Microsoft", registry.Enabled[0].DisplayName);
    }

    [TestMethod]
    public void HalfConfiguredMicrosoftProvider_IsNotAdvertised()
    {
        Assert.AreEqual(0, new AuthProviderRegistry(Options(clientSecret: false)).Enabled.Count);
    }

    [TestMethod]
    public void EnsureEmail_UsesUpnWhenTokenHasNoEmail()
    {
        var identity = new ClaimsIdentity([new Claim("preferred_username", "simon@example.com")], "oidc");

        EntraClaims.EnsureEmail(identity);

        Assert.AreEqual("simon@example.com", UserAllowlist.GetEmail(new ClaimsPrincipal(identity)));
    }

    [TestMethod]
    public void EnsureEmail_KeepsAnExistingEmail()
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Email, "mail@example.com"), new Claim("preferred_username", "upn@example.com")], "oidc");

        EntraClaims.EnsureEmail(identity);

        Assert.AreEqual(1, identity.FindAll(ClaimTypes.Email).Count());
        Assert.AreEqual("mail@example.com", UserAllowlist.GetEmail(new ClaimsPrincipal(identity)));
    }

    [TestMethod]
    public void EnsureEmail_IgnoresAUpnThatIsNotAnAddress()
    {
        var identity = new ClaimsIdentity([new Claim("preferred_username", "not-an-address")], "oidc");

        EntraClaims.EnsureEmail(identity);

        Assert.IsNull(UserAllowlist.GetEmail(new ClaimsPrincipal(identity)));
    }

    [TestMethod]
    public async Task Challenge_RedirectsToTheTenantsAuthorizeEndpointWithTheHttpsCallback()
    {
        var authorize = $"https://login.microsoftonline.com/{TenantId}/oauth2/v2.0/authorize";
        using var factory = new AuthEndpointSmokeTests.Host(
            new Dictionary<string, string?>
            {
                ["Auth:Enabled"] = "true",
                ["Auth:PublicBaseUrl"] = "https://rockbot.example.com",
                ["Auth:Providers:Microsoft:ClientId"] = "client-id",
                ["Auth:Providers:Microsoft:ClientSecret"] = "client-secret",
                ["Auth:Providers:Microsoft:TenantId"] = TenantId,
                ["Auth:AllowedEmails:0"] = "me@example.com",
            },
            // Static metadata, so the challenge needs no discovery call to Microsoft.
            services => services.Configure<OpenIdConnectOptions>(AuthProviderRegistry.MicrosoftScheme, o =>
                o.Configuration = new OpenIdConnectConfiguration { AuthorizationEndpoint = authorize }));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var login = await (await client.GetAsync("/login")).Content.ReadAsStringAsync();
        StringAssert.Contains(login, "/auth/challenge?provider=Microsoft");

        var response = await client.GetAsync("/auth/challenge?provider=Microsoft&returnUrl=%2F");

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        StringAssert.StartsWith(location, authorize + "?");
        var query = Uri.UnescapeDataString(location);
        StringAssert.Contains(query, "redirect_uri=https://rockbot.example.com/signin-microsoft");
        StringAssert.Contains(query, "client_id=client-id");
        StringAssert.Contains(query, "code_challenge_method=S256");
        StringAssert.Contains(query, "scope=openid profile email");
    }
}
