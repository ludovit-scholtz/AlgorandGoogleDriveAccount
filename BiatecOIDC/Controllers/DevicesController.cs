using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using BiatecOIDC.BusinessLogic;
using BiatecOIDC.Helper;
using BiatecOIDC.Model;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace BiatecOIDC.Controllers
{
    /// <summary>
    /// The device API of the Biatec Authenticator: enrollment (user bearer) and the calls the device makes with
    /// <c>Authorization: Device &lt;token&gt;</c>. Every device call must also carry <c>X-Device-Proof</c>
    /// (see <see cref="DeviceProofVerifier"/>); the bearer alone is refused, so a copied device token is useless
    /// without the device key.
    /// </summary>
    [ApiController]
    [Route("devices")]
    [AllowAnonymous]
    public class DevicesController : ControllerBase
    {
        public const string ProofHeader = "X-Device-Proof";
        private static readonly Regex KidPattern = new("^[A-Za-z0-9._-]{8,64}$", RegexOptions.Compiled);

        private readonly IDeviceStore _store;
        private readonly IJwtIssuerService _jwt;
        private readonly DevicesConfiguration _config;
        private readonly TimeProvider _time;
        private readonly IConsentAttestationService? _attestation;

        public DevicesController(IDeviceStore store, IJwtIssuerService jwt, IOptions<DevicesConfiguration> config, TimeProvider time,
            IConsentAttestationService? attestation = null)
        {
            _attestation = attestation;
            _store = store;
            _jwt = jwt;
            _config = config.Value;
            _time = time;
        }

        /// <summary>Enrolls a device for the signed-in user. The device becomes active after the activation delay.</summary>
        /// <response code="201">Enrolled; the device token is shown once.</response>
        /// <response code="400">Invalid kid or key.</response>
        /// <response code="401">No valid user bearer token.</response>
        /// <response code="409">The kid is taken, or the user has too many devices.</response>
        [HttpPost]
        public async Task<IActionResult> Enroll([FromBody] EnrollDeviceRequest? body)
        {
            var token = BearerTokenHelper.ExtractBearerToken(Request);
            var validation = string.IsNullOrWhiteSpace(token) ? default : _jwt.ValidateBearerAccessToken(token);
            var sub = validation.IsValid ? validation.Principal?.FindFirstValue(JwtRegisteredClaimNames.Sub) : null;
            if (string.IsNullOrEmpty(sub))
            {
                return Unauthorized();
            }

            if (body?.Kid is null || !KidPattern.IsMatch(body.Kid))
            {
                return BadRequest(new { error = "invalid_request", error_description = "kid must be 8 to 64 characters of A-Z a-z 0-9 . _ -" });
            }

            var key = body.PublicKey;
            if (key is null || key.Kty != "EC" || key.Crv != "P-256" || !DeviceProofVerifier.IsValidP256Key(key.X, key.Y))
            {
                return BadRequest(new { error = "invalid_request", error_description = "public_key must be a P-256 EC JWK" });
            }

            if (!IsValidPush(body.Push, allowEmpty: true))
            {
                return BadRequest(new { error = "invalid_request", error_description = "push must have provider fcm or apns and a token" });
            }

            if (await _store.CountForUserAsync(sub) >= _config.MaxDevicesPerUser)
            {
                return Conflict(new { error = "device_limit" });
            }

            var deviceToken = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
            var now = _time.GetUtcNow().ToUnixTimeSeconds();
            var device = new DeviceRecord
            {
                Kid = body.Kid,
                Sub = sub,
                Name = (body.Name ?? "").Trim() is { Length: > 64 } n ? n[..64] : (body.Name ?? "").Trim(),
                KeyX = key.X!,
                KeyY = key.Y!,
                TokenHash = DeviceStore.Hash(deviceToken),
                CreatedAt = now,
                ActiveAt = now + _config.ActivationSeconds,
                PushProvider = body.Push?.Provider,
                PushToken = body.Push?.Token,
            };
            if (!await _store.TryAddAsync(device))
            {
                return Conflict(new { error = "kid_taken" });
            }

            return StatusCode(StatusCodes.Status201Created, new EnrollDeviceResponse
            {
                Kid = device.Kid,
                Active = device.ActiveAt <= now,
                ActiveAt = device.ActiveAt,
                DeviceToken = deviceToken,
                Attestation = _attestation?.Issue(_jwt.GetIssuer(Request), sub, device.Kid, device.KeyX, device.KeyY, _time.GetUtcNow()),
            });
        }

        /// <summary>Registers or replaces where the device's notifications go. Rate limited per device.</summary>
        /// <response code="200">Registered.</response>
        /// <response code="400">Invalid provider or token.</response>
        /// <response code="401">Bad device token or proof.</response>
        /// <response code="429">Too many registrations this minute.</response>
        [HttpPut("push")]
        public async Task<IActionResult> PutPush([FromBody] DevicePush? body)
        {
            var (device, error) = await AuthenticateDeviceAsync();
            if (device is null)
            {
                return error!;
            }

            if (!IsValidPush(body, allowEmpty: false))
            {
                return BadRequest(new { error = "invalid_request", error_description = "provider must be fcm or apns and token is required" });
            }

            if (await _store.CountPushRegistrationAsync(device.Kid) > _config.PushRegistrationsPerMinute)
            {
                Response.Headers.RetryAfter = "60";
                return StatusCode(StatusCodes.Status429TooManyRequests, new { error = "rate_limited" });
            }

            await _store.SetPushAsync(device.Kid, body!.Provider!, body.Token!);
            return Ok(new { });
        }

        /// <summary>
        /// Authenticates a device call: <c>Authorization: Device &lt;token&gt;</c> plus a valid, unreplayed
        /// <c>X-Device-Proof</c>. Answers 401 with <c>WWW-Authenticate: Device error="invalid_token"</c> or <c>"invalid_proof"</c>.
        /// </summary>
        [NonAction]
        public async Task<(DeviceRecord? Device, IActionResult? Error)> AuthenticateDeviceAsync()
        {
            var header = Request.Headers.Authorization.ToString();
            const string prefix = "Device ";
            if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || header.Length == prefix.Length)
            {
                return (null, Challenge401("invalid_token"));
            }

            var token = header[prefix.Length..].Trim();
            var device = await _store.GetByTokenHashAsync(DeviceStore.Hash(token));
            if (device is null)
            {
                return (null, Challenge401("invalid_token"));
            }

            var url = _jwt.GetIssuer(Request) + Request.Path.Value;
            var result = DeviceProofVerifier.Verify(Request.Headers[ProofHeader].ToString(), device.Kid, device.KeyX, device.KeyY,
                Request.Method, url, token, _time.GetUtcNow(), _config.ProofSkewSeconds, out var jti);
            if (result != DeviceProofResult.Valid)
            {
                return (null, Challenge401("invalid_proof"));
            }

            // Remembered for twice the accepted window, so a replay inside the window and just after it is both refused.
            if (!await _store.TryRememberProofAsync(device.Kid, jti!, TimeSpan.FromSeconds(2 * _config.ProofSkewSeconds)))
            {
                return (null, Challenge401("invalid_proof"));
            }

            return (device, null);
        }

        private IActionResult Challenge401(string error)
        {
            Response.Headers.WWWAuthenticate = $"Device error=\"{error}\"";
            return Unauthorized(new { error });
        }

        private static bool IsValidPush(DevicePush? push, bool allowEmpty)
        {
            if (push is null)
            {
                return allowEmpty;
            }
            return (push.Provider == "fcm" || push.Provider == "apns")
                && !string.IsNullOrWhiteSpace(push.Token) && push.Token.Length <= 4096;
        }
    }
}
