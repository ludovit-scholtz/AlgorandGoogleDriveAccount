using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BiatecOIDC.Helper;
using BiatecOIDC.Model;
using BiatecSelfCustodyCore.BusinessLogic;
using BiatecSelfCustodyCore.Model;
using BiatecSelfCustodyCore.Providers;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;

namespace BiatecOIDC.BusinessLogic
{
    public class JwtIssuerService : IJwtIssuerService
    {
        private const string PendingPrefix = "oidc:pending:";
        private const string CodePrefix = "oidc:code:";
        private const string RefreshPrefix = "oidc:refresh:";

        private readonly IConnectionMultiplexer _redis;
        private readonly IOptionsMonitor<JwtIssuerConfiguration> _config;
        private readonly IDriveService _driveService;
        private readonly IProviderAccessTokenProtector _providerTokenProtector;
        private readonly ICloudStorageProviderCatalog _providerCatalog;
        private readonly IHostEnvironment _environment;
        private readonly ILogger<JwtIssuerService> _logger;
        private readonly RSA _rsa;
        private readonly SigningCredentials _signingCredentials;

        private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public JwtIssuerService(
            IConnectionMultiplexer redis,
            IOptionsMonitor<JwtIssuerConfiguration> config,
            IDriveService driveService,
            IProviderAccessTokenProtector providerTokenProtector,
            ICloudStorageProviderCatalog providerCatalog,
            IHostEnvironment environment,
            ILogger<JwtIssuerService> logger)
        {
            _redis = redis;
            _config = config;
            _driveService = driveService;
            _providerTokenProtector = providerTokenProtector;
            _providerCatalog = providerCatalog;
            _environment = environment;
            _logger = logger;

            _rsa = LoadOrCreateSigningKey();
            var key = new RsaSecurityKey(_rsa)
            {
                KeyId = Current.KeyId
            };
            _signingCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);
        }

        private JwtIssuerConfiguration Current => _config.CurrentValue;

        public string GetIssuer(HttpRequest request)
        {
            if (!string.IsNullOrWhiteSpace(Current.Issuer))
            {
                return Current.Issuer.TrimEnd('/');
            }

            return $"{request.Scheme}://{request.Host}";
        }

        public object GetDiscoveryDocument(HttpRequest request)
        {
            var issuer = GetIssuer(request);
            return new
            {
                issuer,
                authorization_endpoint = $"{issuer}/authorize",
                token_endpoint = $"{issuer}/token",
                end_session_endpoint = $"{issuer}/connect/endsession",
                userinfo_endpoint = $"{issuer}/userinfo",
                introspection_endpoint = $"{issuer}/introspect",
                jwks_uri = $"{issuer}/.well-known/jwks.json",
                response_types_supported = new[] { "code", "id_token" },
                response_modes_supported = new[] { "query", "form_post" },
                grant_types_supported = new[] { "authorization_code", "refresh_token" },
                frontchannel_logout_supported = false,
                backchannel_logout_supported = false,
                subject_types_supported = new[] { "pairwise" },
                id_token_signing_alg_values_supported = new[] { "RS256" },
                token_endpoint_auth_methods_supported = new[] { "client_secret_post", "client_secret_basic", "none" },
                code_challenge_methods_supported = new[] { "S256", "plain" },
                // "sign" and "manage-limits" are wallet-API authorization scopes (see BiatecOIDC's
                // WalletController) - granting them stamps a matching claim of the same name onto the
                // issued access token; see JwtIssuerService.CreateAccessToken.
                scopes_supported = new[] { "openid", "profile", "email", "sign", "manage-limits", "rekey" },
                claims_supported = new[] { "sub", "iss", "aud", "exp", "iat", "nbf", "nonce", "email", "name", "preferred_username", "algorand_address", "biatec_idp", "sign", "manage-limits", "rekey" }
            };
        }

        public object GetJsonWebKeySet()
        {
            var parameters = _rsa.ExportParameters(false);
            return new
            {
                keys = new[]
                {
                    new
                    {
                        kty = "RSA",
                        use = "sig",
                        kid = Current.KeyId,
                        alg = "RS256",
                        n = Base64UrlEncoder.Encode(parameters.Modulus),
                        e = Base64UrlEncoder.Encode(parameters.Exponent)
                    }
                }
            };
        }

        public async Task<(bool IsValid, string? Error, string? ErrorDescription, OidcAuthorizeRequest? NormalizedRequest, JwtIssuerClientConfiguration? Client)> ValidateAuthorizeRequestAsync(OidcAuthorizeRequest request)
        {
            var normalized = new OidcAuthorizeRequest
            {
                ClientId = request.ClientId,
                RedirectUri = string.IsNullOrWhiteSpace(request.RedirectUri) ? request.ReturnUrl : request.RedirectUri,
                ReturnUrl = request.ReturnUrl,
                ResponseType = string.IsNullOrWhiteSpace(request.ResponseType) ? "code" : request.ResponseType,
                ResponseMode = request.ResponseMode,
                Scope = string.IsNullOrWhiteSpace(request.Scope) ? "openid profile email" : request.Scope,
                State = request.State,
                Nonce = request.Nonce,
                CodeChallenge = request.CodeChallenge,
                CodeChallengeMethod = string.IsNullOrWhiteSpace(request.CodeChallengeMethod) ? "plain" : request.CodeChallengeMethod
            };

            if (normalized.ReturnUrl != null && string.IsNullOrWhiteSpace(request.ClientId) && string.IsNullOrWhiteSpace(request.ResponseType))
            {
                normalized.ResponseType = "id_token";
                normalized.ResponseMode = "form_post";
            }

            if (!string.Equals(normalized.ResponseType, "code", StringComparison.Ordinal) &&
                !string.Equals(normalized.ResponseType, "id_token", StringComparison.Ordinal))
            {
                return (false, "unsupported_response_type", "Supported values are 'code' and 'id_token'.", null, null);
            }

            if (string.IsNullOrWhiteSpace(normalized.ResponseMode))
            {
                normalized.ResponseMode = string.Equals(normalized.ResponseType, "id_token", StringComparison.Ordinal) ? "form_post" : "query";
            }

            if (!string.Equals(normalized.ResponseMode, "query", StringComparison.Ordinal) &&
                !string.Equals(normalized.ResponseMode, "form_post", StringComparison.Ordinal))
            {
                return (false, "unsupported_response_mode", "Supported values are 'query' and 'form_post'.", null, null);
            }

            if (string.IsNullOrWhiteSpace(normalized.RedirectUri))
            {
                return (false, "invalid_request", "Missing redirect_uri (or returnUrl).", null, null);
            }

            if (!Uri.TryCreate(normalized.RedirectUri, UriKind.Absolute, out var redirectUri))
            {
                return (false, "invalid_request", "redirect_uri must be an absolute URI.", null, null);
            }

            if (!string.IsNullOrEmpty(redirectUri.Fragment))
            {
                return (false, "invalid_request", "redirect_uri must not include fragment.", null, null);
            }

            if (string.Equals(redirectUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                var isLoopback = string.Equals(redirectUri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(redirectUri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(redirectUri.Host, "::1", StringComparison.OrdinalIgnoreCase);
                if (!Current.AllowHttpForLoopbackRedirectUris || !isLoopback)
                {
                    return (false, "invalid_request", "redirect_uri must use HTTPS unless explicitly allowed loopback HTTP URI.", null, null);
                }
            }

            var requestedScopes = normalized.Scope
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (!requestedScopes.Contains("openid", StringComparer.Ordinal))
            {
                return (false, "invalid_scope", "The openid scope is required.", null, null);
            }

            JwtIssuerClientConfiguration? client = null;

            if (!string.IsNullOrWhiteSpace(normalized.ClientId))
            {
                client = Current.Clients.FirstOrDefault(c => string.Equals(c.ClientId, normalized.ClientId, StringComparison.Ordinal));
                if (client == null)
                {
                    return (false, "invalid_client", "Unknown client_id.", null, null);
                }
            }
            else
            {
                var matchingClients = Current.Clients
                    .Where(c => c.RedirectUris.Any(r => RedirectUriMatcher.MatchesAuthorizeRedirect(r, normalized.RedirectUri)))
                    .ToList();

                if (matchingClients.Count == 1)
                {
                    client = matchingClients[0];
                    normalized.ClientId = client.ClientId;
                }
                else if (matchingClients.Count > 1)
                {
                    return (false, "invalid_request", "Ambiguous redirect_uri match. Provide client_id explicitly.", null, null);
                }
                else
                {
                    return (false, "invalid_request", "redirect_uri is not allowlisted.", null, null);
                }
            }

            if (!client.RedirectUris.Any(r => RedirectUriMatcher.MatchesAuthorizeRedirect(r, normalized.RedirectUri)))
            {
                return (false, "invalid_request", "redirect_uri is not allowlisted for this client_id.", null, null);
            }

            // Two different failure modes for two different kinds of "unexpected" scope:
            //
            // 1. A scope this server has never heard of at all (a typo, or a library-injected
            //    "resource/.default" some OIDC clients - notably MSAL/Azure AD-flavored ones - auto-append
            //    whether you asked for it or not) is silently dropped, never rejected. There's nothing a
            //    developer could even fix here, and hard-failing the whole login over noise the client
            //    library added on its own is exactly the kind of surprising behavior that makes scopes hard
            //    to work with.
            // 2. A scope this server *does* recognize (openid/profile/email/sign/manage-limits) but that
            //    isn't allowlisted for this specific client - most commonly `sign`/`manage-limits` before
            //    someone's remembered to add them to AllowedScopes - is rejected with `invalid_scope`. This
            //    is deliberately loud: silently dropping a scope the developer explicitly asked for and
            //    expected to receive (e.g. requesting "manage-limits" and then being surprised the token
            //    has no manage-limits claim) is far more confusing than a clear, actionable error up front.
            var knownScopes = AlwaysGrantedScopes.Concat(WalletApiScopes).ToArray();
            var disallowedKnownScopes = requestedScopes
                .Where(s => knownScopes.Contains(s, StringComparer.Ordinal)
                    && !AlwaysGrantedScopes.Contains(s, StringComparer.Ordinal)
                    && !client.AllowedScopes.Contains(s, StringComparer.Ordinal))
                .ToList();
            if (disallowedKnownScopes.Count != 0)
            {
                return (false, "invalid_scope",
                    $"This client is not allowlisted for scope(s): {string.Join(", ", disallowedKnownScopes)}. " +
                    "Add them to this client's AllowedScopes in JwtIssuer:Clients to request them.",
                    null, null);
            }

            var grantedScopes = requestedScopes
                .Where(s => AlwaysGrantedScopes.Contains(s, StringComparer.Ordinal) || client.AllowedScopes.Contains(s, StringComparer.Ordinal))
                .ToList();
            normalized.Scope = string.Join(' ', grantedScopes);

            if (string.Equals(normalized.ResponseType, "id_token", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(normalized.Nonce))
            {
                return (false, "invalid_request", "nonce is required when response_type=id_token.", null, null);
            }

            if (string.Equals(normalized.ResponseType, "code", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(normalized.CodeChallenge))
                {
                    if (client.IsPublicClient)
                    {
                        return (false, "invalid_request", "code_challenge is required for public clients (PKCE, RFC 7636).", null, null);
                    }
                }
                else
                {
                    if (!string.Equals(normalized.CodeChallengeMethod, "S256", StringComparison.Ordinal) &&
                        !string.Equals(normalized.CodeChallengeMethod, "plain", StringComparison.Ordinal))
                    {
                        return (false, "invalid_request", "code_challenge_method must be 'S256' or 'plain'.", null, null);
                    }

                    if (normalized.CodeChallenge.Length < 43 || normalized.CodeChallenge.Length > 128)
                    {
                        return (false, "invalid_request", "code_challenge must be between 43 and 128 characters.", null, null);
                    }
                }
            }

            return (true, null, null, normalized, client);
        }

        public async Task<string> StorePendingAuthorizeRequestAsync(OidcAuthorizeRequest request)
        {
            var requestId = GenerateOpaqueToken(32);
            var key = PendingPrefix + requestId;
            var payload = JsonSerializer.Serialize(request, _jsonOptions);
            await _redis.GetDatabase().StringSetAsync(key, payload, TimeSpan.FromMinutes(10));

            return requestId;
        }

        public async Task<OidcAuthorizeRequest?> GetPendingAuthorizeRequestAsync(string requestId)
        {
            // Atomic get-and-delete: the pending request is one-time-use, so this also consumes it,
            // preventing a narrow-window race where two concurrent callbacks both observe it as present.
            var json = await GetAndDeleteAsync(PendingPrefix + requestId);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return JsonSerializer.Deserialize<OidcAuthorizeRequest>(json, _jsonOptions);
        }

        public Task RemovePendingAuthorizeRequestAsync(string requestId)
        {
            // Already consumed by GetPendingAuthorizeRequestAsync's atomic get-and-delete; this is now a
            // no-op kept for interface/caller compatibility.
            return Task.CompletedTask;
        }

        public async Task<OidcAuthorizeRequest?> PeekPendingAuthorizeRequestAsync(string requestId)
        {
            var value = await _redis.GetDatabase().StringGetAsync(PendingPrefix + requestId);
            if (value.IsNullOrEmpty)
            {
                return null;
            }

            return JsonSerializer.Deserialize<OidcAuthorizeRequest>((string)value!, _jsonOptions);
        }

        /// <summary>
        /// Atomically reads and deletes a one-time-use Redis value (authorization code / refresh token /
        /// pending-authorize-request) so two concurrent requests can never both observe it as present.
        /// </summary>
        private async Task<string?> GetAndDeleteAsync(string key)
        {
            var db = _redis.GetDatabase();
            var value = await db.StringGetDeleteAsync(key);
            return value.IsNullOrEmpty ? null : (string?)value;
        }

        public async Task<(bool Success, string? Error, string? ErrorDescription, Dictionary<string, string>? Response)> CreateAuthorizeResponseAsync(
            OidcAuthorizeRequest request,
            JwtIssuerClientConfiguration client,
            ClaimsPrincipal user,
            string? providerAccessToken,
            string? providerRefreshToken)
        {
            var email = user.FindFirst(ClaimTypes.Email)?.Value;
            if (string.IsNullOrWhiteSpace(email))
            {
                return (false, "access_denied", "Authenticated user does not have an email claim.", null);
            }

            var provider = user.FindFirst(AuthSchemeNames.IdpClaimType)?.Value;

            string? algorandAddress = null;
            try
            {
                algorandAddress = await _driveService.GetAccountAddressAsync(email, provider ?? string.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to load Algorand account for {Email}. Continuing OIDC login without algorand_address claim.", email);
            }

            var shortIdentity = BuildIdentityDisplayName(email, algorandAddress);
            var subject = ComputePairwiseSubject(client.ClientId, email);

            if (string.Equals(request.ResponseType, "id_token", StringComparison.Ordinal))
            {
                var idToken = CreateIdToken(subject, client.ClientId, email, algorandAddress, shortIdentity, request.Nonce);
                var response = new Dictionary<string, string>
                {
                    ["id_token"] = idToken,
                    ["token_type"] = "Bearer",
                    ["expires_in"] = (Current.IdTokenLifetimeMinutes * 60).ToString(),
                    ["state"] = request.State ?? string.Empty
                };
                return (true, null, null, response);
            }

            // Captured now (while the ambient cookie session still has it) so it survives the code
            // exchange and every subsequent refresh - see IProviderAccessTokenProtector and
            // OIDC_INTEGRATION_GUIDE.md's "Provider access token caching" section. Null (never throws) if
            // the caller didn't have a live provider token, or if the protection key isn't configured -
            // either way the issued tokens simply won't carry a provider_token claim, and wallet API
            // callers fall back to supplying their own token explicitly, exactly like before this existed.
            var protectedProviderAccessToken = string.IsNullOrWhiteSpace(providerAccessToken)
                ? null
                : _providerTokenProtector.Protect(providerAccessToken, email);

            // Same reasoning/lifecycle as protectedProviderAccessToken above - captured now because this
            // is the only point with an ambient cookie session to pull it from. See RefreshClaimType and
            // OIDC_INTEGRATION_GUIDE.md's "Provider access token caching" section.
            var protectedProviderRefreshToken = string.IsNullOrWhiteSpace(providerRefreshToken)
                ? null
                : _providerTokenProtector.Protect(providerRefreshToken, email);

            var code = GenerateOpaqueToken(48);
            var codeData = new AuthorizationCodeRecord
            {
                Code = code,
                ClientId = client.ClientId,
                RedirectUri = request.RedirectUri!,
                Scope = request.Scope,
                Nonce = request.Nonce,
                Email = email,
                AlgorandAddress = algorandAddress,
                Provider = provider,
                Subject = subject,
                ShortIdentity = shortIdentity,
                CodeChallenge = request.CodeChallenge,
                CodeChallengeMethod = request.CodeChallengeMethod,
                ProtectedProviderAccessToken = protectedProviderAccessToken,
                ProtectedProviderRefreshToken = protectedProviderRefreshToken,
                CreatedUtc = DateTimeOffset.UtcNow,
                ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(Current.AuthorizationCodeLifetimeSeconds)
            };

            await _redis.GetDatabase().StringSetAsync(CodePrefix + code,
                JsonSerializer.Serialize(codeData, _jsonOptions),
                TimeSpan.FromSeconds(Current.AuthorizationCodeLifetimeSeconds));

            var responseForCode = new Dictionary<string, string>
            {
                ["code"] = code,
                ["state"] = request.State ?? string.Empty
            };

            return (true, null, null, responseForCode);
        }

        public async Task<(bool Success, int StatusCode, string? Error, string? ErrorDescription, OidcTokenResponse? Response)> ExchangeTokenAsync(OidcTokenRequest request, string? basicAuthHeader)
        {
            var clientValidation = ValidateClientAuthentication(request.ClientId, request.ClientSecret, basicAuthHeader);
            if (!clientValidation.Success)
            {
                return (false, clientValidation.StatusCode, clientValidation.Error, clientValidation.ErrorDescription, null);
            }

            var client = clientValidation.Client!;

            if (string.Equals(request.GrantType, "authorization_code", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(request.Code))
                {
                    return (false, 400, "invalid_request", "Missing code.", null);
                }

                var codeKey = CodePrefix + request.Code;
                var codeJson = await GetAndDeleteAsync(codeKey);
                if (string.IsNullOrWhiteSpace(codeJson))
                {
                    return (false, 400, "invalid_grant", "Authorization code is invalid or expired.", null);
                }

                var codeRecord = JsonSerializer.Deserialize<AuthorizationCodeRecord>(codeJson, _jsonOptions);
                if (codeRecord == null)
                {
                    return (false, 400, "invalid_grant", "Authorization code payload is invalid.", null);
                }

                if (!string.Equals(codeRecord.ClientId, client.ClientId, StringComparison.Ordinal))
                {
                    return (false, 400, "invalid_grant", "Authorization code does not belong to this client.", null);
                }

                if (!string.Equals(codeRecord.RedirectUri, request.RedirectUri, StringComparison.Ordinal))
                {
                    return (false, 400, "invalid_grant", "redirect_uri does not match the authorization request.", null);
                }

                var pkceError = ValidatePkce(codeRecord.CodeChallenge, codeRecord.CodeChallengeMethod, request.CodeVerifier);
                if (pkceError != null)
                {
                    return (false, 400, "invalid_grant", pkceError, null);
                }

                if (DateTimeOffset.UtcNow >= codeRecord.ExpiresUtc)
                {
                    return (false, 400, "invalid_grant", "Authorization code expired.", null);
                }

                var response = await BuildTokenResponseAsync(
                    client.ClientId,
                    codeRecord.Subject,
                    codeRecord.Email,
                    codeRecord.AlgorandAddress,
                    codeRecord.Provider,
                    codeRecord.ShortIdentity,
                    codeRecord.Nonce,
                    codeRecord.Scope,
                    codeRecord.ProtectedProviderAccessToken,
                    codeRecord.ProtectedProviderRefreshToken,
                    includeRefreshToken: true);

                return (true, 200, null, null, response);
            }

            if (string.Equals(request.GrantType, "refresh_token", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(request.RefreshToken))
                {
                    return (false, 400, "invalid_request", "Missing refresh_token.", null);
                }

                var refreshKey = RefreshPrefix + request.RefreshToken;
                var refreshJson = await GetAndDeleteAsync(refreshKey);
                if (string.IsNullOrWhiteSpace(refreshJson))
                {
                    return (false, 400, "invalid_grant", "Refresh token is invalid or expired.", null);
                }

                var refreshRecord = JsonSerializer.Deserialize<RefreshTokenRecord>(refreshJson, _jsonOptions);
                if (refreshRecord == null)
                {
                    return (false, 400, "invalid_grant", "Refresh token payload is invalid.", null);
                }

                if (!string.Equals(refreshRecord.ClientId, client.ClientId, StringComparison.Ordinal))
                {
                    return (false, 400, "invalid_grant", "Refresh token does not belong to this client.", null);
                }

                if (DateTimeOffset.UtcNow >= refreshRecord.ExpiresUtc)
                {
                    return (false, 400, "invalid_grant", "Refresh token expired.", null);
                }

                var (protectedProviderAccessToken, protectedProviderRefreshToken) =
                    await RenewProviderTokenAsync(refreshRecord.Provider, refreshRecord.Email, refreshRecord.ProtectedProviderAccessToken, refreshRecord.ProtectedProviderRefreshToken);

                var response = await BuildTokenResponseAsync(
                    client.ClientId,
                    refreshRecord.Subject,
                    refreshRecord.Email,
                    refreshRecord.AlgorandAddress,
                    refreshRecord.Provider,
                    refreshRecord.ShortIdentity,
                    nonce: null,
                    refreshRecord.Scope,
                    protectedProviderAccessToken,
                    protectedProviderRefreshToken,
                    includeRefreshToken: true);

                return (true, 200, null, null, response);
            }

            return (false, 400, "unsupported_grant_type", "Supported grant_type values are authorization_code and refresh_token.", null);
        }

        public (bool IsValid, ClaimsPrincipal? Principal, IDictionary<string, object>? Claims, string? Error) ValidateBearerAccessToken(string token)
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            var parameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new RsaSecurityKey(_rsa.ExportParameters(false)),
                ValidateIssuer = true,
                ValidIssuer = Current.Issuer,
                // Access tokens' aud is always the requesting client_id (see CreateAccessToken). These three
                // endpoints (/userinfo, /introspect, /verify) are shared by every registered client, so we
                // validate the aud is still one of our currently-registered clients rather than a single fixed
                // value - this rejects tokens whose client has since been deregistered and defends against any
                // aud tampering, without breaking any legitimate client's token.
                ValidateAudience = true,
                ValidAudiences = Current.Clients.Select(c => c.ClientId),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(1)
            };

            try
            {
                var principal = tokenHandler.ValidateToken(token, parameters, out var validatedToken);
                if (validatedToken is not JwtSecurityToken jwt)
                {
                    return (false, null, null, "Invalid token format.");
                }

                var tokenUse = principal.FindFirstValue("token_use");
                if (!string.Equals(tokenUse, "access_token", StringComparison.Ordinal))
                {
                    return (false, null, null, "Provided token is not an access token.");
                }

                var claims = jwt.Claims.ToDictionary(c => c.Type, c => (object)c.Value, StringComparer.Ordinal);
                return (true, principal, claims, null);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Access token validation failed");
                return (false, null, null, "invalid_token");
            }
        }

        public string? TryGetAudienceFromSelfIssuedToken(string token)
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            var parameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new RsaSecurityKey(_rsa.ExportParameters(false)),
                ValidateIssuer = true,
                ValidIssuer = Current.Issuer,
                ValidateAudience = false,
                ValidateLifetime = false
            };

            try
            {
                tokenHandler.ValidateToken(token, parameters, out var validatedToken);
                return validatedToken is JwtSecurityToken jwt ? jwt.Audiences.FirstOrDefault() : null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "id_token_hint signature/issuer validation failed; ignoring its aud claim.");
                return null;
            }
        }

        public async Task<Dictionary<string, object>> IntrospectAsync(string token)
        {
            var result = ValidateBearerAccessToken(token);
            if (!result.IsValid || result.Claims == null)
            {
                return new Dictionary<string, object>
                {
                    ["active"] = false
                };
            }

            var response = new Dictionary<string, object>
            {
                ["active"] = true,
                ["scope"] = result.Claims.TryGetValue("scope", out var scope) ? scope : string.Empty,
                ["client_id"] = result.Claims.TryGetValue("client_id", out var clientId) ? clientId : string.Empty,
                ["sub"] = result.Claims.TryGetValue(JwtRegisteredClaimNames.Sub, out var sub) ? sub : string.Empty,
                ["exp"] = result.Claims.TryGetValue(JwtRegisteredClaimNames.Exp, out var exp) ? exp : string.Empty,
                ["iat"] = result.Claims.TryGetValue(JwtRegisteredClaimNames.Iat, out var iat) ? iat : string.Empty,
                ["iss"] = result.Claims.TryGetValue(JwtRegisteredClaimNames.Iss, out var iss) ? iss : string.Empty,
                ["email"] = result.Claims.TryGetValue(ClaimTypes.Email, out var email) ? email : string.Empty,
                ["algorand_address"] = result.Claims.TryGetValue("algorand_address", out var address) ? address : string.Empty
            };

            return await Task.FromResult(response);
        }

        /// <summary>
        /// Called on every <c>grant_type=refresh_token</c> exchange - a server-to-server call with no
        /// ambient cookie session, so the only way to keep the cached provider (Google/Microsoft) access
        /// token fresh is to spend the cached provider *refresh* token, if one was captured at sign-in
        /// (see <see cref="ProviderAccessTokenProtector.RefreshClaimType"/>). This is what makes the
        /// wallet API keep working indefinitely across Biatec token renewals instead of silently carrying
        /// forward a provider access token that will eventually expire and start failing (the bug this
        /// was added to fix - see OIDC_INTEGRATION_GUIDE.md's "Provider access token caching" section).
        /// </summary>
        /// <returns>
        /// The (possibly renewed) protected access/refresh token pair to carry onto the new access/refresh
        /// tokens. Falls back to the inputs unchanged if there's no cached provider refresh token, or the
        /// renewal attempt fails (revoked/expired refresh token) - exactly the previous behavior, so a
        /// caller with no provider refresh token cached (e.g. one that signed in before this existed) sees
        /// no change: they still need a fresh interactive sign-in once the provider access token expires.
        /// </returns>
        private async Task<(string? ProtectedProviderAccessToken, string? ProtectedProviderRefreshToken)> RenewProviderTokenAsync(
            string? provider,
            string email,
            string? protectedProviderAccessToken,
            string? protectedProviderRefreshToken)
        {
            if (string.IsNullOrWhiteSpace(protectedProviderRefreshToken))
            {
                return (protectedProviderAccessToken, protectedProviderRefreshToken);
            }

            var providerRefreshToken = _providerTokenProtector.Unprotect(protectedProviderRefreshToken, email);
            if (string.IsNullOrWhiteSpace(providerRefreshToken))
            {
                return (protectedProviderAccessToken, protectedProviderRefreshToken);
            }

            try
            {
                var storageProvider = _providerCatalog.Resolve(provider);
                var renewed = await storageProvider.RefreshAccessTokenAsync(providerRefreshToken);
                if (renewed == null)
                {
                    // Revoked/expired provider refresh token - fall back to carrying the old (possibly
                    // already stale) access token forward unchanged, same as before this existed.
                    return (protectedProviderAccessToken, protectedProviderRefreshToken);
                }

                var renewedProtectedAccessToken = _providerTokenProtector.Protect(renewed.AccessToken, email);

                // Google normally doesn't rotate the refresh token on renewal; Microsoft Entra ID always
                // does. Only replace the cached refresh token when a new one actually came back, otherwise
                // keep the one that just proved itself valid.
                var renewedProtectedRefreshToken = string.IsNullOrWhiteSpace(renewed.RefreshToken)
                    ? protectedProviderRefreshToken
                    : _providerTokenProtector.Protect(renewed.RefreshToken, email);

                return (renewedProtectedAccessToken, renewedProtectedRefreshToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to renew the cached provider access token during a Biatec token refresh; carrying the previous one forward unchanged.");
                return (protectedProviderAccessToken, protectedProviderRefreshToken);
            }
        }

        private async Task<OidcTokenResponse> BuildTokenResponseAsync(
            string clientId,
            string subject,
            string email,
            string? algorandAddress,
            string? provider,
            string shortIdentity,
            string? nonce,
            string scope,
            string? protectedProviderAccessToken,
            string? protectedProviderRefreshToken,
            bool includeRefreshToken)
        {
            var accessToken = CreateAccessToken(subject, clientId, email, algorandAddress, provider, shortIdentity, scope, protectedProviderAccessToken, protectedProviderRefreshToken);
            var idToken = CreateIdToken(subject, clientId, email, algorandAddress, shortIdentity, nonce);

            string? refreshToken = null;
            if (includeRefreshToken)
            {
                refreshToken = GenerateOpaqueToken(48);
                var refreshRecord = new RefreshTokenRecord
                {
                    RefreshToken = refreshToken,
                    ClientId = clientId,
                    Subject = subject,
                    Email = email,
                    AlgorandAddress = algorandAddress,
                    Provider = provider,
                    ShortIdentity = shortIdentity,
                    Scope = scope,
                    // Renewed (via RenewProviderTokenAsync) whenever a cached provider refresh token is
                    // available - see ProtectedProviderRefreshToken below and OIDC_INTEGRATION_GUIDE.md's
                    // "Provider access token caching" section. Falls back to being carried forward
                    // unchanged only when there's no provider refresh token cached at all (a session
                    // predating this feature) or the provider refresh token itself has been revoked/expired
                    // - at that point the caller needs a fresh interactive sign-in to re-cache one.
                    ProtectedProviderAccessToken = protectedProviderAccessToken,
                    ProtectedProviderRefreshToken = protectedProviderRefreshToken,
                    CreatedUtc = DateTimeOffset.UtcNow,
                    ExpiresUtc = DateTimeOffset.UtcNow.AddDays(Current.RefreshTokenLifetimeDays)
                };

                await _redis.GetDatabase().StringSetAsync(RefreshPrefix + refreshToken,
                    JsonSerializer.Serialize(refreshRecord, _jsonOptions),
                    TimeSpan.FromDays(Current.RefreshTokenLifetimeDays));
            }

            return new OidcTokenResponse
            {
                AccessToken = accessToken,
                IdToken = idToken,
                ExpiresIn = Current.AccessTokenLifetimeMinutes * 60,
                RefreshToken = refreshToken,
                Scope = scope
            };
        }

        private string CreateIdToken(string subject, string audience, string email, string? algorandAddress, string shortIdentity, string? nonce)
        {
            var now = DateTimeOffset.UtcNow;
            var expires = now.AddMinutes(Current.IdTokenLifetimeMinutes);
            var claims = new List<Claim>
            {
                new(JwtRegisteredClaimNames.Sub, subject),
                new(JwtRegisteredClaimNames.Email, email),
                new(ClaimTypes.Email, email),
                new(ClaimTypes.Name, shortIdentity),
                new("preferred_username", shortIdentity),
                new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
            };

            if (!string.IsNullOrWhiteSpace(algorandAddress))
            {
                claims.Add(new Claim("algorand_address", algorandAddress));
            }

            if (!string.IsNullOrWhiteSpace(nonce))
            {
                claims.Add(new Claim("nonce", nonce));
            }

            var token = new JwtSecurityToken(
                issuer: Current.Issuer,
                audience: audience,
                claims: claims,
                notBefore: now.UtcDateTime,
                expires: expires.UtcDateTime,
                signingCredentials: _signingCredentials);

            token.Header[JwtHeaderParameterNames.Kid] = Current.KeyId;
            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        /// <summary>
        /// OIDC scopes that, when granted, stamp a matching claim of the same name (value <c>"true"</c>)
        /// onto the issued access token - checked by <see cref="BiatecOIDC.Controllers.WalletController"/>
        /// to authorize <c>/wallet/sign</c> (requires <c>sign</c>), the spending-limit endpoints (requires
        /// <c>manage-limits</c>), and signing any transaction group that contains a <c>rekey</c> field
        /// (requires <c>rekey</c> - the most dangerous scope: it permanently reassigns which key controls
        /// an account, so a leaked token/session carrying it risks total loss of funds, not just spend up to
        /// a limit). Deliberately explicit claims rather than re-parsing the <c>scope</c> claim's
        /// space-separated string at authorization time.
        /// </summary>
        private static readonly string[] WalletApiScopes = { "sign", "manage-limits", "rekey" };

        /// <summary>
        /// Basic identity scopes every client can always be granted, regardless of its configured
        /// <see cref="JwtIssuerClientConfiguration.AllowedScopes"/> - only the wallet-API scopes
        /// (<see cref="WalletApiScopes"/>) are actually gated by that allowlist. See
        /// <see cref="ValidateAuthorizeRequestAsync"/>'s scope-narrowing logic.
        /// </summary>
        private static readonly string[] AlwaysGrantedScopes = { "openid", "profile", "email" };

        private string CreateAccessToken(string subject, string clientId, string email, string? algorandAddress, string? provider, string shortIdentity, string scope, string? protectedProviderAccessToken, string? protectedProviderRefreshToken)
        {
            var now = DateTimeOffset.UtcNow;
            var expires = now.AddMinutes(Current.AccessTokenLifetimeMinutes);
            var claims = new List<Claim>
            {
                new(JwtRegisteredClaimNames.Sub, subject),
                new(ClaimTypes.Email, email),
                new(ClaimTypes.Name, shortIdentity),
                new("client_id", clientId),
                new("scope", scope),
                new("token_use", "access_token"),
                new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
            };

            if (!string.IsNullOrWhiteSpace(algorandAddress))
            {
                claims.Add(new Claim("algorand_address", algorandAddress));
            }

            if (!string.IsNullOrWhiteSpace(provider))
            {
                claims.Add(new Claim(AuthSchemeNames.IdpClaimType, provider));
            }

            var grantedScopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var walletScope in WalletApiScopes)
            {
                if (grantedScopes.Contains(walletScope, StringComparer.Ordinal))
                {
                    claims.Add(new Claim(walletScope, "true"));
                }
            }

            // Encrypted (never plaintext) cached Google/Microsoft access token, so wallet API callers don't
            // have to separately manage/resend their own provider token on every call - see
            // ProviderAccessTokenProtector and OIDC_INTEGRATION_GUIDE.md's "Provider access token caching"
            // section for the full design/threat-model writeup. Only present when one was actually
            // captured and successfully encrypted (see CreateAuthorizeResponseAsync/BuildTokenResponseAsync).
            if (!string.IsNullOrWhiteSpace(protectedProviderAccessToken))
            {
                claims.Add(new Claim(ProviderAccessTokenProtector.ClaimType, protectedProviderAccessToken));
            }

            // Encrypted (never plaintext) cached Google/Microsoft refresh token - lets WalletController
            // renew the provider_token claim above on the fly, within this access token's own lifetime,
            // if the cached provider access token goes stale before this Biatec token itself expires. Same
            // caveats as protectedProviderAccessToken: only present when one was actually captured and
            // successfully encrypted.
            if (!string.IsNullOrWhiteSpace(protectedProviderRefreshToken))
            {
                claims.Add(new Claim(ProviderAccessTokenProtector.RefreshClaimType, protectedProviderRefreshToken));
            }

            var token = new JwtSecurityToken(
                issuer: Current.Issuer,
                audience: clientId,
                claims: claims,
                notBefore: now.UtcDateTime,
                expires: expires.UtcDateTime,
                signingCredentials: _signingCredentials);

            token.Header[JwtHeaderParameterNames.Kid] = Current.KeyId;
            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        private (bool Success, int StatusCode, string? Error, string? ErrorDescription, JwtIssuerClientConfiguration? Client) ValidateClientAuthentication(
            string? bodyClientId,
            string? bodyClientSecret,
            string? basicAuthHeader)
        {
            var (headerClientId, headerClientSecret) = ParseBasicAuth(basicAuthHeader);

            var clientId = !string.IsNullOrWhiteSpace(bodyClientId) ? bodyClientId : headerClientId;
            var clientSecret = !string.IsNullOrWhiteSpace(bodyClientSecret) ? bodyClientSecret : headerClientSecret;

            if (string.IsNullOrWhiteSpace(clientId))
            {
                return (false, 401, "invalid_client", "Missing client_id.", null);
            }

            var client = Current.Clients.FirstOrDefault(c => string.Equals(c.ClientId, clientId, StringComparison.Ordinal));
            if (client == null)
            {
                return (false, 401, "invalid_client", "Unknown client_id.", null);
            }

            if (!string.IsNullOrWhiteSpace(client.ClientSecret) && !FixedTimeSecretsEqual(client.ClientSecret, clientSecret))
            {
                return (false, 401, "invalid_client", "Invalid client credentials.", null);
            }

            return (true, 200, null, null, client);
        }

        /// <summary>
        /// Compares client secrets in constant time to avoid a timing side-channel that could otherwise let
        /// an attacker recover a confidential client's secret character-by-character.
        /// </summary>
        private static bool FixedTimeSecretsEqual(string expected, string? actual)
        {
            var expectedBytes = Encoding.UTF8.GetBytes(expected);
            var actualBytes = Encoding.UTF8.GetBytes(actual ?? string.Empty);

            return expectedBytes.Length == actualBytes.Length
                && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
        }

        private static (string? ClientId, string? ClientSecret) ParseBasicAuth(string? authorizationHeader)
        {
            if (string.IsNullOrWhiteSpace(authorizationHeader))
            {
                return (null, null);
            }

            const string prefix = "Basic ";
            if (!authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return (null, null);
            }

            try
            {
                var base64 = authorizationHeader[prefix.Length..].Trim();
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                var separatorIndex = decoded.IndexOf(':');
                if (separatorIndex <= 0)
                {
                    return (null, null);
                }

                var clientId = decoded[..separatorIndex];
                var clientSecret = decoded[(separatorIndex + 1)..];
                return (clientId, clientSecret);
            }
            catch
            {
                return (null, null);
            }
        }

        private RSA LoadOrCreateSigningKey()
        {
            var rawConfiguredValue = Current.SigningPrivateKeyPem?.Trim();
            var pem = ResolveConfiguredPem(rawConfiguredValue)?.Replace("\\n", "\n", StringComparison.Ordinal)?.Trim();
            if (!string.IsNullOrWhiteSpace(pem))
            {
                if (pem.Contains("BEGIN OPENSSH PRIVATE KEY", StringComparison.Ordinal))
                {
                    _logger.LogError("JwtIssuer SigningPrivateKeyPem uses unsupported OpenSSH key format. Provide a PEM encoded RSA private key with header 'BEGIN PRIVATE KEY' or 'BEGIN RSA PRIVATE KEY'.");
                }

                try
                {
                    var rsa = RSA.Create();
                    rsa.ImportFromPem(pem);
                    return rsa;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to import configured JwtIssuer signing key. Falling back to ephemeral key.");
                }
            }

            if (!_environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    "JwtIssuer:SigningPrivateKeyPem is missing or invalid. Refusing to start with a silent " +
                    "ephemeral signing key outside Development, since that would invalidate all previously " +
                    "issued tokens and diverge signing keys across replicas without any operator-visible alert. " +
                    "Configure a valid PEM-encoded RSA private key (PKCS#8 or PKCS#1).");
            }

            _logger.LogWarning("JwtIssuer signing key not configured. Using ephemeral RSA key. Tokens become invalid after restart.");
            return RSA.Create(2048);
        }

        private string? ResolveConfiguredPem(string? configuredValue)
        {
            if (string.IsNullOrWhiteSpace(configuredValue))
            {
                return null;
            }

            // Allow either inline PEM content or a local file path to the PEM file.
            if (File.Exists(configuredValue))
            {
                try
                {
                    return File.ReadAllText(configuredValue);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unable to read JwtIssuer signing key file at path: {Path}", configuredValue);
                    return null;
                }
            }

            return configuredValue;
        }

        private static string BuildShortIdentity(string algorandAddress)
        {
            if (string.IsNullOrWhiteSpace(algorandAddress))
            {
                return string.Empty;
            }

            if (algorandAddress.Length <= 8)
            {
                return algorandAddress;
            }

            return $"{algorandAddress[..4]}{algorandAddress[^4..]}";
        }

        private static string BuildIdentityDisplayName(string email, string? algorandAddress)
        {
            if (!string.IsNullOrWhiteSpace(algorandAddress))
            {
                return BuildShortIdentity(algorandAddress);
            }

            var atIndex = email.IndexOf('@');
            if (atIndex > 0)
            {
                return email[..atIndex];
            }

            return email;
        }

        private string ComputePairwiseSubject(string clientId, string email)
        {
            var seed = $"{Current.Issuer}|{clientId}|{email.Trim().ToLowerInvariant()}";
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
            return Base64UrlEncoder.Encode(bytes);
        }

        private static string? ValidatePkce(string? codeChallenge, string? codeChallengeMethod, string? codeVerifier)
        {
            if (string.IsNullOrWhiteSpace(codeChallenge))
            {
                // Authorization request was made without PKCE (confidential client that opted out).
                return null;
            }

            if (string.IsNullOrWhiteSpace(codeVerifier))
            {
                return "code_verifier is required for this authorization code.";
            }

            if (codeVerifier.Length < 43 || codeVerifier.Length > 128)
            {
                return "code_verifier must be between 43 and 128 characters.";
            }

            string computedChallenge;
            if (string.Equals(codeChallengeMethod, "S256", StringComparison.Ordinal))
            {
                var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
                computedChallenge = Base64UrlEncoder.Encode(hash);
            }
            else
            {
                computedChallenge = codeVerifier;
            }

            return string.Equals(computedChallenge, codeChallenge, StringComparison.Ordinal)
                ? null
                : "code_verifier does not match code_challenge.";
        }

        private static string GenerateOpaqueToken(int size)
        {
            return Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(size));
        }

        private sealed class AuthorizationCodeRecord
        {
            public string Code { get; set; } = string.Empty;
            public string ClientId { get; set; } = string.Empty;
            public string RedirectUri { get; set; } = string.Empty;
            public string Scope { get; set; } = string.Empty;
            public string? Nonce { get; set; }
            public string Email { get; set; } = string.Empty;
            public string? AlgorandAddress { get; set; }
            public string? Provider { get; set; }
            public string Subject { get; set; } = string.Empty;
            public string ShortIdentity { get; set; } = string.Empty;
            public string? CodeChallenge { get; set; }
            public string? CodeChallengeMethod { get; set; }

            /// <summary>
            /// The caller's Google/Microsoft access token, AES-256-GCM encrypted under a dedicated key (see
            /// <c>IProviderAccessTokenProtector</c>) - never plaintext, even here. Captured at
            /// <c>CreateAuthorizeResponseAsync</c>-time (while the ambient cookie session still has it) so
            /// it survives to the code-exchange step, which has no cookie session of its own.
            /// </summary>
            public string? ProtectedProviderAccessToken { get; set; }

            /// <summary>
            /// The caller's Google/Microsoft refresh token, AES-256-GCM encrypted the same way as
            /// <see cref="ProtectedProviderAccessToken"/> (see <c>ProviderAccessTokenProtector.RefreshClaimType</c>).
            /// Carried onto the first <c>RefreshTokenRecord</c> so subsequent Biatec token renewals can keep
            /// renewing the provider access token instead of just carrying it forward until it expires.
            /// </summary>
            public string? ProtectedProviderRefreshToken { get; set; }

            public DateTimeOffset CreatedUtc { get; set; }
            public DateTimeOffset ExpiresUtc { get; set; }
        }

        private sealed class RefreshTokenRecord
        {
            public string RefreshToken { get; set; } = string.Empty;
            public string ClientId { get; set; } = string.Empty;
            public string Subject { get; set; } = string.Empty;
            public string Email { get; set; } = string.Empty;
            public string? AlgorandAddress { get; set; }
            public string? Provider { get; set; }
            public string ShortIdentity { get; set; } = string.Empty;
            public string Scope { get; set; } = "openid profile email";

            /// <summary>
            /// Renewed on every refresh (via <see cref="RenewProviderTokenAsync"/>) whenever
            /// <see cref="ProtectedProviderRefreshToken"/> is available - AES-256-GCM encrypted, never
            /// plaintext. Falls back to being carried forward unchanged only when there's no provider
            /// refresh token cached, or renewing it fails (revoked/expired) - at that point the caller
            /// needs a fresh interactive sign-in to re-cache one.
            /// </summary>
            public string? ProtectedProviderAccessToken { get; set; }

            /// <summary>
            /// The caller's Google/Microsoft refresh token, AES-256-GCM encrypted the same way as
            /// <see cref="ProtectedProviderAccessToken"/>. Carried forward unchanged from the previous
            /// record unless the provider actually rotated it on the last renewal (Microsoft Entra ID
            /// always does; Google normally doesn't) - see <see cref="RenewProviderTokenAsync"/>.
            /// </summary>
            public string? ProtectedProviderRefreshToken { get; set; }

            public DateTimeOffset CreatedUtc { get; set; }
            public DateTimeOffset ExpiresUtc { get; set; }
        }
    }
}
