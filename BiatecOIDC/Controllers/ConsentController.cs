using System.Security.Claims;
using System.Text.Json;
using BiatecOIDC.BusinessLogic;
using BiatecOIDC.Helper;
using BiatecOIDC.Model;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;

namespace BiatecOIDC.Controllers
{
    /// <summary>
    /// The authenticator's side of the Biatec consent protocol (<c>Authorization: Device</c> plus <c>X-Device-Proof</c>) and the
    /// client's receipt retrieval. A device that is not active yet sees and decides nothing.
    /// </summary>
    [ApiController]
    // Device tokens, transaction details and receipts must never sit in a cache.
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [AllowAnonymous]
    public class ConsentController : ControllerBase
    {
        private const int MaxReceiptBytes = 32 * 1024;

        private readonly IConsentService _consent;
        private readonly IDeviceAuthenticator _devices;
        private readonly IJwtIssuerService _jwt;
        private readonly TimeProvider _time;

        public ConsentController(IConsentService consent, IDeviceAuthenticator devices, IJwtIssuerService jwt, TimeProvider time)
        {
            _consent = consent;
            _devices = devices;
            _jwt = jwt;
            _time = time;
        }

        /// <summary>The user's pending, unexpired requests, as the consent-request object (never the binding code).</summary>
        [HttpGet("devices/requests")]
        public async Task<IActionResult> ListRequests()
        {
            var (device, error) = await AuthenticateActiveDeviceAsync();
            if (device is null)
            {
                return error!;
            }
            return Ok(new { requests = await _consent.ListOpenForDeviceAsync(device) });
        }

        /// <summary>One consent request, only for the device of the user it belongs to.</summary>
        [HttpGet("consent/requests/{authReqId}")]
        public async Task<IActionResult> GetRequest(string authReqId)
        {
            var (device, error) = await AuthenticateActiveDeviceAsync();
            if (device is null)
            {
                return error!;
            }
            var view = await _consent.GetForDeviceAsync(device, authReqId);
            return view is null ? NotFound(new { error = "not_found" }) : Ok(view);
        }

        /// <summary>Takes the signed receipt of the user's decision (approve, deny or code_failed).</summary>
        [HttpPost("consent/requests/{authReqId}/receipt")]
        [RequestSizeLimit(MaxReceiptBytes)]
        public async Task<IActionResult> PostReceipt(string authReqId, [FromBody] JsonElement receipt)
        {
            var (device, error) = await AuthenticateActiveDeviceAsync();
            if (device is null)
            {
                return error!;
            }

            var result = await _consent.SubmitReceiptAsync(device, authReqId, receipt, _jwt.GetIssuer(Request));
            switch (result.Outcome)
            {
                case ReceiptOutcome.Approved:
                    return Ok(new { status = "approved" });
                case ReceiptOutcome.Denied:
                    return Ok(new { status = "denied" });
                case ReceiptOutcome.CodeFailedRecorded:
                    return Ok(new { status = "code_failed" });
                case ReceiptOutcome.WrongCode:
                    return BadRequest(new { error = "wrong_code", attempts_remaining = result.AttemptsRemaining });
                case ReceiptOutcome.CodeLocked:
                    return StatusCode(StatusCodes.Status403Forbidden, new { error = "access_denied", reason = "code_failed" });
                case ReceiptOutcome.NotFound:
                    return NotFound(new { error = "not_found" });
                case ReceiptOutcome.AlreadyDecided:
                case ReceiptOutcome.Conflict:
                    return Conflict(new { error = "already_decided" });
                case ReceiptOutcome.Expired:
                    return StatusCode(StatusCodes.Status410Gone, new { error = "expired" });
                case ReceiptOutcome.DeviceInactive:
                    return StatusCode(StatusCodes.Status403Forbidden, new { error = "device_inactive" });
                case ReceiptOutcome.NotConfigured:
                    return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "consent_not_configured" });
                default:
                    // The reason is a fixed code, never receipt content.
                    return BadRequest(new { error = "invalid_receipt", reason = result.Detail });
            }
        }

        /// <summary>
        /// The receipt of a decided request. Allowed only to a bearer whose subject owns the request (and whose token was issued
        /// to the client that opened it), or to that client itself with its own credentials.
        /// </summary>
        [HttpGet("consent/receipts/{authReqId}")]
        public async Task<IActionResult> GetReceipt(string authReqId)
        {
            var caller = await IdentifyReceiptCallerAsync();
            if (caller is null)
            {
                Response.Headers.WWWAuthenticate = "Bearer";
                return Unauthorized(new { error = "invalid_token" });
            }

            var (lookup, json) = await _consent.GetReceiptAsync(caller, authReqId);
            return lookup switch
            {
                ReceiptLookup.Found => Content(json!, "application/json"),
                ReceiptLookup.NotReady => Conflict(new { error = "receipt_not_ready" }),
                _ => NotFound(new { error = "not_found" }),
            };
        }

        private async Task<ReceiptCaller?> IdentifyReceiptCallerAsync()
        {
            var header = Request.Headers.Authorization.ToString();
            if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                var client = await _jwt.AuthenticateConfidentialClientAsync(header);
                return client is null ? null : new ReceiptCaller { ClientId = client.ClientId, ClientAuthenticated = true };
            }

            var token = BearerTokenHelper.ExtractBearerToken(Request);
            if (string.IsNullOrWhiteSpace(token))
            {
                return null;
            }
            var validation = _jwt.ValidateBearerAccessToken(token);
            if (!validation.IsValid || validation.Principal is null)
            {
                return null;
            }
            var sub = validation.Principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
            var clientId = validation.Principal.FindFirstValue("client_id");
            return string.IsNullOrEmpty(sub) ? null : new ReceiptCaller { Sub = sub, ClientId = clientId };
        }

        private async Task<(DeviceRecord? Device, IActionResult? Error)> AuthenticateActiveDeviceAsync()
        {
            var (device, failure) = await _devices.AuthenticateAsync(Request);
            if (device is null)
            {
                Response.Headers.WWWAuthenticate = failure == DeviceAuthFailure.InvalidToken ? "Device error=\"invalid_token\"" : "Device error=\"invalid_proof\"";
                return (null, Unauthorized(new { error = failure == DeviceAuthFailure.InvalidToken ? "invalid_token" : "invalid_proof" }));
            }
            // Rule 7: a device in its activation delay neither sees requests nor decides them.
            if (device.ActiveAt > _time.GetUtcNow().ToUnixTimeSeconds())
            {
                return (null, StatusCode(StatusCodes.Status403Forbidden, new { error = "device_inactive" }));
            }
            return (device, null);
        }
    }
}
