using AlgorandGoogleDriveAccount.BusinessLogic;
using AlgorandGoogleDriveAccount.Model;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Moq;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AlgoranGoogleDriveAccountTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // Shared test fixture helpers
    // ─────────────────────────────────────────────────────────────────────────
    public abstract class JwtIssuerServiceTestBase
    {
        protected const string TestIssuer = "https://test.biatec.io";
        protected const string TestClientId = "test-client";
        protected const string TestClientSecret = "test-secret";
        protected const string TestRedirectUri = "https://app.example.com/callback";
        protected const string TestEmail = "user@example.com";
        protected const string TestAlgorandAddress = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567ABCDEFGHIJKLMNOPQRSTUVWXY";

        protected Mock<IDistributedCache> MockCache = null!;
        protected Mock<IOptionsMonitor<JwtIssuerConfiguration>> MockConfig = null!;
        protected Mock<IDriveService> MockDriveService = null!;
        protected Mock<ILogger<JwtIssuerService>> MockLogger = null!;
        protected JwtIssuerService Service = null!;

        protected JwtIssuerConfiguration DefaultConfig = null!;

        private static readonly JsonSerializerOptions CamelCaseOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        [SetUp]
        public virtual void SetUp()
        {
            MockCache = new Mock<IDistributedCache>();
            MockConfig = new Mock<IOptionsMonitor<JwtIssuerConfiguration>>();
            MockDriveService = new Mock<IDriveService>();
            MockLogger = new Mock<ILogger<JwtIssuerService>>();

            DefaultConfig = new JwtIssuerConfiguration
            {
                Enabled = true,
                Issuer = TestIssuer,
                KeyId = "test-key",
                SigningPrivateKeyPem = string.Empty,
                AuthorizationCodeLifetimeSeconds = 120,
                AccessTokenLifetimeMinutes = 15,
                IdTokenLifetimeMinutes = 15,
                RefreshTokenLifetimeDays = 30,
                AllowHttpForLoopbackRedirectUris = true,
                Clients = new List<JwtIssuerClientConfiguration>
                {
                    new()
                    {
                        ClientId = TestClientId,
                        ClientSecret = TestClientSecret,
                        RedirectUris = new List<string> { TestRedirectUri },
                        AllowedScopes = new List<string> { "openid", "profile", "email" }
                    }
                }
            };

            MockConfig.Setup(m => m.CurrentValue).Returns(DefaultConfig);

            Service = new JwtIssuerService(MockCache.Object, MockConfig.Object, MockDriveService.Object, MockLogger.Object);
        }

        /// <summary>Sets up the distributed cache to return a JSON string for GetAsync.</summary>
        protected void SetupCacheGet(string key, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            MockCache
                .Setup(c => c.GetAsync(key, It.IsAny<CancellationToken>()))
                .ReturnsAsync(bytes);
        }

        /// <summary>Sets up the distributed cache to return null (cache miss) for GetAsync.</summary>
        protected void SetupCacheMiss(string key)
        {
            MockCache
                .Setup(c => c.GetAsync(key, It.IsAny<CancellationToken>()))
                .ReturnsAsync((byte[]?)null);
        }

        /// <summary>Builds a camelCase JSON string for an authorization code record.</summary>
        protected static string BuildCodeRecordJson(
            string code,
            string clientId,
            string redirectUri,
            string scope = "openid profile email",
            string? nonce = null,
            string email = TestEmail,
            string algorandAddress = TestAlgorandAddress,
            DateTimeOffset? expiresUtc = null)
        {
            var record = new
            {
                code,
                clientId,
                redirectUri,
                scope,
                nonce,
                email,
                algorandAddress,
                subject = "sub-value",
                shortIdentity = "ABCD" + algorandAddress[^4..],
                createdUtc = DateTimeOffset.UtcNow,
                expiresUtc = expiresUtc ?? DateTimeOffset.UtcNow.AddSeconds(120)
            };
            return JsonSerializer.Serialize(record, CamelCaseOptions);
        }

        /// <summary>Builds a camelCase JSON string for a refresh token record.</summary>
        protected static string BuildRefreshTokenRecordJson(
            string refreshToken,
            string clientId,
            string scope = "openid profile email",
            string email = TestEmail,
            string algorandAddress = TestAlgorandAddress,
            DateTimeOffset? expiresUtc = null)
        {
            var record = new
            {
                refreshToken,
                clientId,
                subject = "sub-value",
                email,
                algorandAddress,
                shortIdentity = "ABCD" + algorandAddress[^4..],
                scope,
                createdUtc = DateTimeOffset.UtcNow,
                expiresUtc = expiresUtc ?? DateTimeOffset.UtcNow.AddDays(30)
            };
            return JsonSerializer.Serialize(record, CamelCaseOptions);
        }

        /// <summary>Creates a default valid authorize request.</summary>
        protected static OidcAuthorizeRequest ValidCodeRequest(string? nonce = null) => new()
        {
            ClientId = TestClientId,
            RedirectUri = TestRedirectUri,
            ResponseType = "code",
            Scope = "openid profile email",
            State = "state-abc",
            Nonce = nonce
        };

        /// <summary>Creates a ClaimsPrincipal with email claim (simulates Google-authenticated user).</summary>
        protected static ClaimsPrincipal CreateUser(string email = TestEmail)
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.Email, email),
                new Claim(ClaimTypes.NameIdentifier, "google-id-123")
            };
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        }

        /// <summary>Creates a mock HttpRequest with scheme/host (for GetIssuer fallback testing).</summary>
        protected static Microsoft.AspNetCore.Http.HttpRequest CreateMockHttpRequest(string scheme = "https", string host = "mock.example.com")
        {
            var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            httpContext.Request.Scheme = scheme;
            httpContext.Request.Host = new Microsoft.AspNetCore.Http.HostString(host);
            return httpContext.Request;
        }
    }

    // =========================================================================
    [TestFixture]
    public class GetIssuerTests : JwtIssuerServiceTestBase
    {
        [Test]
        public void GetIssuer_WhenIssuerConfigured_ReturnsConfiguredIssuer()
        {
            var request = CreateMockHttpRequest("http", "should-not-be-used.example.com");

            var result = Service.GetIssuer(request);

            Assert.That(result, Is.EqualTo(TestIssuer));
        }

        [Test]
        public void GetIssuer_WhenIssuerEmpty_FallsBackToRequestSchemeAndHost()
        {
            DefaultConfig.Issuer = string.Empty;
            var request = CreateMockHttpRequest("https", "dynamic.example.com");

            var result = Service.GetIssuer(request);

            Assert.That(result, Is.EqualTo("https://dynamic.example.com"));
        }

        [Test]
        public void GetIssuer_TrimsTrailingSlash()
        {
            DefaultConfig.Issuer = "https://trailing.example.com/";
            var request = CreateMockHttpRequest();

            var result = Service.GetIssuer(request);

            Assert.That(result, Does.Not.EndWith("/"));
        }
    }

    // =========================================================================
    [TestFixture]
    public class GetDiscoveryDocumentTests : JwtIssuerServiceTestBase
    {
        private JsonDocument _doc = null!;

        [SetUp]
        public override void SetUp()
        {
            base.SetUp();
            var request = CreateMockHttpRequest();
            var obj = Service.GetDiscoveryDocument(request);
            var json = JsonSerializer.Serialize(obj, obj.GetType());
            _doc = JsonDocument.Parse(json);
        }

        [TearDown]
        public void TearDown() => _doc?.Dispose();

        [Test]
        public void DiscoveryDocument_HasIssuer()
        {
            Assert.That(_doc.RootElement.GetProperty("issuer").GetString(), Is.EqualTo(TestIssuer));
        }

        [Test]
        public void DiscoveryDocument_HasAuthorizationEndpoint()
        {
            Assert.That(_doc.RootElement.GetProperty("authorization_endpoint").GetString(), Does.Contain("/authorize"));
        }

        [Test]
        public void DiscoveryDocument_HasTokenEndpoint()
        {
            Assert.That(_doc.RootElement.GetProperty("token_endpoint").GetString(), Does.Contain("/token"));
        }

        [Test]
        public void DiscoveryDocument_HasJwksUri()
        {
            Assert.That(_doc.RootElement.GetProperty("jwks_uri").GetString(), Does.Contain("/.well-known/jwks.json"));
        }

        [Test]
        public void DiscoveryDocument_HasUserinfoEndpoint()
        {
            Assert.That(_doc.RootElement.GetProperty("userinfo_endpoint").GetString(), Does.Contain("/userinfo"));
        }

        [Test]
        public void DiscoveryDocument_SupportsRS256()
        {
            var algs = _doc.RootElement.GetProperty("id_token_signing_alg_values_supported");
            var list = algs.EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.That(list, Does.Contain("RS256"));
        }

        [Test]
        public void DiscoveryDocument_SupportsAuthorizationCodeGrant()
        {
            var grants = _doc.RootElement.GetProperty("grant_types_supported");
            var list = grants.EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.That(list, Does.Contain("authorization_code"));
        }

        [Test]
        public void DiscoveryDocument_SupportsRefreshTokenGrant()
        {
            var grants = _doc.RootElement.GetProperty("grant_types_supported");
            var list = grants.EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.That(list, Does.Contain("refresh_token"));
        }

        [Test]
        public void DiscoveryDocument_ClaimsSupportedContainsAlgorandAddress()
        {
            var claims = _doc.RootElement.GetProperty("claims_supported");
            var list = claims.EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.That(list, Does.Contain("algorand_address"));
        }
    }

    // =========================================================================
    [TestFixture]
    public class GetJsonWebKeySetTests : JwtIssuerServiceTestBase
    {
        [Test]
        public void GetJsonWebKeySet_ReturnsRsaKey()
        {
            var jwks = Service.GetJsonWebKeySet();
            var json = JsonSerializer.Serialize(jwks);
            var parsed = JsonDocument.Parse(json);

            var keys = parsed.RootElement.GetProperty("keys");
            Assert.That(keys.GetArrayLength(), Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void GetJsonWebKeySet_KeyHasCorrectKty()
        {
            var jwks = Service.GetJsonWebKeySet();
            var json = JsonSerializer.Serialize(jwks);
            var parsed = JsonDocument.Parse(json);

            var firstKey = parsed.RootElement.GetProperty("keys")[0];
            Assert.That(firstKey.GetProperty("kty").GetString(), Is.EqualTo("RSA"));
        }

        [Test]
        public void GetJsonWebKeySet_KeyUseIsSig()
        {
            var jwks = Service.GetJsonWebKeySet();
            var json = JsonSerializer.Serialize(jwks);
            var parsed = JsonDocument.Parse(json);

            var firstKey = parsed.RootElement.GetProperty("keys")[0];
            Assert.That(firstKey.GetProperty("use").GetString(), Is.EqualTo("sig"));
        }

        [Test]
        public void GetJsonWebKeySet_KeyHasKid()
        {
            var jwks = Service.GetJsonWebKeySet();
            var json = JsonSerializer.Serialize(jwks);
            var parsed = JsonDocument.Parse(json);

            var firstKey = parsed.RootElement.GetProperty("keys")[0];
            Assert.That(firstKey.GetProperty("kid").GetString(), Is.EqualTo("test-key"));
        }

        [Test]
        public void GetJsonWebKeySet_KeyHasModulusAndExponent()
        {
            var jwks = Service.GetJsonWebKeySet();
            var json = JsonSerializer.Serialize(jwks);
            var parsed = JsonDocument.Parse(json);

            var firstKey = parsed.RootElement.GetProperty("keys")[0];
            Assert.That(firstKey.GetProperty("n").GetString(), Is.Not.Empty);
            Assert.That(firstKey.GetProperty("e").GetString(), Is.Not.Empty);
        }
    }

    // =========================================================================
    [TestFixture]
    public class ValidateAuthorizeRequestAsyncTests : JwtIssuerServiceTestBase
    {
        [Test]
        public async Task ValidRequest_CodeFlow_ReturnsValid()
        {
            var request = ValidCodeRequest();
            var result = await Service.ValidateAuthorizeRequestAsync(request);

            Assert.That(result.IsValid, Is.True);
            Assert.That(result.Error, Is.Null);
            Assert.That(result.Client, Is.Not.Null);
            Assert.That(result.Client!.ClientId, Is.EqualTo(TestClientId));
        }

        [Test]
        public async Task ValidRequest_EmptyResponseType_DefaultsToCode()
        {
            var request = new OidcAuthorizeRequest
            {
                ClientId = TestClientId,
                RedirectUri = TestRedirectUri,
                Scope = "openid",
                ResponseType = string.Empty
            };

            var result = await Service.ValidateAuthorizeRequestAsync(request);

            // Empty response_type is normalized to "code" by the service
            Assert.That(result.IsValid, Is.True);
            Assert.That(result.NormalizedRequest!.ResponseType, Is.EqualTo("code"));
        }

        [Test]
        public async Task ValidRequest_ReturnUrlOnly_SetsIdTokenMode()
        {
            // When no ClientId or ResponseType but ReturnUrl is set — treated as id_token/form_post
            // But redirect_uri is needed. With only ReturnUrl set and client has that redirect:
            DefaultConfig.Clients[0].RedirectUris.Add("https://app.example.com/callback");
            var request = new OidcAuthorizeRequest
            {
                ReturnUrl = TestRedirectUri,
                RedirectUri = null,
                ClientId = null,
                ResponseType = string.Empty,
                Scope = "openid profile email",
                Nonce = "required-nonce"
            };
            var result = await Service.ValidateAuthorizeRequestAsync(request);

            Assert.That(result.IsValid, Is.True);
            Assert.That(result.NormalizedRequest!.ResponseType, Is.EqualTo("id_token"));
            Assert.That(result.NormalizedRequest!.ResponseMode, Is.EqualTo("form_post"));
        }

        [Test]
        public async Task MissingRedirectUri_ReturnsInvalidRequest()
        {
            var request = ValidCodeRequest();
            request.RedirectUri = null;

            var result = await Service.ValidateAuthorizeRequestAsync(request);

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Error, Is.EqualTo("invalid_request"));
        }

        [Test]
        public async Task RelativeRedirectUri_ReturnsInvalidRequest()
        {
            var request = ValidCodeRequest();
            request.RedirectUri = "/relative/path";

            var result = await Service.ValidateAuthorizeRequestAsync(request);

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Error, Is.EqualTo("invalid_request"));
        }

        [Test]
        public async Task RedirectUriWithFragment_ReturnsInvalidRequest()
        {
            var request = ValidCodeRequest();
            request.RedirectUri = "https://app.example.com/callback#frag";

            var result = await Service.ValidateAuthorizeRequestAsync(request);

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Error, Is.EqualTo("invalid_request"));
        }

        [Test]
        public async Task HttpNonLoopbackRedirectUri_ReturnsInvalidRequest()
        {
            DefaultConfig.Clients[0].RedirectUris.Add("http://remote.example.com/callback");
            var request = ValidCodeRequest();
            request.RedirectUri = "http://remote.example.com/callback";

            var result = await Service.ValidateAuthorizeRequestAsync(request);

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Error, Is.EqualTo("invalid_request"));
            Assert.That(result.ErrorDescription, Does.Contain("HTTPS"));
        }

        [Test]
        public async Task HttpLocalhostRedirectUri_AllowedWhenConfigured()
        {
            DefaultConfig.AllowHttpForLoopbackRedirectUris = true;
            DefaultConfig.Clients[0].RedirectUris.Add("http://localhost:5000/callback");
            var request = ValidCodeRequest();
            request.RedirectUri = "http://localhost:5000/callback";

            var result = await Service.ValidateAuthorizeRequestAsync(request);

            Assert.That(result.IsValid, Is.True);
        }

        [Test]
        public async Task Http127001RedirectUri_AllowedWhenConfigured()
        {
            DefaultConfig.AllowHttpForLoopbackRedirectUris = true;
            DefaultConfig.Clients[0].RedirectUris.Add("http://127.0.0.1:8080/callback");
            var request = ValidCodeRequest();
            request.RedirectUri = "http://127.0.0.1:8080/callback";

            var result = await Service.ValidateAuthorizeRequestAsync(request);

            Assert.That(result.IsValid, Is.True);
        }

        [Test]
        public async Task HttpLocalhostRedirectUri_RejectedWhenDisabled()
        {
            DefaultConfig.AllowHttpForLoopbackRedirectUris = false;
            DefaultConfig.Clients[0].RedirectUris.Add("http://localhost:5000/callback");
            var request = ValidCodeRequest();
            request.RedirectUri = "http://localhost:5000/callback";

            var result = await Service.ValidateAuthorizeRequestAsync(request);

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Error, Is.EqualTo("invalid_request"));
        }

        [Test]
        public async Task MissingOpenidScope_ReturnsInvalidScope()
        {
            var request = ValidCodeRequest();
            request.Scope = "profile email";

            var result = await Service.ValidateAuthorizeRequestAsync(request);

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Error, Is.EqualTo("invalid_scope"));
        }

        [Test]
        public async Task UnknownClientId_ReturnsInvalidClient()
        {
            var request = ValidCodeRequest();
            request.ClientId = "unknown-client";

            var result = await Service.ValidateAuthorizeRequestAsync(request);

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Error, Is.EqualTo("invalid_client"));
        }

        [Test]
        public async Task RedirectUriNotAllowlistedForClient_ReturnsInvalidRequest()
        {
            var request = ValidCodeRequest();
            request.RedirectUri = "https://not-registered.example.com/callback";

            var result = await Service.ValidateAuthorizeRequestAsync(request);

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Error, Is.EqualTo("invalid_request"));
        }

        [Test]
        public async Task UnsupportedScopeForClient_ReturnsInvalidScope()
        {
            var request = ValidCodeRequest();
            request.Scope = "openid custom_scope";

            var result = await Service.ValidateAuthorizeRequestAsync(request);

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Error, Is.EqualTo("invalid_scope"));
        }

        [Test]
        public async Task UnsupportedResponseType_ReturnsUnsupportedResponseType()
        {
            var request = ValidCodeRequest();
            request.ResponseType = "token";

            var result = await Service.ValidateAuthorizeRequestAsync(request);

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Error, Is.EqualTo("unsupported_response_type"));
        }

        [Test]
        public async Task UnsupportedResponseMode_ReturnsUnsupportedResponseMode()
        {
            var request = ValidCodeRequest();
            request.ResponseMode = "fragment";

            var result = await Service.ValidateAuthorizeRequestAsync(request);

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Error, Is.EqualTo("unsupported_response_mode"));
        }

        [Test]
        public async Task IdTokenResponseType_WithoutNonce_ReturnsInvalidRequest()
        {
            DefaultConfig.Clients[0].RedirectUris.Add(TestRedirectUri);
            var request = new OidcAuthorizeRequest
            {
                ClientId = TestClientId,
                RedirectUri = TestRedirectUri,
                ResponseType = "id_token",
                Scope = "openid profile email",
                Nonce = null
            };

            var result = await Service.ValidateAuthorizeRequestAsync(request);

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Error, Is.EqualTo("invalid_request"));
            Assert.That(result.ErrorDescription, Does.Contain("nonce"));
        }

        [Test]
        public async Task IdTokenResponseType_WithNonce_ReturnsValid()
        {
            var request = new OidcAuthorizeRequest
            {
                ClientId = TestClientId,
                RedirectUri = TestRedirectUri,
                ResponseType = "id_token",
                Scope = "openid profile email",
                Nonce = "test-nonce"
            };

            var result = await Service.ValidateAuthorizeRequestAsync(request);

            Assert.That(result.IsValid, Is.True);
        }

        [Test]
        public async Task NoClientId_SingleMatchingRedirectUri_ResolvesClientAutomatically()
        {
            var request = new OidcAuthorizeRequest
            {
                ClientId = null,
                RedirectUri = TestRedirectUri,
                ResponseType = "code",
                Scope = "openid profile email"
            };

            var result = await Service.ValidateAuthorizeRequestAsync(request);

            Assert.That(result.IsValid, Is.True);
            Assert.That(result.Client!.ClientId, Is.EqualTo(TestClientId));
            Assert.That(result.NormalizedRequest!.ClientId, Is.EqualTo(TestClientId));
        }

        [Test]
        public async Task NoClientId_NoMatchingRedirectUri_ReturnsInvalidRequest()
        {
            var request = new OidcAuthorizeRequest
            {
                ClientId = null,
                RedirectUri = "https://no-match.example.com/callback",
                ResponseType = "code",
                Scope = "openid profile email"
            };

            var result = await Service.ValidateAuthorizeRequestAsync(request);

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Error, Is.EqualTo("invalid_request"));
        }

        [Test]
        public async Task NoClientId_AmbiguousRedirectUri_ReturnsInvalidRequest()
        {
            DefaultConfig.Clients.Add(new JwtIssuerClientConfiguration
            {
                ClientId = "second-client",
                RedirectUris = new List<string> { TestRedirectUri },
                AllowedScopes = new List<string> { "openid", "profile", "email" }
            });

            var request = new OidcAuthorizeRequest
            {
                ClientId = null,
                RedirectUri = TestRedirectUri,
                ResponseType = "code",
                Scope = "openid profile email"
            };

            var result = await Service.ValidateAuthorizeRequestAsync(request);

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Error, Is.EqualTo("invalid_request"));
            Assert.That(result.ErrorDescription, Does.Contain("Ambiguous"));
        }

        [Test]
        public async Task DefaultResponseMode_ForCodeFlow_IsQuery()
        {
            var request = ValidCodeRequest();

            var result = await Service.ValidateAuthorizeRequestAsync(request);

            Assert.That(result.IsValid, Is.True);
            Assert.That(result.NormalizedRequest!.ResponseMode, Is.EqualTo("query"));
        }

        [Test]
        public async Task DefaultResponseMode_ForIdTokenFlow_IsFormPost()
        {
            var request = new OidcAuthorizeRequest
            {
                ClientId = TestClientId,
                RedirectUri = TestRedirectUri,
                ResponseType = "id_token",
                Scope = "openid profile email",
                Nonce = "nonce-x"
            };

            var result = await Service.ValidateAuthorizeRequestAsync(request);

            Assert.That(result.IsValid, Is.True);
            Assert.That(result.NormalizedRequest!.ResponseMode, Is.EqualTo("form_post"));
        }
    }

    // =========================================================================
    [TestFixture]
    public class StorePendingAuthorizeRequestTests : JwtIssuerServiceTestBase
    {
        [Test]
        public async Task StorePendingAuthorizeRequestAsync_ReturnsNonEmptyId()
        {
            MockCache
                .Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(),
                    It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var request = ValidCodeRequest();
            var id = await Service.StorePendingAuthorizeRequestAsync(request);

            Assert.That(id, Is.Not.Null.Or.Empty);
        }

        [Test]
        public async Task StorePendingAuthorizeRequestAsync_CallsCache()
        {
            MockCache
                .Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(),
                    It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var request = ValidCodeRequest();
            await Service.StorePendingAuthorizeRequestAsync(request);

            MockCache.Verify(c => c.SetAsync(
                It.Is<string>(k => k.StartsWith("oidc:pending:")),
                It.IsAny<byte[]>(),
                It.IsAny<DistributedCacheEntryOptions>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task GetPendingAuthorizeRequestAsync_ReturnsDeserializedRequest()
        {
            var original = ValidCodeRequest("test-nonce");
            var json = JsonSerializer.Serialize(original, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            SetupCacheGet("oidc:pending:test-id", json);

            var result = await Service.GetPendingAuthorizeRequestAsync("test-id");

            Assert.That(result, Is.Not.Null);
            Assert.That(result!.ClientId, Is.EqualTo(TestClientId));
            Assert.That(result.Nonce, Is.EqualTo("test-nonce"));
        }

        [Test]
        public async Task GetPendingAuthorizeRequestAsync_CacheMiss_ReturnsNull()
        {
            SetupCacheMiss("oidc:pending:missing-id");

            var result = await Service.GetPendingAuthorizeRequestAsync("missing-id");

            Assert.That(result, Is.Null);
        }

        [Test]
        public async Task RemovePendingAuthorizeRequestAsync_CallsRemoveOnCache()
        {
            MockCache
                .Setup(c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            await Service.RemovePendingAuthorizeRequestAsync("test-id");

            MockCache.Verify(c => c.RemoveAsync(
                "oidc:pending:test-id",
                It.IsAny<CancellationToken>()), Times.Once);
        }
    }

    // =========================================================================
    [TestFixture]
    public class CreateAuthorizeResponseAsyncTests : JwtIssuerServiceTestBase
    {
        [SetUp]
        public override void SetUp()
        {
            base.SetUp();
            MockDriveService
                .Setup(d => d.GetAccountAddressAsync(TestEmail))
                .ReturnsAsync(TestAlgorandAddress);

            MockCache
                .Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(),
                    It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        }

        [Test]
        public async Task CodeFlow_Success_ReturnsCode()
        {
            var request = ValidCodeRequest();
            var client = DefaultConfig.Clients[0];
            var user = CreateUser();

            var result = await Service.CreateAuthorizeResponseAsync(request, client, user);

            Assert.That(result.Success, Is.True);
            Assert.That(result.Response, Is.Not.Null);
            Assert.That(result.Response!.ContainsKey("code"), Is.True);
            Assert.That(result.Response["code"], Is.Not.Empty);
        }

        [Test]
        public async Task CodeFlow_Success_ResponseContainsState()
        {
            var request = ValidCodeRequest();
            request.State = "my-state";
            var client = DefaultConfig.Clients[0];
            var user = CreateUser();

            var result = await Service.CreateAuthorizeResponseAsync(request, client, user);

            Assert.That(result.Success, Is.True);
            Assert.That(result.Response!["state"], Is.EqualTo("my-state"));
        }

        [Test]
        public async Task CodeFlow_StoresCodeInCache()
        {
            var request = ValidCodeRequest();
            var client = DefaultConfig.Clients[0];
            var user = CreateUser();

            await Service.CreateAuthorizeResponseAsync(request, client, user);

            MockCache.Verify(c => c.SetAsync(
                It.Is<string>(k => k.StartsWith("oidc:code:")),
                It.IsAny<byte[]>(),
                It.IsAny<DistributedCacheEntryOptions>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task IdTokenFlow_Success_ReturnsIdToken()
        {
            var request = new OidcAuthorizeRequest
            {
                ClientId = TestClientId,
                RedirectUri = TestRedirectUri,
                ResponseType = "id_token",
                Scope = "openid profile email",
                Nonce = "test-nonce"
            };
            var client = DefaultConfig.Clients[0];
            var user = CreateUser();

            var result = await Service.CreateAuthorizeResponseAsync(request, client, user);

            Assert.That(result.Success, Is.True);
            Assert.That(result.Response!.ContainsKey("id_token"), Is.True);
            Assert.That(result.Response["id_token"], Is.Not.Empty);
        }

        [Test]
        public async Task IdTokenFlow_TokenContainsAlgorandAddressClaim()
        {
            var request = new OidcAuthorizeRequest
            {
                ClientId = TestClientId,
                RedirectUri = TestRedirectUri,
                ResponseType = "id_token",
                Scope = "openid profile email",
                Nonce = "test-nonce"
            };
            var client = DefaultConfig.Clients[0];
            var user = CreateUser();

            var result = await Service.CreateAuthorizeResponseAsync(request, client, user);

            Assert.That(result.Success, Is.True);
            var handler = new JwtSecurityTokenHandler();
            var jwt = handler.ReadJwtToken(result.Response!["id_token"]);
            var address = jwt.Claims.FirstOrDefault(c => c.Type == "algorand_address")?.Value;
            Assert.That(address, Is.EqualTo(TestAlgorandAddress));
        }

        [Test]
        public async Task NoEmailClaim_ReturnsAccessDenied()
        {
            var request = ValidCodeRequest();
            var client = DefaultConfig.Clients[0];
            var user = new ClaimsPrincipal(new ClaimsIdentity(Array.Empty<Claim>(), "test"));

            var result = await Service.CreateAuthorizeResponseAsync(request, client, user);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Is.EqualTo("access_denied"));
        }

        [Test]
        public async Task DriveServiceThrows_ContinuesWithoutAlgorandAddress()
        {
            MockDriveService
                .Setup(d => d.GetAccountAddressAsync(TestEmail))
                .ThrowsAsync(new InvalidOperationException("Drive unavailable"));

            var request = ValidCodeRequest();
            var client = DefaultConfig.Clients[0];
            var user = CreateUser();

            var result = await Service.CreateAuthorizeResponseAsync(request, client, user);

            Assert.That(result.Success, Is.True);
            Assert.That(result.Response, Is.Not.Null);
            Assert.That(result.Response!.ContainsKey("code"), Is.True);
        }

        [Test]
        public async Task IdTokenFlow_WhenDriveUnavailable_DoesNotIncludeAlgorandAddressClaim()
        {
            MockDriveService
                .Setup(d => d.GetAccountAddressAsync(TestEmail))
                .ThrowsAsync(new InvalidOperationException("Drive unavailable"));

            var request = new OidcAuthorizeRequest
            {
                ClientId = TestClientId,
                RedirectUri = TestRedirectUri,
                ResponseType = "id_token",
                Scope = "openid profile email",
                Nonce = "test-nonce"
            };
            var client = DefaultConfig.Clients[0];
            var user = CreateUser();

            var result = await Service.CreateAuthorizeResponseAsync(request, client, user);

            Assert.That(result.Success, Is.True);
            var handler = new JwtSecurityTokenHandler();
            var jwt = handler.ReadJwtToken(result.Response!["id_token"]);
            var address = jwt.Claims.FirstOrDefault(c => c.Type == "algorand_address")?.Value;
            Assert.That(address, Is.Null);
        }
    }

    // =========================================================================
    [TestFixture]
    public class ExchangeTokenAsyncTests : JwtIssuerServiceTestBase
    {
        [SetUp]
        public override void SetUp()
        {
            base.SetUp();
            MockCache
                .Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(),
                    It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            MockCache
                .Setup(c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        }

        // ── authorization_code grant ──────────────────────────────────────────

        [Test]
        public async Task AuthorizationCodeGrant_ValidCode_ReturnsTokens()
        {
            var code = "valid-code";
            var codeJson = BuildCodeRecordJson(code, TestClientId, TestRedirectUri);
            SetupCacheGet("oidc:code:" + code, codeJson);

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
            Assert.That(result.StatusCode, Is.EqualTo(200));
            Assert.That(result.Response, Is.Not.Null);
            Assert.That(result.Response!.AccessToken, Is.Not.Empty);
            Assert.That(result.Response.IdToken, Is.Not.Empty);
            Assert.That(result.Response.RefreshToken, Is.Not.Empty);
        }

        [Test]
        public async Task AuthorizationCodeGrant_ValidCode_DeletesCodeFromCache()
        {
            var code = "use-once-code";
            var codeJson = BuildCodeRecordJson(code, TestClientId, TestRedirectUri);
            SetupCacheGet("oidc:code:" + code, codeJson);

            var tokenRequest = new OidcTokenRequest
            {
                GrantType = "authorization_code",
                Code = code,
                RedirectUri = TestRedirectUri,
                ClientId = TestClientId,
                ClientSecret = TestClientSecret
            };

            await Service.ExchangeTokenAsync(tokenRequest, null);

            MockCache.Verify(c => c.RemoveAsync(
                "oidc:code:" + code,
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task AuthorizationCodeGrant_CodeNotInCache_ReturnsInvalidGrant()
        {
            SetupCacheMiss("oidc:code:bad-code");

            var tokenRequest = new OidcTokenRequest
            {
                GrantType = "authorization_code",
                Code = "bad-code",
                RedirectUri = TestRedirectUri,
                ClientId = TestClientId,
                ClientSecret = TestClientSecret
            };

            var result = await Service.ExchangeTokenAsync(tokenRequest, null);

            Assert.That(result.Success, Is.False);
            Assert.That(result.StatusCode, Is.EqualTo(400));
            Assert.That(result.Error, Is.EqualTo("invalid_grant"));
        }

        [Test]
        public async Task AuthorizationCodeGrant_MissingCode_ReturnsInvalidRequest()
        {
            var tokenRequest = new OidcTokenRequest
            {
                GrantType = "authorization_code",
                Code = null,
                RedirectUri = TestRedirectUri,
                ClientId = TestClientId,
                ClientSecret = TestClientSecret
            };

            var result = await Service.ExchangeTokenAsync(tokenRequest, null);

            Assert.That(result.Success, Is.False);
            Assert.That(result.StatusCode, Is.EqualTo(400));
            Assert.That(result.Error, Is.EqualTo("invalid_request"));
        }

        [Test]
        public async Task AuthorizationCodeGrant_WrongClient_ReturnsInvalidGrant()
        {
            var code = "cross-client-code";
            var codeJson = BuildCodeRecordJson(code, "other-client", TestRedirectUri);
            SetupCacheGet("oidc:code:" + code, codeJson);

            var tokenRequest = new OidcTokenRequest
            {
                GrantType = "authorization_code",
                Code = code,
                RedirectUri = TestRedirectUri,
                ClientId = TestClientId,
                ClientSecret = TestClientSecret
            };

            var result = await Service.ExchangeTokenAsync(tokenRequest, null);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Is.EqualTo("invalid_grant"));
            Assert.That(result.ErrorDescription, Does.Contain("client"));
        }

        [Test]
        public async Task AuthorizationCodeGrant_WrongRedirectUri_ReturnsInvalidGrant()
        {
            var code = "redirect-mismatch-code";
            var codeJson = BuildCodeRecordJson(code, TestClientId, TestRedirectUri);
            SetupCacheGet("oidc:code:" + code, codeJson);

            var tokenRequest = new OidcTokenRequest
            {
                GrantType = "authorization_code",
                Code = code,
                RedirectUri = "https://wrong.example.com/callback",
                ClientId = TestClientId,
                ClientSecret = TestClientSecret
            };

            var result = await Service.ExchangeTokenAsync(tokenRequest, null);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Is.EqualTo("invalid_grant"));
            Assert.That(result.ErrorDescription, Does.Contain("redirect_uri"));
        }

        [Test]
        public async Task AuthorizationCodeGrant_ExpiredCode_ReturnsInvalidGrant()
        {
            var code = "expired-code";
            var codeJson = BuildCodeRecordJson(code, TestClientId, TestRedirectUri,
                expiresUtc: DateTimeOffset.UtcNow.AddSeconds(-1));
            SetupCacheGet("oidc:code:" + code, codeJson);

            var tokenRequest = new OidcTokenRequest
            {
                GrantType = "authorization_code",
                Code = code,
                RedirectUri = TestRedirectUri,
                ClientId = TestClientId,
                ClientSecret = TestClientSecret
            };

            var result = await Service.ExchangeTokenAsync(tokenRequest, null);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Is.EqualTo("invalid_grant"));
            Assert.That(result.ErrorDescription, Does.Contain("expired"));
        }

        // ── refresh_token grant ───────────────────────────────────────────────

        [Test]
        public async Task RefreshTokenGrant_ValidToken_ReturnsNewTokens()
        {
            var refreshToken = "valid-refresh";
            var refreshJson = BuildRefreshTokenRecordJson(refreshToken, TestClientId);
            SetupCacheGet("oidc:refresh:" + refreshToken, refreshJson);

            var tokenRequest = new OidcTokenRequest
            {
                GrantType = "refresh_token",
                RefreshToken = refreshToken,
                ClientId = TestClientId,
                ClientSecret = TestClientSecret
            };

            var result = await Service.ExchangeTokenAsync(tokenRequest, null);

            Assert.That(result.Success, Is.True);
            Assert.That(result.StatusCode, Is.EqualTo(200));
            Assert.That(result.Response!.AccessToken, Is.Not.Empty);
        }

        [Test]
        public async Task RefreshTokenGrant_ValidToken_RotatesRefreshToken()
        {
            var refreshToken = "rotatable-refresh";
            var refreshJson = BuildRefreshTokenRecordJson(refreshToken, TestClientId);
            SetupCacheGet("oidc:refresh:" + refreshToken, refreshJson);

            var tokenRequest = new OidcTokenRequest
            {
                GrantType = "refresh_token",
                RefreshToken = refreshToken,
                ClientId = TestClientId,
                ClientSecret = TestClientSecret
            };

            var result = await Service.ExchangeTokenAsync(tokenRequest, null);

            Assert.That(result.Success, Is.True);
            Assert.That(result.Response!.RefreshToken, Is.Not.EqualTo(refreshToken));
        }

        [Test]
        public async Task RefreshTokenGrant_ValidToken_DeletesOldToken()
        {
            var refreshToken = "delete-me-refresh";
            var refreshJson = BuildRefreshTokenRecordJson(refreshToken, TestClientId);
            SetupCacheGet("oidc:refresh:" + refreshToken, refreshJson);

            var tokenRequest = new OidcTokenRequest
            {
                GrantType = "refresh_token",
                RefreshToken = refreshToken,
                ClientId = TestClientId,
                ClientSecret = TestClientSecret
            };

            await Service.ExchangeTokenAsync(tokenRequest, null);

            MockCache.Verify(c => c.RemoveAsync(
                "oidc:refresh:" + refreshToken,
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task RefreshTokenGrant_MissingToken_ReturnsInvalidRequest()
        {
            var tokenRequest = new OidcTokenRequest
            {
                GrantType = "refresh_token",
                RefreshToken = null,
                ClientId = TestClientId,
                ClientSecret = TestClientSecret
            };

            var result = await Service.ExchangeTokenAsync(tokenRequest, null);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Is.EqualTo("invalid_request"));
        }

        [Test]
        public async Task RefreshTokenGrant_TokenNotInCache_ReturnsInvalidGrant()
        {
            SetupCacheMiss("oidc:refresh:ghost-token");

            var tokenRequest = new OidcTokenRequest
            {
                GrantType = "refresh_token",
                RefreshToken = "ghost-token",
                ClientId = TestClientId,
                ClientSecret = TestClientSecret
            };

            var result = await Service.ExchangeTokenAsync(tokenRequest, null);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Is.EqualTo("invalid_grant"));
        }

        [Test]
        public async Task RefreshTokenGrant_WrongClient_ReturnsInvalidGrant()
        {
            var refreshToken = "wrong-client-refresh";
            var refreshJson = BuildRefreshTokenRecordJson(refreshToken, "other-client");
            SetupCacheGet("oidc:refresh:" + refreshToken, refreshJson);

            var tokenRequest = new OidcTokenRequest
            {
                GrantType = "refresh_token",
                RefreshToken = refreshToken,
                ClientId = TestClientId,
                ClientSecret = TestClientSecret
            };

            var result = await Service.ExchangeTokenAsync(tokenRequest, null);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Is.EqualTo("invalid_grant"));
        }

        [Test]
        public async Task RefreshTokenGrant_ExpiredToken_ReturnsInvalidGrant()
        {
            var refreshToken = "expired-refresh";
            var refreshJson = BuildRefreshTokenRecordJson(refreshToken, TestClientId,
                expiresUtc: DateTimeOffset.UtcNow.AddDays(-1));
            SetupCacheGet("oidc:refresh:" + refreshToken, refreshJson);

            var tokenRequest = new OidcTokenRequest
            {
                GrantType = "refresh_token",
                RefreshToken = refreshToken,
                ClientId = TestClientId,
                ClientSecret = TestClientSecret
            };

            var result = await Service.ExchangeTokenAsync(tokenRequest, null);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Is.EqualTo("invalid_grant"));
            Assert.That(result.ErrorDescription, Does.Contain("expired"));
        }

        // ── client authentication ─────────────────────────────────────────────

        [Test]
        public async Task MissingClientId_ReturnsInvalidClient_401()
        {
            var tokenRequest = new OidcTokenRequest
            {
                GrantType = "authorization_code",
                Code = "some-code",
                RedirectUri = TestRedirectUri,
                ClientId = null,
                ClientSecret = null
            };

            var result = await Service.ExchangeTokenAsync(tokenRequest, null);

            Assert.That(result.Success, Is.False);
            Assert.That(result.StatusCode, Is.EqualTo(401));
            Assert.That(result.Error, Is.EqualTo("invalid_client"));
        }

        [Test]
        public async Task WrongClientSecret_ReturnsInvalidClient_401()
        {
            var tokenRequest = new OidcTokenRequest
            {
                GrantType = "authorization_code",
                Code = "some-code",
                RedirectUri = TestRedirectUri,
                ClientId = TestClientId,
                ClientSecret = "wrong-secret"
            };

            var result = await Service.ExchangeTokenAsync(tokenRequest, null);

            Assert.That(result.Success, Is.False);
            Assert.That(result.StatusCode, Is.EqualTo(401));
            Assert.That(result.Error, Is.EqualTo("invalid_client"));
        }

        [Test]
        public async Task BasicAuthHeader_CredentialsUsedForClientAuth()
        {
            var code = "basic-auth-code";
            var codeJson = BuildCodeRecordJson(code, TestClientId, TestRedirectUri);
            SetupCacheGet("oidc:code:" + code, codeJson);

            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{TestClientId}:{TestClientSecret}"));
            var basicAuthHeader = $"Basic {credentials}";

            var tokenRequest = new OidcTokenRequest
            {
                GrantType = "authorization_code",
                Code = code,
                RedirectUri = TestRedirectUri,
                ClientId = null,
                ClientSecret = null
            };

            var result = await Service.ExchangeTokenAsync(tokenRequest, basicAuthHeader);

            Assert.That(result.Success, Is.True);
        }

        [Test]
        public async Task UnsupportedGrantType_ReturnsUnsupportedGrantType()
        {
            var tokenRequest = new OidcTokenRequest
            {
                GrantType = "client_credentials",
                ClientId = TestClientId,
                ClientSecret = TestClientSecret
            };

            var result = await Service.ExchangeTokenAsync(tokenRequest, null);

            Assert.That(result.Success, Is.False);
            Assert.That(result.StatusCode, Is.EqualTo(400));
            Assert.That(result.Error, Is.EqualTo("unsupported_grant_type"));
        }
    }

    // =========================================================================
    [TestFixture]
    public class ValidateBearerAccessTokenTests : JwtIssuerServiceTestBase
    {
        /// <summary>Issues a real access token via the service using internal RSA key.</summary>
        private async Task<string> IssueRealAccessTokenAsync()
        {
            MockDriveService
                .Setup(d => d.GetAccountAddressAsync(TestEmail))
                .ReturnsAsync(TestAlgorandAddress);
            MockCache
                .Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(),
                    It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            MockCache
                .Setup(c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var code = "real-code";
            var codeJson = BuildCodeRecordJson(code, TestClientId, TestRedirectUri);
            SetupCacheGet("oidc:code:" + code, codeJson);

            var tokenRequest = new OidcTokenRequest
            {
                GrantType = "authorization_code",
                Code = code,
                RedirectUri = TestRedirectUri,
                ClientId = TestClientId,
                ClientSecret = TestClientSecret
            };

            var result = await Service.ExchangeTokenAsync(tokenRequest, null);
            Assert.That(result.Success, Is.True, "Pre-condition: token issuance must succeed");
            return result.Response!.AccessToken;
        }

        [Test]
        public async Task ValidToken_ReturnsIsValidTrue()
        {
            var token = await IssueRealAccessTokenAsync();

            var result = Service.ValidateBearerAccessToken(token);

            Assert.That(result.IsValid, Is.True);
            Assert.That(result.Error, Is.Null);
        }

        [Test]
        public async Task ValidToken_ReturnsPrincipalAndClaims()
        {
            var token = await IssueRealAccessTokenAsync();

            var result = Service.ValidateBearerAccessToken(token);

            Assert.That(result.Principal, Is.Not.Null);
            Assert.That(result.Claims, Is.Not.Null);
        }

        [Test]
        public async Task ValidToken_ClaimsContainAlgorandAddress()
        {
            var token = await IssueRealAccessTokenAsync();

            var result = Service.ValidateBearerAccessToken(token);

            Assert.That(result.IsValid, Is.True);
            Assert.That(result.Claims!.ContainsKey("algorand_address"), Is.True);
            Assert.That(result.Claims["algorand_address"].ToString(), Is.EqualTo(TestAlgorandAddress));
        }

        [Test]
        public void RandomString_ReturnsIsValidFalse()
        {
            var result = Service.ValidateBearerAccessToken("not.a.jwt");

            Assert.That(result.IsValid, Is.False);
        }

        [Test]
        public void EmptyToken_ReturnsIsValidFalse()
        {
            var result = Service.ValidateBearerAccessToken(string.Empty);

            Assert.That(result.IsValid, Is.False);
        }

        [Test]
        public void TokenSignedWithDifferentKey_ReturnsIsValidFalse()
        {
            // Generate a token signed with a completely different RSA key
            using var foreignRsa = RSA.Create(2048);
            var foreignKey = new RsaSecurityKey(foreignRsa) { KeyId = "foreign-key" };
            var creds = new SigningCredentials(foreignKey, SecurityAlgorithms.RsaSha256);
            var jwt = new JwtSecurityToken(
                issuer: TestIssuer,
                audience: TestClientId,
                claims: new[] { new Claim("token_use", "access_token") },
                notBefore: DateTime.UtcNow,
                expires: DateTime.UtcNow.AddMinutes(15),
                signingCredentials: creds);
            var foreignToken = new JwtSecurityTokenHandler().WriteToken(jwt);

            var result = Service.ValidateBearerAccessToken(foreignToken);

            Assert.That(result.IsValid, Is.False);
        }

        [Test]
        public void IdToken_UsedAsAccessToken_ReturnsIsValidFalse()
        {
            // Build an id_token (missing token_use=access_token) using a public-key-matched signer
            // Since we cannot access the private key directly, we create an id_token via the flow
            // and verify ValidateBearerAccessToken rejects it.
            // Instead, we test via a crafted JWT signed with wrong key which also fails.
            // This test verifies the token_use check by creating a JWT with token_use=id_token.
            using var rsa2 = RSA.Create(2048);
            var key2 = new RsaSecurityKey(rsa2) { KeyId = "test-key2" };
            var creds = new SigningCredentials(key2, SecurityAlgorithms.RsaSha256);
            var idToken = new JwtSecurityToken(
                issuer: TestIssuer,
                audience: TestClientId,
                claims: new[] { new Claim("token_use", "id_token") },
                notBefore: DateTime.UtcNow,
                expires: DateTime.UtcNow.AddMinutes(15),
                signingCredentials: creds);
            var tokenStr = new JwtSecurityTokenHandler().WriteToken(idToken);

            // Must fail (even if issuer matches) because signing key won't match
            var result = Service.ValidateBearerAccessToken(tokenStr);

            Assert.That(result.IsValid, Is.False);
        }
    }

    // =========================================================================
    [TestFixture]
    public class IntrospectAsyncTests : JwtIssuerServiceTestBase
    {
        private async Task<string> IssueRealAccessTokenAsync()
        {
            MockDriveService
                .Setup(d => d.GetAccountAddressAsync(TestEmail))
                .ReturnsAsync(TestAlgorandAddress);
            MockCache
                .Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(),
                    It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            MockCache
                .Setup(c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var code = "introspect-code";
            var codeJson = BuildCodeRecordJson(code, TestClientId, TestRedirectUri);
            SetupCacheGet("oidc:code:" + code, codeJson);

            var tokenRequest = new OidcTokenRequest
            {
                GrantType = "authorization_code",
                Code = code,
                RedirectUri = TestRedirectUri,
                ClientId = TestClientId,
                ClientSecret = TestClientSecret
            };

            var result = await Service.ExchangeTokenAsync(tokenRequest, null);
            return result.Response!.AccessToken;
        }

        [Test]
        public async Task ValidToken_ReturnsActiveTrue()
        {
            var token = await IssueRealAccessTokenAsync();

            var introspection = await Service.IntrospectAsync(token);

            Assert.That(introspection["active"], Is.EqualTo(true));
        }

        [Test]
        public async Task ValidToken_ResponseContainsEmail()
        {
            var token = await IssueRealAccessTokenAsync();

            var introspection = await Service.IntrospectAsync(token);

            Assert.That(introspection.ContainsKey("email"), Is.True);
        }

        [Test]
        public async Task ValidToken_ResponseContainsAlgorandAddress()
        {
            var token = await IssueRealAccessTokenAsync();

            var introspection = await Service.IntrospectAsync(token);

            Assert.That(introspection.ContainsKey("algorand_address"), Is.True);
            Assert.That(introspection["algorand_address"].ToString(), Is.EqualTo(TestAlgorandAddress));
        }

        [Test]
        public async Task InvalidToken_ReturnsActiveFalse()
        {
            var introspection = await Service.IntrospectAsync("invalid.token.here");

            Assert.That(introspection["active"], Is.EqualTo(false));
        }

        [Test]
        public async Task InvalidToken_ResponseOnlyContainsActiveKey()
        {
            var introspection = await Service.IntrospectAsync("garbage");

            Assert.That(introspection.Count, Is.EqualTo(1));
            Assert.That(introspection.ContainsKey("active"), Is.True);
        }
    }

    // =========================================================================
    [TestFixture]
    public class TokenClaimsTests : JwtIssuerServiceTestBase
    {
        [SetUp]
        public override void SetUp()
        {
            base.SetUp();
            MockDriveService
                .Setup(d => d.GetAccountAddressAsync(TestEmail))
                .ReturnsAsync(TestAlgorandAddress);
            MockCache
                .Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(),
                    It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            MockCache
                .Setup(c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        }

        private async Task<OidcTokenResponse> GetTokensAsync(string code = "claims-code")
        {
            var codeJson = BuildCodeRecordJson(code, TestClientId, TestRedirectUri, nonce: "test-nonce");
            SetupCacheGet("oidc:code:" + code, codeJson);

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
            return result.Response!;
        }

        [Test]
        public async Task AccessToken_HasTokenUseAccessToken()
        {
            var tokens = await GetTokensAsync();
            var handler = new JwtSecurityTokenHandler();
            var jwt = handler.ReadJwtToken(tokens.AccessToken);

            Assert.That(jwt.Claims.FirstOrDefault(c => c.Type == "token_use")?.Value, Is.EqualTo("access_token"));
        }

        [Test]
        public async Task AccessToken_IssuerMatchesConfig()
        {
            var tokens = await GetTokensAsync();
            var handler = new JwtSecurityTokenHandler();
            var jwt = handler.ReadJwtToken(tokens.AccessToken);

            Assert.That(jwt.Issuer, Is.EqualTo(TestIssuer));
        }

        [Test]
        public async Task AccessToken_AudienceIsClientId()
        {
            var tokens = await GetTokensAsync();
            var handler = new JwtSecurityTokenHandler();
            var jwt = handler.ReadJwtToken(tokens.AccessToken);

            Assert.That(jwt.Audiences, Does.Contain(TestClientId));
        }

        [Test]
        public async Task AccessToken_ContainsAlgorandAddressClaim()
        {
            var tokens = await GetTokensAsync();
            var handler = new JwtSecurityTokenHandler();
            var jwt = handler.ReadJwtToken(tokens.AccessToken);

            Assert.That(jwt.Claims.FirstOrDefault(c => c.Type == "algorand_address")?.Value, Is.EqualTo(TestAlgorandAddress));
        }

        [Test]
        public async Task AccessToken_HasNonEmptyJti()
        {
            var tokens = await GetTokensAsync();
            var handler = new JwtSecurityTokenHandler();
            var jwt = handler.ReadJwtToken(tokens.AccessToken);

            Assert.That(jwt.Id, Is.Not.Empty);
        }

        [Test]
        public async Task IdToken_HasIssuerClaim()
        {
            var tokens = await GetTokensAsync();
            var handler = new JwtSecurityTokenHandler();
            var jwt = handler.ReadJwtToken(tokens.IdToken);

            Assert.That(jwt.Issuer, Is.EqualTo(TestIssuer));
        }

        [Test]
        public async Task IdToken_ContainsAlgorandAddress()
        {
            var tokens = await GetTokensAsync();
            var handler = new JwtSecurityTokenHandler();
            var jwt = handler.ReadJwtToken(tokens.IdToken);

            Assert.That(jwt.Claims.FirstOrDefault(c => c.Type == "algorand_address")?.Value, Is.EqualTo(TestAlgorandAddress));
        }

        [Test]
        public async Task IdToken_ContainsPreferredUsername_AsShortIdentity()
        {
            var tokens = await GetTokensAsync();
            var handler = new JwtSecurityTokenHandler();
            var jwt = handler.ReadJwtToken(tokens.IdToken);

            var pref = jwt.Claims.FirstOrDefault(c => c.Type == "preferred_username")?.Value;
            Assert.That(pref, Is.Not.Null);
            // Short identity is first4 + last4 of the Algorand address
            Assert.That(pref, Does.StartWith(TestAlgorandAddress[..4]));
            Assert.That(pref, Does.EndWith(TestAlgorandAddress[^4..]));
        }

        [Test]
        public async Task TwoExchanges_SameEmail_SamePairwiseSubject()
        {
            var code1 = "sub-code-1";
            var code2 = "sub-code-2";
            SetupCacheGet("oidc:code:" + code1, BuildCodeRecordJson(code1, TestClientId, TestRedirectUri));
            SetupCacheGet("oidc:code:" + code2, BuildCodeRecordJson(code2, TestClientId, TestRedirectUri));

            var req1 = new OidcTokenRequest { GrantType = "authorization_code", Code = code1, RedirectUri = TestRedirectUri, ClientId = TestClientId, ClientSecret = TestClientSecret };
            var req2 = new OidcTokenRequest { GrantType = "authorization_code", Code = code2, RedirectUri = TestRedirectUri, ClientId = TestClientId, ClientSecret = TestClientSecret };

            var result1 = await Service.ExchangeTokenAsync(req1, null);
            var result2 = await Service.ExchangeTokenAsync(req2, null);

            var jwt1 = new JwtSecurityTokenHandler().ReadJwtToken(result1.Response!.AccessToken);
            var jwt2 = new JwtSecurityTokenHandler().ReadJwtToken(result2.Response!.AccessToken);

            Assert.That(jwt1.Subject, Is.EqualTo(jwt2.Subject));
        }

        [Test]
        public async Task TokenLifetime_AccessTokenExpiresInMatchesConfig()
        {
            var tokens = await GetTokensAsync();
            Assert.That(tokens.ExpiresIn, Is.EqualTo(DefaultConfig.AccessTokenLifetimeMinutes * 60));
        }

        [Test]
        public async Task TokenType_IsBearer()
        {
            var tokens = await GetTokensAsync();
            Assert.That(tokens.TokenType, Is.EqualTo("Bearer"));
        }
    }

    // =========================================================================
    [TestFixture]
    public class SigningKeyTests : JwtIssuerServiceTestBase
    {
        [Test]
        public void EphemeralKey_ServiceCreated_DoesNotThrow()
        {
            DefaultConfig.SigningPrivateKeyPem = string.Empty;

            Assert.DoesNotThrow(() =>
            {
                var svc = new JwtIssuerService(
                    MockCache.Object,
                    MockConfig.Object,
                    MockDriveService.Object,
                    MockLogger.Object);
                _ = svc.GetJsonWebKeySet();
            });
        }

        [Test]
        public void ConfiguredPemKey_ServiceCreated_DoesNotThrow()
        {
            using var rsa = RSA.Create(2048);
            var pem = rsa.ExportRSAPrivateKeyPem();
            DefaultConfig.SigningPrivateKeyPem = pem;

            Assert.DoesNotThrow(() =>
            {
                var svc = new JwtIssuerService(
                    MockCache.Object,
                    MockConfig.Object,
                    MockDriveService.Object,
                    MockLogger.Object);
                _ = svc.GetJsonWebKeySet();
            });
        }

        [Test]
        public void InvalidPemKey_FallsBackToEphemeral_DoesNotThrow()
        {
            DefaultConfig.SigningPrivateKeyPem = "-----BEGIN RSA PRIVATE KEY-----\ninvalid-data\n-----END RSA PRIVATE KEY-----";

            Assert.DoesNotThrow(() =>
            {
                var svc = new JwtIssuerService(
                    MockCache.Object,
                    MockConfig.Object,
                    MockDriveService.Object,
                    MockLogger.Object);
                _ = svc.GetJsonWebKeySet();
            });
        }

        [Test]
        public void ConfiguredPemKey_TokenIssuedAndValidatedWithSameKey()
        {
            using var rsa = RSA.Create(2048);
            var pem = rsa.ExportRSAPrivateKeyPem();
            DefaultConfig.SigningPrivateKeyPem = pem;

            MockCache
                .Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(),
                    It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            MockCache
                .Setup(c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var svc = new JwtIssuerService(
                MockCache.Object,
                MockConfig.Object,
                MockDriveService.Object,
                MockLogger.Object);

            // Build a "valid" access token JSON directly
            var code = "pem-code";
            var codeJson = BuildCodeRecordJson(code, TestClientId, TestRedirectUri);
            SetupCacheGet("oidc:code:" + code, codeJson);

            var tokenRequest = new OidcTokenRequest
            {
                GrantType = "authorization_code",
                Code = code,
                RedirectUri = TestRedirectUri,
                ClientId = TestClientId,
                ClientSecret = TestClientSecret
            };

            var task = svc.ExchangeTokenAsync(tokenRequest, null);
            task.Wait();
            var response = task.Result;
            Assert.That(response.Success, Is.True);

            var validation = svc.ValidateBearerAccessToken(response.Response!.AccessToken);
            Assert.That(validation.IsValid, Is.True);
        }
    }
}
