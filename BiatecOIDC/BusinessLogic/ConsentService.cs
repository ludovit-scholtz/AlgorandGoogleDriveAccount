using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BiatecOIDC.Helper;
using BiatecOIDC.Model;
using Microsoft.Extensions.Options;

namespace BiatecOIDC.BusinessLogic
{
    /// <summary>The rules of the Biatec consent protocol at the authorization server: opening requests, showing them to a device, taking its receipt, and handing the receipt back to the client.</summary>
    public interface IConsentService
    {
        Task<OpenConsentResult> OpenAsync(OpenConsentCommand command);

        /// <summary>The device's view (consent-request schema) of its user's open requests. Never contains the binding code.</summary>
        Task<IReadOnlyList<JsonObject>> ListOpenForDeviceAsync(DeviceRecord device);

        /// <summary>The device's view of one request, or null when it does not exist, is not the device user's, or has ended.</summary>
        Task<JsonObject?> GetForDeviceAsync(DeviceRecord device, string authReqId);

        /// <summary>Verifies and records a signed receipt posted by a device (approve, deny or code_failed).</summary>
        Task<ReceiptResult> SubmitReceiptAsync(DeviceRecord device, string authReqId, JsonElement receipt, string issuer);

        /// <summary>
        /// One poll of the CIBA token endpoint by the client that opened the request. A poll that comes before the announced interval
        /// has passed answers <c>slow_down</c> and grows the interval by 5 seconds (RFC 8628 3.5, CIBA 11); a request that was denied,
        /// or locked by three wrong codes, answers <c>access_denied</c>. The state is changed by compare-and-set, so concurrent polls
        /// cannot both pass.
        /// </summary>
        Task<PollResult> PollAsync(string clientId, string authReqId);

        /// <summary>The stored receipt of a decided request, only to the user it belongs to or to its own confidential client.</summary>
        Task<(ReceiptLookup Lookup, string? ReceiptJson)> GetReceiptAsync(ReceiptCaller caller, string authReqId);
    }

    /// <inheritdoc cref="IConsentService"/>
    public sealed class ConsentService : IConsentService
    {
        private const int MaxAuthorizationDetailsBytes = 16 * 1024;
        private const int MaxBindingMessageLength = 140;
        private const int MaxOriginLength = 255;
        private const int MaxIdentifierLength = 200;
        private const int MaxDisplayNameLength = 100;
        private const int MaxScopeLength = 200;
        private const int ConflictRetries = 5;
        private const int SlowDownStepSeconds = 5;

        private readonly IConsentStore _store;
        private readonly IDeviceStore _devices;
        private readonly IConsentAttestationService _attestation;
        private readonly ConsentConfiguration _config;
        private readonly TimeProvider _time;
        private readonly ILogger<ConsentService> _logger;
        private readonly byte[] _codeKey;
        private readonly JsonElement? _attestationJwk;

        public ConsentService(IConsentStore store, IDeviceStore devices, IConsentAttestationService attestation, IOptions<ConsentConfiguration> config,
            TimeProvider time, ILogger<ConsentService> logger)
        {
            _store = store;
            _devices = devices;
            _attestation = attestation;
            _config = config.Value;
            _time = time;
            _logger = logger;
            _codeKey = DeriveCodeKey(_config);
            using var jwkDoc = attestation.GetPublicJwk() is { } jwk ? JsonDocument.Parse(jwk.ToJsonString()) : null;
            _attestationJwk = jwkDoc?.RootElement.Clone();
        }

        /// <summary>The key of the binding-code hash: the configured secret, else one derived (domain-separated) from the attestation key. Empty when neither exists.</summary>
        private static byte[] DeriveCodeKey(ConsentConfiguration config)
        {
            if (!string.IsNullOrWhiteSpace(config.BindingCodeKey))
            {
                return Encoding.UTF8.GetBytes(config.BindingCodeKey);
            }
            return string.IsNullOrWhiteSpace(config.AttestationSigningKeyPem)
                ? Array.Empty<byte>()
                : SHA256.HashData(Encoding.UTF8.GetBytes("biatec-consent-binding-code-v1:" + config.AttestationSigningKeyPem));
        }

        public async Task<OpenConsentResult> OpenAsync(OpenConsentCommand c)
        {
            if (!_attestation.IsConfigured || _codeKey.Length == 0)
            {
                return Fail(OpenConsentFailure.NotConfigured);
            }

            var invalid = Validate(c, out var details, out var boundHash, out var origin);
            if (invalid is not null)
            {
                return Fail(OpenConsentFailure.InvalidRequest, invalid);
            }

            var now = _time.GetUtcNow();
            var nowSeconds = now.ToUnixTimeSeconds();
            // Rule 8: with no active device nothing that needs the phone can be completed, so no request is opened.
            if (!(await _devices.ListForUserAsync(c.Sub)).Any(d => d.ActiveAt <= nowSeconds))
            {
                return Fail(OpenConsentFailure.NoActiveDevice);
            }

            var requested = Math.Clamp(c.RequestedExpirySeconds ?? _config.RequestedExpirySeconds, 1, _config.RequestedExpirySeconds);
            var validity = c.MaxConsentSeconds is { } ceiling ? Math.Min(requested, Math.Max(1, ceiling)) : requested;
            var limits = new ConsentLimits
            {
                MaxOpenPerUser = _config.MaxOpenRequestsPerUser,
                MaxPendingPerClientOrigin = _config.MaxPendingPerClientOrigin,
                MaxPerClientUserHour = _config.MaxRequestsPerClientUserPerHour,
                RetentionSeconds = _config.RetentionDays * 86400,
            };

            for (var attempt = 0; attempt < 3; attempt++)
            {
                var id = "ciba_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
                var record = new ConsentRequestRecord
                {
                    AuthReqId = id,
                    Entry = c.Entry,
                    Sub = c.Sub,
                    ClientId = c.ClientId,
                    // An unverified client's self-declared entity id is never shown or signed: only its client id is.
                    ClientEntityId = c.ClientVerified && !string.IsNullOrEmpty(c.ClientEntityId) ? c.ClientEntityId : c.ClientId,
                    ClientVerified = c.ClientVerified,
                    ClientDisplayName = c.ClientVerified ? c.ClientDisplayName : null,
                    ClientDomain = c.ClientVerified ? c.ClientDomain : null,
                    Origin = origin!,
                    Scope = c.Scope,
                    Resource = c.Resource,
                    AuthorizationDetailsJson = details!,
                    BoundHash = boundHash!,
                    // An unverified client has no free-text channel to the trusted surface: its text is neither shown nor signed.
                    BindingMessage = c.ClientVerified ? (c.BindingMessage ?? "").Trim() : "",
                    RequestedExpiry = requested,
                    CreatedAt = nowSeconds,
                    Expiry = nowSeconds + validity,
                    BindingCodeHash = HashCode(_codeKey, id, code),
                    AttemptsRemaining = Math.Max(1, _config.MaxWrongCodes),
                    Status = ConsentStatus.Pending,
                    Interval = _config.PollIntervalSeconds,
                    NextPollAt = nowSeconds,
                };

                switch (await _store.TryOpenAsync(record, limits, now))
                {
                    case OpenOutcome.Opened:
                        _logger.LogInformation("consent_opened auth_req_id={AuthReqId} client={ClientId} entry={Entry} expires_in={ExpiresIn}", id, c.ClientId, c.Entry, validity);
                        return new OpenConsentResult { AuthReqId = id, ExpiresIn = validity, Interval = record.Interval, BindingCode = code };
                    case OpenOutcome.UserCap:
                        _logger.LogWarning("consent_refused reason=user_cap client={ClientId}", c.ClientId);
                        return Fail(OpenConsentFailure.UserCap);
                    case OpenOutcome.OriginCap:
                        _logger.LogWarning("consent_refused reason=origin_cap client={ClientId}", c.ClientId);
                        return Fail(OpenConsentFailure.OriginCap);
                    case OpenOutcome.RateLimited:
                        _logger.LogWarning("consent_refused reason=rate_limited client={ClientId}", c.ClientId);
                        return Fail(OpenConsentFailure.RateLimited);
                }
            }

            throw new InvalidOperationException("Could not generate a unique auth_req_id.");
        }

        public async Task<IReadOnlyList<JsonObject>> ListOpenForDeviceAsync(DeviceRecord device)
        {
            var now = _time.GetUtcNow().ToUnixTimeSeconds();
            var views = new List<JsonObject>();
            if (device.ActiveAt > now)
            {
                return views; // a device in its activation delay sees nothing
            }
            foreach (var id in await _store.ListOpenIdsAsync(device.Sub, _time.GetUtcNow()))
            {
                var record = await _store.GetAsync(id);
                if (record is not null && record.Sub == device.Sub && !record.IsDecided && record.Expiry > now)
                {
                    views.Add(ToDeviceView(record));
                }
            }
            return views;
        }

        public async Task<JsonObject?> GetForDeviceAsync(DeviceRecord device, string authReqId)
        {
            var record = await _store.GetAsync(authReqId);
            // Not the device user's, or the device is not active yet: the same answer as for an unknown id.
            if (record is null || device.ActiveAt > _time.GetUtcNow().ToUnixTimeSeconds() || record.Sub != device.Sub || record.IsDecided || record.Expiry <= _time.GetUtcNow().ToUnixTimeSeconds())
            {
                return null;
            }
            return ToDeviceView(record);
        }

        public async Task<ReceiptResult> SubmitReceiptAsync(DeviceRecord device, string authReqId, JsonElement receipt, string issuer)
        {
            if (!_attestation.IsConfigured)
            {
                return new ReceiptResult { Outcome = ReceiptOutcome.NotConfigured };
            }

            var nowOffset = _time.GetUtcNow();
            var now = nowOffset.ToUnixTimeSeconds();
            // Rule 7: a device that is not active yet cannot decide.
            if (device.ActiveAt > now)
            {
                return new ReceiptResult { Outcome = ReceiptOutcome.DeviceInactive };
            }

            for (var attempt = 0; attempt < ConflictRetries; attempt++)
            {
                var record = await _store.GetAsync(authReqId);
                if (record is null || record.Sub != device.Sub)
                {
                    return new ReceiptResult { Outcome = ReceiptOutcome.NotFound };
                }
                if (record.IsDecided)
                {
                    return new ReceiptResult { Outcome = ReceiptOutcome.AlreadyDecided };
                }
                if (record.Expiry <= now)
                {
                    await _store.ReleaseAsync(record);
                    return new ReceiptResult { Outcome = ReceiptOutcome.Expired };
                }

                var problem = CheckReceipt(receipt, device, record, issuer, nowOffset, out var decision, out var enteredCode, out var envelope);
                if (problem is not null)
                {
                    _logger.LogWarning("consent_receipt_refused auth_req_id={AuthReqId} device={Kid} reason={Reason}", authReqId, device.Kid, problem);
                    return new ReceiptResult { Outcome = ReceiptOutcome.InvalidReceipt, Detail = problem };
                }

                var updated = Clone(record);
                ReceiptResult result;
                bool finished = false;

                if (record.Status == ConsentStatus.CodeLocked && decision != "code_failed")
                {
                    // After the third wrong code only the code_failed receipt is accepted.
                    return new ReceiptResult { Outcome = ReceiptOutcome.CodeLocked };
                }

                switch (decision)
                {
                    case "deny":
                        updated.Status = ConsentStatus.Denied;
                        result = new ReceiptResult { Outcome = ReceiptOutcome.Denied };
                        finished = true;
                        break;
                    case "code_failed":
                        if (record.Status != ConsentStatus.CodeLocked)
                        {
                            return new ReceiptResult { Outcome = ReceiptOutcome.InvalidReceipt, Detail = "code_failed_not_allowed" };
                        }
                        updated.Status = ConsentStatus.CodeFailed;
                        result = new ReceiptResult { Outcome = ReceiptOutcome.CodeFailedRecorded };
                        finished = true;
                        break;
                    default: // approve
                        if (CodeMatches(record, enteredCode))
                        {
                            updated.Status = ConsentStatus.Approved;
                            result = new ReceiptResult { Outcome = ReceiptOutcome.Approved };
                            finished = true;
                        }
                        else
                        {
                            updated.AttemptsRemaining = Math.Max(0, record.AttemptsRemaining - 1);
                            if (updated.AttemptsRemaining == 0)
                            {
                                updated.Status = ConsentStatus.CodeLocked;
                                result = new ReceiptResult { Outcome = ReceiptOutcome.CodeLocked };
                            }
                            else
                            {
                                result = new ReceiptResult { Outcome = ReceiptOutcome.WrongCode, AttemptsRemaining = updated.AttemptsRemaining };
                            }
                        }
                        break;
                }

                if (finished)
                {
                    updated.DecidedAt = now;
                    updated.DecidedByDeviceKid = device.Kid;
                    // The stored (and later returned) receipt is rebuilt from the members that were verified, so a duplicated or extra
                    // member in what the device posted can never reach a client or be read differently by another parser.
                    updated.ReceiptJson = envelope!.ToJsonString();
                }

                if (!await _store.TryUpdateAsync(record, updated))
                {
                    continue; // someone changed the request between the read and the write: look again
                }

                _logger.LogInformation("consent_receipt auth_req_id={AuthReqId} device={Kid} outcome={Outcome}", authReqId, device.Kid, result.Outcome);
                return result;
            }

            return new ReceiptResult { Outcome = ReceiptOutcome.Conflict };
        }

        public async Task<PollResult> PollAsync(string clientId, string authReqId)
        {
            var interval = _config.PollIntervalSeconds;
            for (var attempt = 0; attempt < ConflictRetries; attempt++)
            {
                var now = _time.GetUtcNow().ToUnixTimeSeconds();
                var record = await _store.GetAsync(authReqId);
                if (record is null || !string.Equals(record.ClientId, clientId, StringComparison.Ordinal))
                {
                    return new PollResult { Outcome = PollOutcome.InvalidGrant };
                }

                switch (record.Status)
                {
                    case ConsentStatus.Approved:
                        // A consent is valid only inside its window (rule 5): an approval that is collected late yields nothing.
                        if (record.Expiry <= now)
                        {
                            return new PollResult { Outcome = PollOutcome.Expired, Interval = record.Interval };
                        }
                        return new PollResult { Outcome = PollOutcome.Approved, Interval = record.Interval, Request = record };
                    case ConsentStatus.Denied:
                    case ConsentStatus.CodeFailed:
                    case ConsentStatus.CodeLocked: // three wrong codes: denied for the client even before the signed code_failed arrives
                        return new PollResult { Outcome = PollOutcome.AccessDenied, Interval = record.Interval };
                }
                if (record.Expiry <= now)
                {
                    return new PollResult { Outcome = PollOutcome.Expired, Interval = record.Interval };
                }

                var tooFast = now < record.NextPollAt;
                var updated = Clone(record);
                if (tooFast)
                {
                    updated.Interval = record.Interval + SlowDownStepSeconds;
                }
                updated.NextPollAt = now + updated.Interval;
                if (await _store.TryUpdateAsync(record, updated))
                {
                    if (tooFast)
                    {
                        _logger.LogInformation("consent_slow_down auth_req_id={AuthReqId} interval={Interval}", authReqId, updated.Interval);
                    }
                    return new PollResult { Outcome = tooFast ? PollOutcome.SlowDown : PollOutcome.Pending, Interval = updated.Interval };
                }
                interval = record.Interval;
            }

            // The request kept changing underneath us (other polls): answer as for a slow client without recording anything.
            return new PollResult { Outcome = PollOutcome.SlowDown, Interval = interval + SlowDownStepSeconds };
        }

        public async Task<(ReceiptLookup Lookup, string? ReceiptJson)> GetReceiptAsync(ReceiptCaller caller, string authReqId)
        {
            var record = await _store.GetAsync(authReqId);
            if (record is null || !Owns(caller, record))
            {
                return (ReceiptLookup.NotFound, null);
            }
            return record.IsDecided && record.ReceiptJson is not null ? (ReceiptLookup.Found, record.ReceiptJson) : (ReceiptLookup.NotReady, null);
        }

        /// <summary>
        /// The receipt belongs to the user whose request it is, and to the client that opened it. A bearer token proves the subject
        /// (and the client it was issued to); a client proves itself only with its own secret. A caller that proves neither gets nothing.
        /// </summary>
        private static bool Owns(ReceiptCaller caller, ConsentRequestRecord record)
        {
            var clientMatches = !string.IsNullOrEmpty(caller.ClientId) && string.Equals(caller.ClientId, record.ClientId, StringComparison.Ordinal);
            if (caller.ClientAuthenticated)
            {
                return clientMatches;
            }
            return !string.IsNullOrEmpty(caller.Sub) && string.Equals(caller.Sub, record.Sub, StringComparison.Ordinal) && clientMatches;
        }

        // ---- opening ----

        private string? Validate(OpenConsentCommand c, out string? detailsJson, out string? boundHash, out string? normalizedOrigin)
        {
            detailsJson = null;
            boundHash = null;
            normalizedOrigin = null;
            if (string.IsNullOrWhiteSpace(c.Sub) || string.IsNullOrWhiteSpace(c.ClientId))
            {
                return "sub and client are required";
            }
            if (c.Sub.Length > MaxIdentifierLength || c.ClientId.Length > MaxIdentifierLength || (c.ClientEntityId?.Length ?? 0) > MaxOriginLength
                || (c.ClientDisplayName?.Length ?? 0) > MaxDisplayNameLength || (c.ClientDomain?.Length ?? 0) > MaxOriginLength
                || (c.Scope?.Length ?? 0) > MaxScopeLength || (c.Resource?.Length ?? 0) > MaxOriginLength)
            {
                return "a field is too long";
            }
            if (c.Entry is not ("step-up" or "direct"))
            {
                return "entry must be step-up or direct";
            }
            normalizedOrigin = NormalizeOrigin(c.Origin);
            if (normalizedOrigin is null)
            {
                return "origin must be an absolute http(s) origin";
            }
            if (c.BindingMessage is { } message && (message.Length > MaxBindingMessageLength || message.Any(IsUnsafeTextCharacter)))
            {
                return "binding_message is too long or contains control or formatting characters";
            }
            if (c.AuthorizationDetails.ValueKind != JsonValueKind.Array || c.AuthorizationDetails.GetArrayLength() != 1
                || c.AuthorizationDetails[0].ValueKind != JsonValueKind.Object)
            {
                return "authorization_details must be an array of exactly one object";
            }

            var d = c.AuthorizationDetails[0];
            var type = d.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var hashMember = type switch { "biatec_transaction" => "group_hash", "biatec_limits_change" => "request_hash", _ => null };
            if (hashMember is null)
            {
                return "unsupported authorization_details type";
            }
            if (!d.TryGetProperty(hashMember, out var h) || h.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(h.GetString()))
            {
                return hashMember + " is required";
            }

            try
            {
                // Strict canonicalization is what the receipt will be signed over: refuse here what could never be signed.
                var canonical = JsonCanonicalizer.Canonicalize(c.AuthorizationDetails);
                if (Encoding.UTF8.GetByteCount(canonical) > MaxAuthorizationDetailsBytes)
                {
                    return "authorization_details is too large";
                }
                detailsJson = canonical;
            }
            catch (JsonCanonicalizationException ex)
            {
                return "authorization_details cannot be canonicalized: " + ex.Message;
            }

            boundHash = h.GetString();
            return null;
        }

        /// <summary>
        /// One spelling per origin: scheme and host lowercase, default port dropped, no path, query, fragment or credentials, ASCII host
        /// only (an internationalised host must be sent as punycode). The per-origin cap, the pause-by-origin feature and the card all
        /// key on this value, so it must not be spoofable by spelling.
        /// </summary>
        public static string? NormalizeOrigin(string? origin)
        {
            if (string.IsNullOrEmpty(origin) || origin.Length > MaxOriginLength || origin.Any(ch => ch <= ' ' || ch > '~')
                || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("https" or "http") || uri.PathAndQuery != "/" || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0
                || origin.Contains('#') || origin.Contains('?') || uri.Host != uri.IdnHost)
            {
                return null;
            }
            return uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped).ToLowerInvariant();
        }

        private static bool IsUnsafeTextCharacter(char ch)
            => char.IsControl(ch) || System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) is System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator;

        private static OpenConsentResult Fail(OpenConsentFailure failure, string? detail = null) => new() { Failure = failure, Detail = detail };

        // ---- receipts ----

        private static readonly string[] EnvelopeMembers = { "version", "signed", "alg", "signature", "issued_at", "device" };
        private static readonly string[] SignedMembers =
            { "authorization_details", "auth_req_id", "client_entity_id", "binding_code", "decision", "expiry", "sub", "binding_message" };

        private string? CheckReceipt(JsonElement receipt, DeviceRecord device, ConsentRequestRecord record, string issuer, DateTimeOffset now,
            out string decision, out string enteredCode, out JsonObject? envelope)
        {
            decision = "";
            enteredCode = "";
            envelope = null;
            if (receipt.ValueKind != JsonValueKind.Object || Str(receipt, "version") != "1" || Str(receipt, "alg") != "ES256"
                || !HasExactMembers(receipt, EnvelopeMembers)
                || !receipt.TryGetProperty("signed", out var signed) || signed.ValueKind != JsonValueKind.Object
                || !receipt.TryGetProperty("device", out var dev) || dev.ValueKind != JsonValueKind.Object
                || !HasExactMembers(dev, new[] { "kid", "public_key", "attestation" })
                || !receipt.TryGetProperty("issued_at", out var issuedAt) || issuedAt.ValueKind != JsonValueKind.Number
                || !issuedAt.TryGetInt64(out var issuedAtSeconds) || issuedAtSeconds < 0)
            {
                return "malformed_receipt";
            }

            // The key in the receipt must be the enrolled one: a receipt cannot bring its own key.
            if (Str(dev, "kid") != device.Kid || !dev.TryGetProperty("public_key", out var key) || !HasExactMembers(key, new[] { "kty", "crv", "x", "y" })
                || Str(key, "kty") != "EC" || Str(key, "crv") != "P-256" || Str(key, "x") != device.KeyX || Str(key, "y") != device.KeyY)
            {
                return "device_key_mismatch";
            }

            if (_attestationJwk is not { } jwk)
            {
                return "not_configured";
            }
            if (!ConsentReceiptVerifier.VerifyDeviceSignature(receipt))
            {
                return "bad_signature";
            }
            if (!ConsentReceiptVerifier.VerifyAttestation(receipt, new[] { jwk }, issuer, now))
            {
                return "bad_attestation";
            }

            // The signed object is exactly the eight members of the protocol (section 8), and every one is compared with what this
            // server stored, never with what the receipt supplies.
            if (!HasExactMembers(signed, SignedMembers))
            {
                return "malformed_receipt";
            }
            decision = Str(signed, "decision") ?? "";
            enteredCode = Str(signed, "binding_code") ?? "";
            if (decision is not ("approve" or "deny" or "code_failed"))
            {
                return "bad_decision";
            }
            if (Str(signed, "auth_req_id") != record.AuthReqId || Str(signed, "client_entity_id") != record.ClientEntityId
                || Str(signed, "binding_message") != record.BindingMessage || Str(signed, "sub") != record.Sub
                || !signed.TryGetProperty("expiry", out var expiry) || !expiry.TryGetInt64(out var expirySeconds) || expirySeconds != record.Expiry)
            {
                return "receipt_does_not_match_request";
            }

            try
            {
                if (!signed.TryGetProperty("authorization_details", out var ad)
                    || JsonCanonicalizer.Canonicalize(ad) != record.AuthorizationDetailsJson)
                {
                    return "receipt_does_not_match_request";
                }
            }
            catch (JsonCanonicalizationException)
            {
                return "malformed_receipt";
            }

            // A deny or code_failed receipt carries whatever the user typed; only an approval's code is compared.
            if (decision == "approve" && (enteredCode.Length != 6 || !enteredCode.All(char.IsAsciiDigit)))
            {
                return "bad_binding_code_format";
            }

            // Rebuilt from the verified members only (see SubmitReceiptAsync).
            envelope = new JsonObject
            {
                ["version"] = "1",
                ["signed"] = JsonNode.Parse(signed.GetRawText()),
                ["alg"] = "ES256",
                ["signature"] = Str(receipt, "signature"),
                ["issued_at"] = issuedAtSeconds,
                ["device"] = new JsonObject
                {
                    ["kid"] = device.Kid,
                    ["public_key"] = new JsonObject { ["kty"] = "EC", ["crv"] = "P-256", ["x"] = device.KeyX, ["y"] = device.KeyY },
                    ["attestation"] = Str(dev, "attestation"),
                },
            };
            return null;
        }

        /// <summary>True when the object has exactly these members, each once. A duplicated member is refused because parsers disagree on which copy wins.</summary>
        private static bool HasExactMembers(JsonElement o, string[] names)
        {
            if (o.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in o.EnumerateObject())
            {
                if (!seen.Add(p.Name))
                {
                    return false;
                }
            }
            return seen.Count == names.Length && names.All(seen.Contains);
        }

        private bool CodeMatches(ConsentRequestRecord record, string entered)
        {
            var expected = Encoding.ASCII.GetBytes(record.BindingCodeHash);
            var actual = Encoding.ASCII.GetBytes(HashCode(_codeKey, record.AuthReqId, entered));
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }

        /// <summary>
        /// HMAC-SHA-256 (hex) of <c>auth_req_id:code</c> under a server secret. A six digit code has only 20 bits, so an unkeyed hash would let
        /// anyone with a Redis dump or backup read the code back; with the key they would also need the secret store.
        /// </summary>
        public static string HashCode(byte[] key, string authReqId, string code)
            => Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(authReqId + ":" + code))).ToLowerInvariant();

        // ---- views ----

        private static JsonObject ToDeviceView(ConsentRequestRecord r)
        {
            var client = new JsonObject { ["entity_id"] = r.ClientEntityId, ["verified"] = r.ClientVerified };
            if (r.ClientVerified)
            {
                client["display_name"] = r.ClientDisplayName;
                client["domain"] = r.ClientDomain;
            }
            else
            {
                // An unverified client shows only a short fingerprint of its identifier.
                var h = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(r.ClientId)))[..8];
                client["fingerprint"] = h[..4] + "-" + h[4..];
            }

            // The binding code is deliberately absent: the user types it from the requesting surface.
            return new JsonObject
            {
                ["auth_req_id"] = r.AuthReqId,
                ["entry"] = r.Entry,
                ["authorization_details"] = JsonNode.Parse(r.AuthorizationDetailsJson),
                ["client"] = client,
                ["binding_message"] = r.BindingMessage,
                ["requested_expiry"] = r.RequestedExpiry,
                ["expiry"] = r.Expiry,
                ["bound_hash"] = r.BoundHash,
                ["origin"] = r.Origin,
                ["sub"] = r.Sub,
            };
        }

        private static ConsentRequestRecord Clone(ConsentRequestRecord r) => JsonSerializer.Deserialize<ConsentRequestRecord>(JsonSerializer.Serialize(r))!;

        private static string? Str(JsonElement o, string name)
            => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
