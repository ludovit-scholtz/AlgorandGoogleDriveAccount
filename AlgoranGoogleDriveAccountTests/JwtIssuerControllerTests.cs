using AlgorandGoogleDriveAccount.BusinessLogic;
using AlgorandGoogleDriveAccount.Controllers;
using AlgorandGoogleDriveAccount.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace AlgoranGoogleDriveAccountTests
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
                var result = await controller.Authorize(ClientId, RedirectUri, null, "code", "query", "openid profile email", "state-1", null);
                Assert.That(result, Is.TypeOf<ChallengeResult>());
            }

            var blockedResult = await controller.Authorize(ClientId, RedirectUri, null, "code", "query", "openid profile email", "state-1", null);

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
                var result = await unauthenticatedController.Authorize(ClientId, RedirectUri, null, "code", "query", "openid profile email", "state-1", null);
                Assert.That(result, Is.TypeOf<ChallengeResult>());
            }

            var authenticatedController = CreateController(jwtIssuerService.Object, cache, authenticated: true);
            var successResult = await authenticatedController.Authorize(ClientId, RedirectUri, null, "code", "query", "openid profile email", "state-1", null);
            Assert.That(successResult, Is.TypeOf<RedirectResult>());

            var nextAttemptController = CreateController(jwtIssuerService.Object, cache, authenticated: false);
            var nextAttemptResult = await nextAttemptController.Authorize(ClientId, RedirectUri, null, "code", "query", "openid profile email", "state-1", null);
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

        private static JwtIssuerController CreateController(IJwtIssuerService jwtIssuerService, IDistributedCache cache, bool authenticated, IConfiguration? configuration = null)
        {
            var controller = new JwtIssuerController(jwtIssuerService, cache);
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Scheme = "https";
            httpContext.Request.Host = new HostString("google.biatec.io");
            httpContext.Request.Headers.UserAgent = "nunit-test-agent";
            httpContext.RequestServices = new ServiceCollection()
                .AddSingleton<IConfiguration>(configuration ?? new ConfigurationBuilder().AddInMemoryCollection().Build())
                .BuildServiceProvider();
            httpContext.User = authenticated
                ? new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Email, "user@example.com") }, "test"))
                : new ClaimsPrincipal(new ClaimsIdentity());

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
