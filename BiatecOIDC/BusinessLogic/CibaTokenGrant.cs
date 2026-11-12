using System.Text.Json.Nodes;
using BiatecOIDC.Model;
using Microsoft.Extensions.Options;

namespace BiatecOIDC.BusinessLogic
{
    /// <summary>The answer of the CIBA token grant: an OAuth error (with the interval for pending and slow_down), or the token response.</summary>
    public sealed class CibaGrantResult
    {
        public int Status { get; set; }
        public string? Error { get; set; }
        public string? Description { get; set; }
        public int? Interval { get; set; }
        public JsonObject? Body { get; set; }
    }

    /// <summary><c>grant_type=urn:openid:params:grant-type:ciba</c> (poll mode) at the token endpoint.</summary>
    public interface ICibaTokenGrant
    {
        public const string GrantType = "urn:openid:params:grant-type:ciba";

        /// <summary>One poll by an authenticated confidential client for its own <c>auth_req_id</c>.</summary>
        Task<CibaGrantResult> RedeemAsync(JwtIssuerClientConfiguration client, string? authReqId, string? resource);
    }

    /// <inheritdoc cref="ICibaTokenGrant"/>
    public sealed class CibaTokenGrant : ICibaTokenGrant
    {
        private readonly IConsentService _consent;
        private readonly IJwtIssuerService _jwt;
        private readonly ConsentConfiguration _config;
        private readonly TimeProvider _time;

        public CibaTokenGrant(IConsentService consent, IJwtIssuerService jwt, IOptions<ConsentConfiguration> config, TimeProvider time)
        {
            _consent = consent;
            _jwt = jwt;
            _config = config.Value;
            _time = time;
        }

        public async Task<CibaGrantResult> RedeemAsync(JwtIssuerClientConfiguration client, string? authReqId, string? resource)
        {
            if (string.IsNullOrWhiteSpace(authReqId) || authReqId.Length > 128)
            {
                return Error(400, "invalid_request", "auth_req_id is required.");
            }

            var poll = await _consent.PollAsync(client.ClientId, authReqId, resource);
            switch (poll.Outcome)
            {
                case PollOutcome.Pending:
                    return Error(400, "authorization_pending", "The user has not decided yet.", poll.Interval);
                case PollOutcome.SlowDown:
                    return Error(400, "slow_down", "Polling too fast; wait for the interval.", poll.Interval);
                case PollOutcome.AccessDenied:
                    return Error(400, "access_denied", "The user denied the request or failed the binding code.");
                case PollOutcome.Expired:
                    return Error(400, "expired_token", "The request expired.");
                case PollOutcome.InvalidTarget:
                    return Error(400, "invalid_target", "resource does not match the consent request.");
                case PollOutcome.Approved:
                    break;
                default:
                    // Unknown, not this client's, already redeemed, or not redeemable: one answer for all of them.
                    return Error(400, "invalid_grant", "The auth_req_id is invalid.");
            }

            var request = poll.Request!;
            var now = _time.GetUtcNow();
            var lifetime = TimeSpan.FromSeconds(Math.Max(1, _config.ElevatedTokenSeconds));
            var token = _jwt.IssueConsentAccessToken(
                request.TokenClaims!.Select(c => new System.Security.Claims.Claim(c.Type, c.Value)),
                client.ClientId, request.Resource, request.AuthReqId, request.BoundHash, now, lifetime);

            // No refresh token and no ID token: an elevated token is for the bound action only.
            return new CibaGrantResult
            {
                Status = 200,
                Body = new JsonObject
                {
                    ["access_token"] = token,
                    ["token_type"] = "Bearer",
                    ["expires_in"] = (int)lifetime.TotalSeconds,
                    ["scope"] = request.Scope,
                    ["consent_receipt"] = JsonNode.Parse(request.ReceiptJson!),
                },
            };
        }

        private static CibaGrantResult Error(int status, string error, string description, int? interval = null)
            => new() { Status = status, Error = error, Description = description, Interval = interval };
    }
}
