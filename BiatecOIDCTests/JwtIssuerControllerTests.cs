using System.Security.Claims;
using BiatecOIDC.BusinessLogic;
using BiatecOIDC.Controllers;
using BiatecOIDC.Model;
using BiatecSelfCustodyCore.Providers;
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
                var result = await controller.Authorize(ClientId, RedirectUri, null, "code", "query", "openid profile email", "state-1", null, null, null, "google");
                Assert.That(result, Is.TypeOf<ChallengeResult>());
            }

            var blockedResult = await controller.Authorize(ClientId, RedirectUri, null, "code", "query", "openid profile email", "state-1", null, null, null, "google");

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
                .Setup(service => service.CreateAuthorizeResponseAsync(It.IsAny<OidcAuthorizeRequest>(), It.IsAny<JwtIssuerClientConfiguration>(), It.IsAny<ClaimsPrincipal>()))
                .ReturnsAsync((true, null, null, new Dictionary<string, string>
                {
                    ["code"] = "issued-code",
                    ["state"] = "state-1"
                }));

            var unauthenticatedController = CreateController(jwtIssuerService.Object, cache, authenticated: false);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var result = await unauthenticatedController.Authorize(ClientId, RedirectUri, null, "code", "query", "openid profile email", "state-1", null, null, null, "google");
                Assert.That(result, Is.TypeOf<ChallengeResult>());
            }

            var authenticatedController = CreateController(jwtIssuerService.Object, cache, authenticated: true);
            var successResult = await authenticatedController.Authorize(ClientId, RedirectUri, null, "code", "query", "openid profile email", "state-1", null, null, null, "google");
            Assert.That(successResult, Is.TypeOf<RedirectToActionResult>());
            Assert.That(((RedirectToActionResult)successResult).ActionName, Is.EqualTo(nameof(JwtIssuerController.AuthorizeConsent)));

            var nextAttemptController = CreateController(jwtIssuerService.Object, cache, authenticated: false);
            var nextAttemptResult = await nextAttemptController.Authorize(ClientId, RedirectUri, null, "code", "query", "openid profile email", "state-1", null, null, null, "google");
            Assert.That(nextAttemptResult, Is.TypeOf<ChallengeResult>());
        }

        [Test]
        public void EndSession_WhenWildcardPostLogoutRedirectMatches_ReturnsSignOut()
        {
            var cache = new InMemoryDistributedCache();
            var jwtIssuerService = CreateJwtIssuerServiceMock();
            var configuration = BuildJwtIssuerConfiguration(
                new[] { RedirectUri },
                new[] { "https://*.example.com/login" });

            var controller = CreateController(jwtIssuerService.Object, cache, authenticated: true, configuration);

            var result = controller.EndSession(null, "https://tenant-a.example.com/login?redirect=%2F", "state-1", ClientId);

            Assert.That(result, Is.TypeOf<SignOutResult>());
            var signOut = (SignOutResult)result;
            Assert.That(signOut.Properties, Is.Not.Null);
            Assert.That(signOut.Properties!.RedirectUri, Is.EqualTo("https://tenant-a.example.com/login?redirect=%2F&state=state-1"));
        }

        [Test]
        public void EndSession_WhenWildcardPostLogoutRedirectDoesNotMatchRootDomain_ReturnsBadRequest()
        {
            var cache = new InMemoryDistributedCache();
            var jwtIssuerService = CreateJwtIssuerServiceMock();
            var configuration = BuildJwtIssuerConfiguration(
                new[] { RedirectUri },
                new[] { "https://*.example.com/login" });

            var controller = CreateController(jwtIssuerService.Object, cache, authenticated: true, configuration);

            var result = controller.EndSession(null, "https://example.com/login", null, ClientId);

            Assert.That(result, Is.TypeOf<BadRequestObjectResult>());
            var badRequest = (BadRequestObjectResult)result;
            Assert.That(badRequest.Value, Is.TypeOf<ProblemDetails>());
            var problem = (ProblemDetails)badRequest.Value!;
            Assert.That(problem.Detail, Does.Contain("not allowlisted"));
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
            // Identity + storage + sign + limits all granted - four granted rows, zero denied.
            Assert.That(CountOccurrences(html, "permission-icon granted"), Is.EqualTo(4));
            Assert.That(html, Does.Not.Contain("permission-icon denied"));
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
            // Identity + storage are granted; sign + limits are both denied since neither was requested.
            Assert.That(CountOccurrences(html, "permission-icon granted"), Is.EqualTo(2));
            Assert.That(CountOccurrences(html, "permission-icon denied"), Is.EqualTo(2));
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
            var jwtIssuerService = CreateJwtIssuerServiceMock();
            jwtIssuerService
                .Setup(service => service.PeekPendingAuthorizeRequestAsync("request-id"))
                .ReturnsAsync(new OidcAuthorizeRequest { ClientId = ClientId, Scope = "openid profile email" });
            var configuration = BuildJwtIssuerConfigurationWithDisplayName("Capitalism 5");
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
            var jwtIssuerService = CreateJwtIssuerServiceMock();
            jwtIssuerService
                .Setup(service => service.PeekPendingAuthorizeRequestAsync("request-id"))
                .ReturnsAsync(new OidcAuthorizeRequest { ClientId = ClientId, Scope = "openid sign manage-limits" });
            var configuration = BuildJwtIssuerConfigurationWithDisplayName("Capitalism 5");
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
                .Setup(service => service.CreateAuthorizeResponseAsync(It.IsAny<OidcAuthorizeRequest>(), It.IsAny<JwtIssuerClientConfiguration>(), It.IsAny<ClaimsPrincipal>()))
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

        private static Mock<IJwtIssuerService> CreateJwtIssuerServiceMock()
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
            var providerCatalog = new CloudStorageProviderCatalog(new ICloudStorageProvider[] { new FakeCloudStorageProvider() });
            var controller = new JwtIssuerController(jwtIssuerService, cache, providerCatalog);
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
            public string Name => "Google";
            public string DisplayName => "Google";
            public string RequiredScope => "fake-scope";
            public bool IsConfigured => true;
            public Task<byte[]?> TryDownloadAsync(string fileName, string accessToken) => Task.FromResult<byte[]?>(null);
            public Task UploadAsync(string fileName, byte[] content, string accessToken) => Task.CompletedTask;
            public Task<bool> HasWriteAccessAsync(string accessToken) => Task.FromResult(true);
            public Task<string?> GetAmbientAccessTokenAsync() => Task.FromResult<string?>(null);
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
