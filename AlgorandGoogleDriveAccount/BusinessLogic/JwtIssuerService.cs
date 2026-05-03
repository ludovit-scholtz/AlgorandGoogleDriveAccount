using AlgorandGoogleDriveAccount.Model;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AlgorandGoogleDriveAccount.BusinessLogic
{
    public class JwtIssuerService : IJwtIssuerService
    {
        private const string PendingPrefix = "oidc:pending:";
        private const string CodePrefix = "oidc:code:";
        private const string RefreshPrefix = "oidc:refresh:";

        private readonly IDistributedCache _cache;
        private readonly IOptionsMonitor<JwtIssuerConfiguration> _config;
        private readonly IDriveService _driveService;
        private readonly ILogger<JwtIssuerService> _logger;
        private readonly RSA _rsa;
        private readonly SigningCredentials _signingCredentials;

        private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public JwtIssuerService(
            IDistributedCache cache,
            IOptionsMonitor<JwtIssuerConfiguration> config,
            IDriveService driveService,
            ILogger<JwtIssuerService> logger)
        {
            _cache = cache;
            _config = config;
            _driveService = driveService;
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
                userinfo_endpoint = $"{issuer}/userinfo",
                introspection_endpoint = $"{issuer}/introspect",
                jwks_uri = $"{issuer}/.well-known/jwks.json",
                response_types_supported = new[] { "code", "id_token" },
                response_modes_supported = new[] { "query", "form_post" },
                grant_types_supported = new[] { "authorization_code", "refresh_token" },
                subject_types_supported = new[] { "pairwise" },
                id_token_signing_alg_values_supported = new[] { "RS256" },
                token_endpoint_auth_methods_supported = new[] { "client_secret_post", "client_secret_basic", "none" },
                scopes_supported = new[] { "openid", "profile", "email" },
                claims_supported = new[] { "sub", "iss", "aud", "exp", "iat", "nbf", "nonce", "email", "name", "preferred_username", "algorand_address" }
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
                Nonce = request.Nonce
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
                    .Where(c => c.RedirectUris.Any(r => UriEquals(r, normalized.RedirectUri)))
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

            if (!client.RedirectUris.Any(r => UriEquals(r, normalized.RedirectUri)))
            {
                return (false, "invalid_request", "redirect_uri is not allowlisted for this client_id.", null, null);
            }

            var disallowedScopes = requestedScopes
                .Where(s => !client.AllowedScopes.Contains(s, StringComparer.Ordinal))
                .ToList();
            if (disallowedScopes.Count != 0)
            {
                return (false, "invalid_scope", $"Unsupported scope(s) for client: {string.Join(", ", disallowedScopes)}", null, null);
            }

            if (string.Equals(normalized.ResponseType, "id_token", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(normalized.Nonce))
            {
                return (false, "invalid_request", "nonce is required when response_type=id_token.", null, null);
            }

            return (true, null, null, normalized, client);
        }

        public async Task<string> StorePendingAuthorizeRequestAsync(OidcAuthorizeRequest request)
        {
            var requestId = GenerateOpaqueToken(32);
            var key = PendingPrefix + requestId;
            var payload = JsonSerializer.Serialize(request, _jsonOptions);
            await _cache.SetStringAsync(key, payload, new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10)
            });

            return requestId;
        }

        public async Task<OidcAuthorizeRequest?> GetPendingAuthorizeRequestAsync(string requestId)
        {
            var key = PendingPrefix + requestId;
            var json = await _cache.GetStringAsync(key);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return JsonSerializer.Deserialize<OidcAuthorizeRequest>(json, _jsonOptions);
        }

        public Task RemovePendingAuthorizeRequestAsync(string requestId)
        {
            return _cache.RemoveAsync(PendingPrefix + requestId);
        }

        public async Task<(bool Success, string? Error, string? ErrorDescription, Dictionary<string, string>? Response)> CreateAuthorizeResponseAsync(
            OidcAuthorizeRequest request,
            JwtIssuerClientConfiguration client,
            ClaimsPrincipal user)
        {
            var email = user.FindFirst(ClaimTypes.Email)?.Value;
            if (string.IsNullOrWhiteSpace(email))
            {
                return (false, "access_denied", "Authenticated user does not have an email claim.", null);
            }

            string algorandAddress;
            try
            {
                algorandAddress = await _driveService.GetAccountAddressAsync(email);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to load Algorand account for {Email}", email);
                return (false, "server_error", "Unable to load Algorand account from Google Drive.", null);
            }

            var shortIdentity = BuildShortIdentity(algorandAddress);
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
                Subject = subject,
                ShortIdentity = shortIdentity,
                CreatedUtc = DateTimeOffset.UtcNow,
                ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(Current.AuthorizationCodeLifetimeSeconds)
            };

            await _cache.SetStringAsync(CodePrefix + code,
                JsonSerializer.Serialize(codeData, _jsonOptions),
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(Current.AuthorizationCodeLifetimeSeconds)
                });

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
                var codeJson = await _cache.GetStringAsync(codeKey);
                if (string.IsNullOrWhiteSpace(codeJson))
                {
                    return (false, 400, "invalid_grant", "Authorization code is invalid or expired.", null);
                }

                await _cache.RemoveAsync(codeKey);

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

                if (DateTimeOffset.UtcNow >= codeRecord.ExpiresUtc)
                {
                    return (false, 400, "invalid_grant", "Authorization code expired.", null);
                }

                var response = await BuildTokenResponseAsync(
                    client.ClientId,
                    codeRecord.Subject,
                    codeRecord.Email,
                    codeRecord.AlgorandAddress,
                    codeRecord.ShortIdentity,
                    codeRecord.Nonce,
                    codeRecord.Scope,
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
                var refreshJson = await _cache.GetStringAsync(refreshKey);
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

                var response = await BuildTokenResponseAsync(
                    client.ClientId,
                    refreshRecord.Subject,
                    refreshRecord.Email,
                    refreshRecord.AlgorandAddress,
                    refreshRecord.ShortIdentity,
                    nonce: null,
                    refreshRecord.Scope,
                    includeRefreshToken: true);

                await _cache.RemoveAsync(refreshKey);
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
                ValidateAudience = false,
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

        private async Task<OidcTokenResponse> BuildTokenResponseAsync(
            string clientId,
            string subject,
            string email,
            string algorandAddress,
            string shortIdentity,
            string? nonce,
            string scope,
            bool includeRefreshToken)
        {
            var accessToken = CreateAccessToken(subject, clientId, email, algorandAddress, shortIdentity, scope);
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
                    ShortIdentity = shortIdentity,
                    Scope = scope,
                    CreatedUtc = DateTimeOffset.UtcNow,
                    ExpiresUtc = DateTimeOffset.UtcNow.AddDays(Current.RefreshTokenLifetimeDays)
                };

                await _cache.SetStringAsync(RefreshPrefix + refreshToken,
                    JsonSerializer.Serialize(refreshRecord, _jsonOptions),
                    new DistributedCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(Current.RefreshTokenLifetimeDays)
                    });
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

        private string CreateIdToken(string subject, string audience, string email, string algorandAddress, string shortIdentity, string? nonce)
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
                new("algorand_address", algorandAddress),
                new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
            };

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

        private string CreateAccessToken(string subject, string clientId, string email, string algorandAddress, string shortIdentity, string scope)
        {
            var now = DateTimeOffset.UtcNow;
            var expires = now.AddMinutes(Current.AccessTokenLifetimeMinutes);
            var claims = new List<Claim>
            {
                new(JwtRegisteredClaimNames.Sub, subject),
                new(ClaimTypes.Email, email),
                new(ClaimTypes.Name, shortIdentity),
                new("algorand_address", algorandAddress),
                new("client_id", clientId),
                new("scope", scope),
                new("token_use", "access_token"),
                new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
            };

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

            if (!string.IsNullOrWhiteSpace(client.ClientSecret) && !string.Equals(client.ClientSecret, clientSecret, StringComparison.Ordinal))
            {
                return (false, 401, "invalid_client", "Invalid client credentials.", null);
            }

            return (true, 200, null, null, client);
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
            var pem = Current.SigningPrivateKeyPem?.Replace("\\n", "\n", StringComparison.Ordinal)?.Trim();
            if (!string.IsNullOrWhiteSpace(pem))
            {
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

            _logger.LogWarning("JwtIssuer signing key not configured. Using ephemeral RSA key. Tokens become invalid after restart.");
            return RSA.Create(2048);
        }

        private static bool UriEquals(string configuredUri, string? actualUri)
        {
            if (string.IsNullOrWhiteSpace(configuredUri) || string.IsNullOrWhiteSpace(actualUri))
            {
                return false;
            }

            if (!Uri.TryCreate(configuredUri, UriKind.Absolute, out var configured))
            {
                return false;
            }

            if (!Uri.TryCreate(actualUri, UriKind.Absolute, out var actual))
            {
                return false;
            }

            return Uri.Compare(configured, actual, UriComponents.AbsoluteUri, UriFormat.SafeUnescaped, StringComparison.Ordinal) == 0;
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

        private string ComputePairwiseSubject(string clientId, string email)
        {
            var seed = $"{Current.Issuer}|{clientId}|{email.Trim().ToLowerInvariant()}";
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
            return Base64UrlEncoder.Encode(bytes);
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
            public string AlgorandAddress { get; set; } = string.Empty;
            public string Subject { get; set; } = string.Empty;
            public string ShortIdentity { get; set; } = string.Empty;
            public DateTimeOffset CreatedUtc { get; set; }
            public DateTimeOffset ExpiresUtc { get; set; }
        }

        private sealed class RefreshTokenRecord
        {
            public string RefreshToken { get; set; } = string.Empty;
            public string ClientId { get; set; } = string.Empty;
            public string Subject { get; set; } = string.Empty;
            public string Email { get; set; } = string.Empty;
            public string AlgorandAddress { get; set; } = string.Empty;
            public string ShortIdentity { get; set; } = string.Empty;
            public string Scope { get; set; } = "openid profile email";
            public DateTimeOffset CreatedUtc { get; set; }
            public DateTimeOffset ExpiresUtc { get; set; }
        }
    }
}
