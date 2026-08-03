using System.Security.Claims;
using BiatecOIDC.BusinessLogic;
using BiatecOIDC.Controllers;
using BiatecOIDC.Model;
using BiatecSelfCustodyCore.Providers;
using BiatecSelfCustodyCore.Repository;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BiatecOIDCTests
{
    [TestFixture]
    public class JwtIssuerControllerTests
    {
        private const string ClientId = "capitalism";
        private const string RedirectUri = "http://localhost:5173/auth/callback";

        [Test]
        public async Task Authorize_WhenAttemptsExceedLimit_ReturnsRetryBlockError()
        {
            var cache = new InMemoryDistributedCache();
            var jwtIssuerService = CreateJwtIssuerServiceMock();
            jwtIssuerService
                .Setup(service => service.StorePendingAuthorizeRequestAsync(It.IsAny<OidcAuthorizeRequest>()))
                .ReturnsAsync("request-id");

            var controller = CreateController(jwtIssuerService.Object, cache, authenticated: false);

            for (var attempt = 0; attempt < 3; attempt++)
            {
                var result = await controller.Authorize(ClientId, RedirectUri, null, "code", "query", "openid profile email", "state-1", null, null, null, null, "google");
                Assert.That(result, Is.TypeOf<ChallengeResult>());
            }

            var blockedResult = await controller.Authorize(ClientId, RedirectUri, null, "code", "query", "openid profile email", "state-1", null, null, null, null, "google");

            Assert.That(blockedResult, Is.TypeOf<RedirectResult>());
            var redirect = (RedirectResult)blockedResult;
            Assert.That(redirect.Url, Does.Contain("error=temporarily_unavailable"));
            Assert.That(redirect.Url, Does.Contain("error_description=Too%20many%20authorization%20attempts"));
            jwtIssuerService.Verify(service => service.StorePendingAuthorizeRequestAsync(It.IsAny<OidcAuthorizeRequest>()), Times.Exactly(3));
        }

        [Test]
        public async Task Authorize_WhenSuccessClearsAttempts_AllowsNextLogin()
        {
            var cache = new InMemoryDistributedCache();
            var jwtIssuerService = CreateJwtIssuerServiceMock();
            jwtIssuerService
                .Setup(service => service.StorePendingAuthorizeRequestAsync(It.IsAny<OidcAuthorizeRequest>()))
                .ReturnsAsync("request-id");
            jwtIssuerService
                .Setup(service => service.CreateAuthorizeResponseAsync(It.IsAny<OidcAuthorizeRequest>(), It.IsAny<JwtIssuerClientConfiguration>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync((true, null, null, new Dictionary<string, string>
                {
                    ["code"] = "issued-code",
                    ["state"] = "state-1"
                }));

            var unauthenticatedController = CreateController(jwtIssuerService.Object, cache, authenticated: false);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var result = await unauthenticatedController.Authorize(ClientId, RedirectUri, null, "code", "query", "openid profile email", "state-1", null, null, null, null, "google");
                Assert.That(result, Is.TypeOf<ChallengeResult>());
            }

            var authenticatedController = CreateController(jwtIssuerService.Object, cache, authenticated: true);
            var successResult = await authenticatedController.Authorize(ClientId, RedirectUri, null, "code", "query", "openid profile email", "state-1", null, null, null, null, "google");
            Assert.That(successResult, Is.TypeOf<RedirectToActionResult>());
            Assert.That(((RedirectToActionResult)successResult).ActionName, Is.EqualTo(nameof(JwtIssuerController.AuthorizeConsent)));

            var nextAttemptController = CreateController(jwtIssuerService.Object, cache, authenticated: false);
            var nextAttemptResult = await nextAttemptController.Authorize(ClientId, RedirectUri, null, "code", "query", "openid profile email", "state-1", null, null, null, null, "google");
            Assert.That(nextAttemptResult, Is.TypeOf<ChallengeResult>());
        }

        [Test]
        public async Task EndSession_WhenWildcardPostLogoutRedirectMatches_ReturnsSignOut()
        {
            var cache = new InMemoryDistributedCache();
            var configuration = BuildJwtIssuerConfiguration(
                new[] { RedirectUri },
                new[] { "https://*.example.com/login" });
            var jwtIssuerService = CreateJwtIssuerServiceMock(configuration);

            var controller = CreateController(jwtIssuerService.Object, cache, authenticated: true, configuration);

            var result = await controller.EndSession(null, "https://tenant-a.example.com/login?redirect=%2F", "state-1", ClientId);

            Assert.That(result, Is.TypeOf<SignOutResult>());
            var signOut = (SignOutResult)result;
            Assert.That(signOut.Properties, Is.Not.Null);
            Assert.That(signOut.Properties!.RedirectUri, Is.EqualTo("https://tenant-a.example.com/login?redirect=%2F&state=state-1"));
        }

        [Test]
        public async Task EndSession_WhenWildcardPostLogoutRedirectDoesNotMatchRootDomain_ReturnsBadRequest()
        {
            var cache = new InMemoryDistributedCache();
            var configuration = BuildJwtIssuerConfiguration(
                new[] { RedirectUri },
                new[] { "https://*.example.com/login" });
            var jwtIssuerService = CreateJwtIssuerServiceMock(configuration);

            var controller = CreateController(jwtIssuerService.Object, cache, authenticated: true, configuration);

            var result = await controller.EndSession(null, "https://example.com/login", null, ClientId);

            Assert.That(result, Is.TypeOf<BadRequestObjectResult>());
            var badRequest = (BadRequestObjectResult)result;
            Assert.That(badRequest.Value, Is.TypeOf<ProblemDetails>());
            var problem = (ProblemDetails)badRequest.Value!;
            Assert.That(problem.Detail, Does.Contain("not allowlisted"));
        }

        [Test]
        public async Task Register_ValidRequest_Returns201WithPublicClient()
        {
            var jwtIssuerService = CreateJwtIssuerServiceMock();
            var newClient = new JwtIssuerClientConfiguration { ClientId = "new-dyn-client", RedirectUris = { "http://127.0.0.1:5000/cb" }, AllowedScopes = { "openid", "sign" } };
            jwtIssuerService
                .Setup(s => s.RegisterDynamicClientAsync("My MCP Client", It.Is<List<string>>(l => l.Contains("http://127.0.0.1:5000/cb")), "openid sign"))
                .ReturnsAsync(newClient);
            var controller = CreateController(jwtIssuerService.Object, new InMemoryDistributedCache(), authenticated: false);

            var result = await controller.Register(new DynamicClientRegistrationRequest
            {
                ClientName = "My MCP Client",
                RedirectUris = new List<string> { "http://127.0.0.1:5000/cb" },
                Scope = "openid sign"
            });

            Assert.That(result, Is.TypeOf<ObjectResult>());
            var objectResult = (ObjectResult)result;
            Assert.That(objectResult.StatusCode, Is.EqualTo(201));
            var response = (DynamicClientRegistrationResponse)objectResult.Value!;
            Assert.That(response.ClientId, Is.EqualTo("new-dyn-client"));
            Assert.That(response.TokenEndpointAuthMethod, Is.EqualTo("none"));
        }

        [Test]
        public async Task Register_RequestsConfidentialAuthMethod_ReturnsBadRequest()
        {
            var jwtIssuerService = CreateJwtIssuerServiceMock();
            var controller = CreateController(jwtIssuerService.Object, new InMemoryDistributedCache(), authenticated: false);

            var result = await controller.Register(new DynamicClientRegistrationRequest
            {
                RedirectUris = new List<string> { "https://app.example.com/cb" },
                TokenEndpointAuthMethod = "client_secret_post"
            });

            Assert.That(result, Is.TypeOf<BadRequestObjectResult>());
            jwtIssuerService.Verify(s => s.RegisterDynamicClientAsync(It.IsAny<string?>(), It.IsAny<List<string>>(), It.IsAny<string?>()), Times.Never);
        }

        [Test]
        public async Task Register_InvalidRedirectUri_ReturnsBadRequest()
        {
            var jwtIssuerService = CreateJwtIssuerServiceMock();
            jwtIssuerService
                .Setup(s => s.RegisterDynamicClientAsync(It.IsAny<string?>(), It.IsAny<List<string>>(), It.IsAny<string?>()))
                .ThrowsAsync(new ArgumentException("redirect_uri 'http://evil.example.com' must be HTTPS, or a loopback HTTP URI if allowed."));
            var controller = CreateController(jwtIssuerService.Object, new InMemoryDistributedCache(), authenticated: false);

            var result = await controller.Register(new DynamicClientRegistrationRequest
            {
                RedirectUris = new List<string> { "http://evil.example.com" }
            });

            Assert.That(result, Is.TypeOf<BadRequestObjectResult>());
        }

        [Test]
        public void OAuthAuthorizationServerMetadata_ReturnsSameDocumentAsOpenIdConfiguration()
        {
            // RFC 8414 OAuth 2.0 Authorization Server Metadata - some OAuth/MCP clients (e.g. VS Code's MCP
            // client) probe this URL before falling back to OIDC Discovery
            // (.well-known/openid-configuration). The MCP Authorization spec only requires an authorization
            // server to implement *one* of the two discovery mechanisms, and a spec-compliant client falls
            // back to OIDC discovery if this 404s - but serving both removes the "Failed to fetch
            // authorization server metadata... 404" warning entirely and is more broadly compatible with
            // pure-OAuth (non-OIDC-aware) clients that only ever try this one.
            var jwtIssuerService = CreateJwtIssuerServiceMock();
            var expectedDocument = new { issuer = "https://stage.oidc.biatec.io" };
            jwtIssuerService.Setup(s => s.GetDiscoveryDocument(It.IsAny<HttpRequest>())).Returns(expectedDocument);
            var controller = CreateController(jwtIssuerService.Object, new InMemoryDistributedCache(), authenticated: false);

            var result = controller.OAuthAuthorizationServerMetadata();

            Assert.That(result, Is.TypeOf<OkObjectResult>());
            Assert.That(((OkObjectResult)result).Value, Is.SameAs(expectedDocument));
        }

        [Test]
        public async Task AuthorizeConsent_MissingRequestId_ReturnsBadRequest()
        {
            var jwtIssuerService = CreateJwtIssuerServiceMock();
            var controller = CreateController(jwtIssuerService.Object, new InMemoryDistributedCache(), authenticated: true);

            var result = await controller.AuthorizeConsent(string.Empty);

            Assert.That(result, Is.TypeOf<BadRequestObjectResult>());
        }

        [Test]
        public async Task AuthorizeConsent_UnknownRequestId_ReturnsBadRequest()
        {
            var jwtIssuerService = CreateJwtIssuerServiceMock();
            jwtIssuerService.Setup(service => service.PeekPendingAuthorizeRequestAsync("missing")).ReturnsAsync((OidcAuthorizeRequest?)null);
            var controller = CreateController(jwtIssuerService.Object, new InMemoryDistributedCache(), authenticated: true);

            var result = await controller.AuthorizeConsent("missing");

            Assert.That(result, Is.TypeOf<BadRequestObjectResult>());
        }

        [Test]
        public async Task AuthorizeConsent_ScopeRequestsSignAndLimits_ShowsGrantedRowsAndRequiresConfirmation()
        {
            var jwtIssuerService = CreateJwtIssuerServiceMock();
            jwtIssuerService
                .Setup(service => service.PeekPendingAuthorizeRequestAsync("request-id"))
                .ReturnsAsync(new OidcAuthorizeRequest { ClientId = ClientId, Scope = "openid profile email sign manage-limits" });
            // Storage access already granted, so this isolates the sign/manage-limits confirmation behavior.
            var controller = CreateController(jwtIssuerService.Object, new InMemoryDistributedCache(), authenticated: true, providerAccessToken: "google-access-token");

            var result = await controller.AuthorizeConsent("request-id");

            Assert.That(result, Is.TypeOf<ContentResult>());
            var html = ((ContentResult)result).Content!;
            // Identity + storage + sign + limits granted - four granted rows; rekey wasn't requested, so
            // it's the sole denied row.
            Assert.That(CountOccurrences(html, "permission-icon granted"), Is.EqualTo(4));
            Assert.That(CountOccurrences(html, "permission-icon denied"), Is.EqualTo(1));
            Assert.That(html, Does.Contain("Confirm &amp; Continue"));
            // Sensitive scopes requested - must not auto-continue without the user clicking through.
            Assert.That(html, Does.Not.Contain("setInterval"));
        }

        [Test]
        public async Task AuthorizeConsent_ScopeWithoutSignOrLimits_ShowsDeniedRowsAndAutoContinues()
        {
            var jwtIssuerService = CreateJwtIssuerServiceMock();
            jwtIssuerService
                .Setup(service => service.PeekPendingAuthorizeRequestAsync("request-id"))
                .ReturnsAsync(new OidcAuthorizeRequest { ClientId = ClientId, Scope = "openid profile email" });
            // Storage access already granted - the only thing left to decide is sign/manage-limits, which
            // weren't requested, so this should be the safe auto-continuing screen.
            var controller = CreateController(jwtIssuerService.Object, new InMemoryDistributedCache(), authenticated: true, providerAccessToken: "google-access-token");

            var result = await controller.AuthorizeConsent("request-id");

            Assert.That(result, Is.TypeOf<ContentResult>());
            var html = ((ContentResult)result).Content!;
            // Identity + storage are granted; sign, limits, and rekey are all denied since none was requested.
            Assert.That(CountOccurrences(html, "permission-icon granted"), Is.EqualTo(2));
            Assert.That(CountOccurrences(html, "permission-icon denied"), Is.EqualTo(3));
            Assert.That(html, Does.Not.Contain("Confirm &amp; Continue"));
            // No sensitive scopes requested and storage already works - safe to auto-continue.
            Assert.That(html, Does.Contain("setInterval"));
        }

        [Test]
        public async Task AuthorizeConsent_StorageAccessMissing_ShowsReAuthLinkAndRequiresConfirmation()
        {
            var jwtIssuerService = CreateJwtIssuerServiceMock();
            jwtIssuerService
                .Setup(service => service.PeekPendingAuthorizeRequestAsync("request-id"))
                .ReturnsAsync(new OidcAuthorizeRequest { ClientId = ClientId, Scope = "openid profile email" });
            // No provider token at all (default), so HasWriteAccessAsync is never even reached - storage
            // access is treated as missing, same as AuthorizeCallback's own check.
            var controller = CreateController(jwtIssuerService.Object, new InMemoryDistributedCache(), authenticated: true);

            var result = await controller.AuthorizeConsent("request-id");

            Assert.That(result, Is.TypeOf<ContentResult>());
            var html = ((ContentResult)result).Content!;
            Assert.That(html, Does.Contain("storage access is missing"));
            Assert.That(html, Does.Contain("Grant Google access"));
            Assert.That(html, Does.Contain("Continue without storage access"));
            // Missing storage access must not auto-continue either, even with no other scopes requested.
            Assert.That(html, Does.Not.Contain("setInterval"));
        }

        [Test]
        public async Task AuthorizeConsent_ClientHasDisplayName_ShowsDisplayNameInsteadOfClientId()
        {
            var configuration = BuildJwtIssuerConfigurationWithDisplayName("Capitalism 5");
            var jwtIssuerService = CreateJwtIssuerServiceMock(configuration);
            jwtIssuerService
                .Setup(service => service.PeekPendingAuthorizeRequestAsync("request-id"))
                .ReturnsAsync(new OidcAuthorizeRequest { ClientId = ClientId, Scope = "openid profile email" });
            var controller = CreateController(jwtIssuerService.Object, new InMemoryDistributedCache(), authenticated: true, configuration, providerAccessToken: "google-access-token");

            var result = await controller.AuthorizeConsent("request-id");

            var html = ((ContentResult)result).Content!;
            Assert.That(html, Does.Contain("Capitalism 5"));
        }

        [Test]
        public async Task AuthorizeConsent_ClientHasNoDisplayName_FallsBackToClientId()
        {
            var jwtIssuerService = CreateJwtIssuerServiceMock();
            jwtIssuerService
                .Setup(service => service.PeekPendingAuthorizeRequestAsync("request-id"))
                .ReturnsAsync(new OidcAuthorizeRequest { ClientId = ClientId, Scope = "openid profile email" });
            // No IConfiguration client registration at all (default empty config) - DisplayName can't be
            // resolved, so the raw ClientId is the only reasonable fallback.
            var controller = CreateController(jwtIssuerService.Object, new InMemoryDistributedCache(), authenticated: true, providerAccessToken: "google-access-token");

            var result = await controller.AuthorizeConsent("request-id");

            var html = ((ContentResult)result).Content!;
            Assert.That(html, Does.Contain(ClientId));
        }

        [Test]
        public async Task SelectProvider_ClientHasDisplayName_ShowsDisplayNameAndRequestedScopes()
        {
            var configuration = BuildJwtIssuerConfigurationWithDisplayName("Capitalism 5");
            var jwtIssuerService = CreateJwtIssuerServiceMock(configuration);
            jwtIssuerService
                .Setup(service => service.PeekPendingAuthorizeRequestAsync("request-id"))
                .ReturnsAsync(new OidcAuthorizeRequest { ClientId = ClientId, Scope = "openid sign manage-limits" });
            var controller = CreateController(jwtIssuerService.Object, new InMemoryDistributedCache(), authenticated: false, configuration);

            var result = await controller.SelectProvider("request-id");

            Assert.That(result, Is.TypeOf<ContentResult>());
            var html = ((ContentResult)result).Content!;
            Assert.That(html, Does.Contain("Capitalism 5"));
            Assert.That(html, Does.Contain("Verify your identity"));
            Assert.That(html, Does.Contain("Sign transactions on your behalf"));
            Assert.That(html, Does.Contain("Change your spending limit"));
        }

        [Test]
        public async Task SelectProvider_NoPendingRequest_StillRendersPickerWithoutScopesSection()
        {
            var jwtIssuerService = CreateJwtIssuerServiceMock();
            jwtIssuerService
                .Setup(service => service.PeekPendingAuthorizeRequestAsync("missing"))
                .ReturnsAsync((OidcAuthorizeRequest?)null);
            var controller = CreateController(jwtIssuerService.Object, new InMemoryDistributedCache(), authenticated: false);

            var result = await controller.SelectProvider("missing");

            Assert.That(result, Is.TypeOf<ContentResult>());
            var html = ((ContentResult)result).Content!;
            Assert.That(html, Does.Not.Contain("is requesting:"));
        }

        [Test]
        public async Task AuthorizeConsentContinue_ValidRequest_FinalizesAuthorization()
        {
            var jwtIssuerService = CreateJwtIssuerServiceMock();
            jwtIssuerService
                .Setup(service => service.GetPendingAuthorizeRequestAsync("request-id"))
                .ReturnsAsync(new OidcAuthorizeRequest { ClientId = ClientId, RedirectUri = RedirectUri, ResponseMode = "query", State = "state-1", Scope = "openid profile email" });
            jwtIssuerService
                .Setup(service => service.CreateAuthorizeResponseAsync(It.IsAny<OidcAuthorizeRequest>(), It.IsAny<JwtIssuerClientConfiguration>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync((true, null, null, new Dictionary<string, string>
                {
                    ["code"] = "issued-code",
                    ["state"] = "state-1"
                }));
            var controller = CreateController(jwtIssuerService.Object, new InMemoryDistributedCache(), authenticated: true);

            var result = await controller.AuthorizeConsentContinue("request-id");

            Assert.That(result, Is.TypeOf<RedirectResult>());
            var redirect = (RedirectResult)result;
            Assert.That(redirect.Url, Does.Contain("code=issued-code"));
        }

        [Test]
        public async Task AuthorizeConsentContinue_AmbientProviderTokenAvailable_PassesItToCreateAuthorizeResponseAsync()
        {
            var jwtIssuerService = CreateJwtIssuerServiceMock();
            jwtIssuerService
                .Setup(service => service.GetPendingAuthorizeRequestAsync("request-id"))
                .ReturnsAsync(new OidcAuthorizeRequest { ClientId = ClientId, RedirectUri = RedirectUri, ResponseMode = "query", State = "state-1", Scope = "openid profile email" });
            jwtIssuerService
                .Setup(service => service.CreateAuthorizeResponseAsync(It.IsAny<OidcAuthorizeRequest>(), It.IsAny<JwtIssuerClientConfiguration>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync((true, null, null, new Dictionary<string, string> { ["code"] = "issued-code", ["state"] = "state-1" }));
            var controller = CreateController(jwtIssuerService.Object, new InMemoryDistributedCache(), authenticated: true, providerAccessToken: "live-google-token");

            await controller.AuthorizeConsentContinue("request-id");

            jwtIssuerService.Verify(service => service.CreateAuthorizeResponseAsync(
                It.IsAny<OidcAuthorizeRequest>(), It.IsAny<JwtIssuerClientConfiguration>(), It.IsAny<ClaimsPrincipal>(), "live-google-token", It.IsAny<string?>()), Times.Once);
        }

        [Test]
        public async Task AuthorizeConsentContinue_NoAmbientProviderToken_PassesNullToCreateAuthorizeResponseAsync()
        {
            var jwtIssuerService = CreateJwtIssuerServiceMock();
            jwtIssuerService
                .Setup(service => service.GetPendingAuthorizeRequestAsync("request-id"))
                .ReturnsAsync(new OidcAuthorizeRequest { ClientId = ClientId, RedirectUri = RedirectUri, ResponseMode = "query", State = "state-1", Scope = "openid profile email" });
            jwtIssuerService
                .Setup(service => service.CreateAuthorizeResponseAsync(It.IsAny<OidcAuthorizeRequest>(), It.IsAny<JwtIssuerClientConfiguration>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync((true, null, null, new Dictionary<string, string> { ["code"] = "issued-code", ["state"] = "state-1" }));
            var controller = CreateController(jwtIssuerService.Object, new InMemoryDistributedCache(), authenticated: true);

            await controller.AuthorizeConsentContinue("request-id");

            jwtIssuerService.Verify(service => service.CreateAuthorizeResponseAsync(
                It.IsAny<OidcAuthorizeRequest>(), It.IsAny<JwtIssuerClientConfiguration>(), It.IsAny<ClaimsPrincipal>(), null, It.IsAny<string?>()), Times.Once);
        }

        [Test]
        public async Task AuthorizeConsentContinue_UnknownRequestId_ReturnsBadRequest()
        {
            var jwtIssuerService = CreateJwtIssuerServiceMock();
            jwtIssuerService.Setup(service => service.GetPendingAuthorizeRequestAsync("missing")).ReturnsAsync((OidcAuthorizeRequest?)null);
            var controller = CreateController(jwtIssuerService.Object, new InMemoryDistributedCache(), authenticated: true);

            var result = await controller.AuthorizeConsentContinue("missing");

            Assert.That(result, Is.TypeOf<BadRequestObjectResult>());
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            var count = 0;
            var index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }

            return count;
        }

        private static Mock<IJwtIssuerService> CreateJwtIssuerServiceMock(IConfiguration? configuration = null)
        {
            var mock = new Mock<IJwtIssuerService>();
            mock
                .Setup(service => service.ValidateAuthorizeRequestAsync(It.IsAny<OidcAuthorizeRequest>()))
                .ReturnsAsync((OidcAuthorizeRequest request) =>
                {
                    var normalizedRequest = new OidcAuthorizeRequest
                    {
                        ClientId = request.ClientId,
                        RedirectUri = request.RedirectUri,
                        ReturnUrl = request.ReturnUrl,
                        ResponseType = request.ResponseType,
                        ResponseMode = request.ResponseMode,
                        Scope = request.Scope,
                        State = request.State,
                        Nonce = request.Nonce
                    };

                    var client = new JwtIssuerClientConfiguration
                    {
                        ClientId = request.ClientId ?? ClientId,
                        RedirectUris = new List<string> { request.RedirectUri ?? RedirectUri },
                        AllowedScopes = new List<string> { "openid", "profile", "email" }
                    };

                    return (true, null, null, normalizedRequest, client);
                });

            // Mirrors JwtIssuerService.ResolveClientAsync (real implementation, tested separately in
            // JwtIssuerServiceTests) for controller-level tests: resolves from the optional configuration
            // (built by BuildJwtIssuerConfiguration[WithDisplayName], same "JwtIssuer:Clients:0:..." shape
            // the real config-bound JwtIssuerConfiguration uses) when the id matches, else falls back to a
            // default client synthesized from the id - so tests that never configure a client explicitly
            // (e.g. AuthorizeConsent_ClientHasNoDisplayName_FallsBackToClientId) still get a resolvable
            // client with no DisplayName, exactly like an unconfigured real client would.
            var configuredClient = configuration?.GetSection("JwtIssuer:Clients:0").Get<JwtIssuerClientConfiguration>();
            mock
                .Setup(service => service.ResolveClientAsync(It.IsAny<string?>()))
                .ReturnsAsync((string? clientId) =>
                {
                    if (string.IsNullOrWhiteSpace(clientId))
                    {
                        return null;
                    }

                    if (configuredClient != null && string.Equals(configuredClient.ClientId, clientId, StringComparison.Ordinal))
                    {
                        return configuredClient;
                    }

                    return new JwtIssuerClientConfiguration
                    {
                        ClientId = clientId,
                        RedirectUris = new List<string> { RedirectUri },
                        AllowedScopes = new List<string> { "openid", "profile", "email" }
                    };
                });

            return mock;
        }

        private static IConfiguration BuildJwtIssuerConfiguration(IEnumerable<string> redirectUris, IEnumerable<string> postLogoutRedirectUris)
        {
            var values = new Dictionary<string, string?>
            {
                ["JwtIssuer:Clients:0:ClientId"] = ClientId
            };

            var redirectUriIndex = 0;
            foreach (var redirectUri in redirectUris)
            {
                values[$"JwtIssuer:Clients:0:RedirectUris:{redirectUriIndex++}"] = redirectUri;
            }

            var postLogoutUriIndex = 0;
            foreach (var postLogoutRedirectUri in postLogoutRedirectUris)
            {
                values[$"JwtIssuer:Clients:0:PostLogoutRedirectUris:{postLogoutUriIndex++}"] = postLogoutRedirectUri;
            }

            return new ConfigurationBuilder()
                .AddInMemoryCollection(values)
                .Build();
        }

        private static IConfiguration BuildJwtIssuerConfigurationWithDisplayName(string displayName)
        {
            return new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["JwtIssuer:Clients:0:ClientId"] = ClientId,
                    ["JwtIssuer:Clients:0:DisplayName"] = displayName
                })
                .Build();
        }

        private static JwtIssuerController CreateController(IJwtIssuerService jwtIssuerService, IDistributedCache cache, bool authenticated, IConfiguration? configuration = null, string? providerAccessToken = null)
        {
            // Shared with HttpContext.GetTokenAsync's stubbed value below (see the AuthenticationService
            // setup further down) - FinalizeAuthorizeAsync resolves the ambient token via
            // ICloudStorageProvider.GetAmbientAccessTokenAsync() (for the freshest possible token, see its
            // own comment), while AuthorizeConsent/AuthorizeCallback use the plain
            // HttpContext.GetTokenAsync - both need to observe the same providerAccessToken in tests.
            var providerCatalog = new CloudStorageProviderCatalog(new ICloudStorageProvider[] { new FakeCloudStorageProvider(providerAccessToken) });
            var accountRepository = Mock.Of<ICloudAccountRepository>();
            var mockConfig = Mock.Of<Microsoft.Extensions.Options.IOptionsMonitor<MockCloudServiceConfiguration>>(m => m.CurrentValue == new MockCloudServiceConfiguration());
            var controller = new JwtIssuerController(jwtIssuerService, cache, providerCatalog, accountRepository, mockConfig);
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Scheme = "https";
            httpContext.Request.Host = new HostString("google.biatec.io");
            httpContext.Request.Headers.UserAgent = "nunit-test-agent";

            // AuthorizeConsent calls HttpContext.GetTokenAsync(provider.Name, "access_token"), which needs
            // a real IAuthenticationService - stub one that either has no result (no provider token, the
            // default) or returns providerAccessToken via AuthenticationProperties.StoreTokens, mirroring
            // what SaveTokens=true actually persists on a real signed-in cookie.
            var principal = authenticated
                ? new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Email, "user@example.com") }, "test"))
                : new ClaimsPrincipal(new ClaimsIdentity());
            var authService = new Mock<IAuthenticationService>();
            if (providerAccessToken != null)
            {
                var properties = new AuthenticationProperties();
                properties.StoreTokens(new[] { new AuthenticationToken { Name = "access_token", Value = providerAccessToken } });
                var ticket = new AuthenticationTicket(principal, properties, "Google");
                authService.Setup(s => s.AuthenticateAsync(It.IsAny<HttpContext>(), It.IsAny<string>())).ReturnsAsync(AuthenticateResult.Success(ticket));
            }
            else
            {
                authService.Setup(s => s.AuthenticateAsync(It.IsAny<HttpContext>(), It.IsAny<string>())).ReturnsAsync(AuthenticateResult.NoResult());
            }

            httpContext.RequestServices = new ServiceCollection()
                .AddSingleton<IConfiguration>(configuration ?? new ConfigurationBuilder().AddInMemoryCollection().Build())
                .AddSingleton(authService.Object)
                .BuildServiceProvider();
            httpContext.User = principal;

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            };

            var urlHelper = new Mock<IUrlHelper>();
            urlHelper
                .Setup(helper => helper.Action(It.IsAny<UrlActionContext>()))
                .Returns("https://google.biatec.io/authorize/callback?requestId=request-id");
            controller.Url = urlHelper.Object;

            return controller;
        }

        /// <summary>Minimal test double standing in for the real Google/Microsoft providers, which need heavier dependencies to construct.</summary>
        private sealed class FakeCloudStorageProvider : ICloudStorageProvider
        {
            private readonly string? _ambientAccessToken;
            private readonly string? _ambientRefreshToken;

            public FakeCloudStorageProvider(string? ambientAccessToken = null, string? ambientRefreshToken = null)
            {
                _ambientAccessToken = ambientAccessToken;
                _ambientRefreshToken = ambientRefreshToken;
            }

            public string Name => "Google";
            public string DisplayName => "Google";
            public string RequiredScope => "fake-scope";
            public bool IsConfigured => true;
            public Task<byte[]?> TryDownloadAsync(string fileName, string accessToken) => Task.FromResult<byte[]?>(null);
            public Task UploadAsync(string fileName, byte[] content, string accessToken) => Task.CompletedTask;
            public Task DeleteAsync(string fileName, string accessToken) => Task.CompletedTask;
            public Task<bool> HasWriteAccessAsync(string accessToken) => Task.FromResult(true);
            public Task<string?> GetAmbientAccessTokenAsync() => Task.FromResult(_ambientAccessToken);
            public Task<string?> GetAmbientRefreshTokenAsync() => Task.FromResult(_ambientRefreshToken);
            public Task<ProviderTokenRefreshResult?> RefreshAccessTokenAsync(string refreshToken) => Task.FromResult<ProviderTokenRefreshResult?>(null);
            public string BuildAuthorizationUrl(string redirectUri, string state) => $"https://example.invalid/authorize?redirect_uri={redirectUri}&state={state}";
            public Task<string?> ExchangeAuthorizationCodeAsync(string code, string redirectUri) => Task.FromResult<string?>(null);
        }

        private sealed class InMemoryDistributedCache : IDistributedCache
        {
            private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);

            public byte[]? Get(string key)
            {
                _values.TryGetValue(key, out var value);
                return value;
            }

            public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
            {
                return Task.FromResult(Get(key));
            }

            public void Refresh(string key)
            {
            }

            public Task RefreshAsync(string key, CancellationToken token = default)
            {
                return Task.CompletedTask;
            }

            public void Remove(string key)
            {
                _values.Remove(key);
            }

            public Task RemoveAsync(string key, CancellationToken token = default)
            {
                Remove(key);
                return Task.CompletedTask;
            }

            public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
            {
                _values[key] = value;
            }

            public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
            {
                Set(key, value, options);
                return Task.CompletedTask;
            }
        }
    }
}
