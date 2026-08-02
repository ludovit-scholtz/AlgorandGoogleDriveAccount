using BiatecOIDC.Model;
using Moq;

namespace BiatecOIDCTests
{
    // =========================================================================
    // POST /register (RFC 7591 Dynamic Client Registration) - service-level behavior.
    // =========================================================================
    [TestFixture]
    public class DynamicClientRegistrationTests : JwtIssuerServiceTestBase
    {
        [Test]
        public async Task RegisterDynamicClientAsync_ValidRequest_PersistsPublicClientWithNoSecret()
        {
            JwtIssuerClientConfiguration? saved = null;
            MockDynamicClientStore
                .Setup(s => s.SaveAsync(It.IsAny<JwtIssuerClientConfiguration>()))
                .Callback<JwtIssuerClientConfiguration>(c => saved = c)
                .Returns(Task.CompletedTask);

            var client = await Service.RegisterDynamicClientAsync("Claude Desktop", new List<string> { "http://127.0.0.1:33445/callback" }, "openid sign");

            Assert.That(client.ClientSecret, Is.Null.Or.Empty);
            Assert.That(client.IsPublicClient, Is.True);
            Assert.That(client.ClientId, Is.Not.Null.And.Not.Empty);
            Assert.That(saved, Is.Not.Null);
            Assert.That(saved!.ClientId, Is.EqualTo(client.ClientId));
        }

        [Test]
        public async Task RegisterDynamicClientAsync_RequestedManageLimitsAndRekeyScopes_AreCappedToDefaultScopes()
        {
            DefaultConfig.DynamicClientRegistrationDefaultScopes = new List<string> { "openid", "profile", "email", "sign" };

            var client = await Service.RegisterDynamicClientAsync("Some Client", new List<string> { "https://app.example.com/callback" }, "openid sign manage-limits rekey");

            Assert.That(client.AllowedScopes, Is.EquivalentTo(new[] { "openid", "profile", "email", "sign" }));
            Assert.That(client.AllowedScopes, Does.Not.Contain("manage-limits"));
            Assert.That(client.AllowedScopes, Does.Not.Contain("rekey"));
        }

        [Test]
        public void RegisterDynamicClientAsync_NoRedirectUris_ThrowsArgumentException()
        {
            Assert.That(
                async () => await Service.RegisterDynamicClientAsync("Some Client", new List<string>(), "openid"),
                Throws.ArgumentException);
        }

        [TestCase("http://evil.example.com/callback")] // non-loopback plain HTTP
        [TestCase("ftp://127.0.0.1/callback")] // wrong scheme entirely
        [TestCase("https://app.example.com/callback#fragment")] // fragment not allowed
        public void RegisterDynamicClientAsync_DisallowedRedirectUri_ThrowsArgumentException(string redirectUri)
        {
            Assert.That(
                async () => await Service.RegisterDynamicClientAsync("Some Client", new List<string> { redirectUri }, "openid"),
                Throws.ArgumentException);
        }

        [Test]
        public async Task RegisterDynamicClientAsync_LoopbackHttpRedirectUri_IsAllowed()
        {
            DefaultConfig.AllowHttpForLoopbackRedirectUris = true;

            var client = await Service.RegisterDynamicClientAsync("Native App", new List<string> { "http://localhost:54321/cb" }, "openid");

            Assert.That(client.RedirectUris, Does.Contain("http://localhost:54321/cb"));
        }

        [Test]
        public async Task ResolveClientAsync_StaticConfigClient_ReturnsStaticEntry_WithoutConsultingDynamicStore()
        {
            var resolved = await Service.ResolveClientAsync(TestClientId);

            Assert.That(resolved, Is.Not.Null);
            Assert.That(resolved!.ClientId, Is.EqualTo(TestClientId));
            MockDynamicClientStore.Verify(s => s.GetAsync(It.IsAny<string>()), Times.Never);
        }

        [Test]
        public async Task ResolveClientAsync_UnknownStaticId_FallsBackToDynamicStore()
        {
            var dynamicClient = new JwtIssuerClientConfiguration { ClientId = "dyn-123", RedirectUris = { "https://app.example.com/cb" } };
            MockDynamicClientStore.Setup(s => s.GetAsync("dyn-123")).ReturnsAsync(dynamicClient);

            var resolved = await Service.ResolveClientAsync("dyn-123");

            Assert.That(resolved, Is.SameAs(dynamicClient));
        }

        [Test]
        public async Task ResolveClientAsync_StaticEntryTakesPrecedenceOverDynamicEntryWithSameId()
        {
            // An operator "upgrading" a dynamically-registered client by adding a static entry with the
            // same ClientId must win - this is how a DCR'd client gets hand-granted higher-privilege scopes.
            var dynamicClient = new JwtIssuerClientConfiguration { ClientId = TestClientId, RedirectUris = { "https://should-not-be-used.example.com/cb" } };
            MockDynamicClientStore.Setup(s => s.GetAsync(TestClientId)).ReturnsAsync(dynamicClient);

            var resolved = await Service.ResolveClientAsync(TestClientId);

            Assert.That(resolved!.RedirectUris, Is.EqualTo(new List<string> { TestRedirectUri }));
        }

        [Test]
        public async Task ResolveClientAsync_NullOrWhitespaceId_ReturnsNullWithoutConsultingAnyStore()
        {
            var resolved = await Service.ResolveClientAsync(null);

            Assert.That(resolved, Is.Null);
            MockDynamicClientStore.Verify(s => s.GetAsync(It.IsAny<string>()), Times.Never);
        }
    }

    // =========================================================================
    // RFC 8707 resource indicator handling on /authorize and /token - aud binding for resource servers
    // (like BiatecMCP) that accept tokens from many dynamically-registered clients.
    // =========================================================================
    [TestFixture]
    public class ResourceIndicatorTests : JwtIssuerServiceTestBase
    {
        private const string McpResource = "https://mcp.biatec.io/mcp";

        [SetUp]
        public override void SetUp()
        {
            base.SetUp();
            DefaultConfig.ProtectedResources = new List<string> { McpResource };
        }

        [Test]
        public async Task ValidateAuthorizeRequestAsync_AllowlistedResource_Succeeds()
        {
            var request = ValidCodeRequest();
            request.Resource = McpResource;

            var result = await Service.ValidateAuthorizeRequestAsync(request);

            Assert.That(result.IsValid, Is.True);
            Assert.That(result.NormalizedRequest!.Resource, Is.EqualTo(McpResource));
        }

        [Test]
        public async Task ValidateAuthorizeRequestAsync_NonAllowlistedResource_ReturnsInvalidTarget()
        {
            var request = ValidCodeRequest();
            request.Resource = "https://not-registered.example.com/mcp";

            var result = await Service.ValidateAuthorizeRequestAsync(request);

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Error, Is.EqualTo("invalid_target"));
        }

        [Test]
        public async Task ValidateAuthorizeRequestAsync_NoResourceRequested_StillSucceeds_ExistingBehaviorUnaffected()
        {
            var request = ValidCodeRequest();
            request.Resource = null;

            var result = await Service.ValidateAuthorizeRequestAsync(request);

            Assert.That(result.IsValid, Is.True);
            Assert.That(result.NormalizedRequest!.Resource, Is.Null.Or.Empty);
        }

        [Test]
        public async Task ExchangeTokenAsync_ResourceMatchesAuthorization_AccessTokenAudienceIncludesBothClientIdAndResource()
        {
            var code = "resource-code";
            SetupCacheGet("oidc:code:" + code, BuildCodeRecordJson(code, TestClientId, TestRedirectUri, resource: McpResource));

            var tokenRequest = new OidcTokenRequest
            {
                GrantType = "authorization_code",
                Code = code,
                RedirectUri = TestRedirectUri,
                ClientId = TestClientId,
                ClientSecret = TestClientSecret,
                Resource = McpResource
            };

            var result = await Service.ExchangeTokenAsync(tokenRequest, null);

            Assert.That(result.Success, Is.True);
            var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
            var jwt = handler.ReadJwtToken(result.Response!.AccessToken);
            Assert.That(jwt.Audiences, Does.Contain(TestClientId));
            Assert.That(jwt.Audiences, Does.Contain(McpResource));
        }

        [Test]
        public async Task ExchangeTokenAsync_NoResourceOnEitherSide_AudienceIsClientIdOnly_ExistingBehaviorUnaffected()
        {
            var code = "no-resource-code";
            SetupCacheGet("oidc:code:" + code, BuildCodeRecordJson(code, TestClientId, TestRedirectUri));

            var tokenRequest = new OidcTokenRequest
            {
                GrantType = "authorization_code",
                Code = code,
                RedirectUri = TestRedirectUri,
                ClientId = TestClientId,
                ClientSecret = TestClientSecret
            };

            var result = await Service.ExchangeTokenAsync(tokenRequest, null);

            Assert.That(result.Success, Is.True);
            var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
            var jwt = handler.ReadJwtToken(result.Response!.AccessToken);
            Assert.That(jwt.Audiences, Is.EquivalentTo(new[] { TestClientId }));
        }

        [Test]
        public async Task ExchangeTokenAsync_ResourceMismatchBetweenAuthorizeAndToken_ReturnsInvalidTarget()
        {
            var code = "mismatched-resource-code";
            SetupCacheGet("oidc:code:" + code, BuildCodeRecordJson(code, TestClientId, TestRedirectUri, resource: McpResource));

            var tokenRequest = new OidcTokenRequest
            {
                GrantType = "authorization_code",
                Code = code,
                RedirectUri = TestRedirectUri,
                ClientId = TestClientId,
                ClientSecret = TestClientSecret,
                Resource = "https://a-different-resource.example.com"
            };

            var result = await Service.ExchangeTokenAsync(tokenRequest, null);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Is.EqualTo("invalid_target"));
        }

        [Test]
        public async Task ValidateBearerAccessToken_TokenWithResourceInAudience_StillValidatesAgainstClientIdAllowlist()
        {
            // Existing endpoints (/userinfo, /introspect, /verify) validate ValidAudiences = registered
            // client ids - confirms adding the resource to `aud` doesn't break that pre-existing check.
            var code = "resource-code-2";
            SetupCacheGet("oidc:code:" + code, BuildCodeRecordJson(code, TestClientId, TestRedirectUri, resource: McpResource));
            var tokenRequest = new OidcTokenRequest
            {
                GrantType = "authorization_code",
                Code = code,
                RedirectUri = TestRedirectUri,
                ClientId = TestClientId,
                ClientSecret = TestClientSecret,
                Resource = McpResource
            };
            var tokenResult = await Service.ExchangeTokenAsync(tokenRequest, null);

            var validation = Service.ValidateBearerAccessToken(tokenResult.Response!.AccessToken);

            Assert.That(validation.IsValid, Is.True);
            Assert.That(validation.Claims, Is.Not.Null);
        }
    }
}
