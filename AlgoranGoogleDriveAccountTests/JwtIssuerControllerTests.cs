using AlgorandGoogleDriveAccount.BusinessLogic;
using AlgorandGoogleDriveAccount.Controllers;
using AlgorandGoogleDriveAccount.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Caching.Distributed;
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

        private static JwtIssuerController CreateController(IJwtIssuerService jwtIssuerService, IDistributedCache cache, bool authenticated)
        {
            var controller = new JwtIssuerController(jwtIssuerService, cache);
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Scheme = "https";
            httpContext.Request.Host = new HostString("google.biatec.io");
            httpContext.Request.Headers.UserAgent = "nunit-test-agent";
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
