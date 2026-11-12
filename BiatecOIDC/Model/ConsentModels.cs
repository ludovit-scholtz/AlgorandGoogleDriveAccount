using System.Text.Json;

namespace BiatecOIDC.Model
{
    /// <summary>Lifecycle of a consent request. Only <see cref="Pending"/> and <see cref="CodeLocked"/> hold an open slot.</summary>
    public enum ConsentStatus
    {
        /// <summary>Waiting for the user's decision.</summary>
        Pending = 0,
        /// <summary>The user entered the wrong binding code three times: only a signed <c>code_failed</c> receipt is accepted now.</summary>
        CodeLocked = 1,
        /// <summary>Approved with the right binding code. Consumed by the wallet call that signs (a later step).</summary>
        Approved = 2,
        Denied = 3,
        CodeFailed = 4,
    }

    /// <summary>One claim of the caller's access token, stored with a step-up request so the elevated token can carry it.</summary>
    public sealed class ClaimSnapshot
    {
        public string Type { get; set; } = "";
        public string Value { get; set; } = "";
    }

    /// <summary>One consent request, stored at Biatec OIDC. The binding code is never stored, only a salted hash of it.</summary>
    public sealed class ConsentRequestRecord
    {
        /// <summary>Optimistic-concurrency counter. Every update must present the version it read.</summary>
        public int Version { get; set; }
        public string AuthReqId { get; set; } = "";
        /// <summary><c>step-up</c> or <c>direct</c>.</summary>
        public string Entry { get; set; } = "";
        public string Sub { get; set; } = "";
        public string ClientId { get; set; } = "";
        public string ClientEntityId { get; set; } = "";
        public bool ClientVerified { get; set; }
        public string? ClientDisplayName { get; set; }
        public string? ClientDomain { get; set; }
        public string Origin { get; set; } = "";
        public string Scope { get; set; } = "";
        public string? Resource { get; set; }
        /// <summary>The <c>authorization_details</c> array exactly as shown on the phone and signed (one decoded object).</summary>
        public string AuthorizationDetailsJson { get; set; } = "[]";
        /// <summary><c>group_hash</c> or <c>request_hash</c> the consent is tied to.</summary>
        public string BoundHash { get; set; } = "";
        /// <summary>The text rendered on the card; empty for an unverified client.</summary>
        public string BindingMessage { get; set; } = "";
        public int RequestedExpiry { get; set; }
        public long CreatedAt { get; set; }
        public long Expiry { get; set; }
        /// <summary>SHA-256 hex of <c>auth_req_id:code</c>.</summary>
        public string BindingCodeHash { get; set; } = "";
        public int AttemptsRemaining { get; set; }
        public ConsentStatus Status { get; set; }
        public long? DecidedAt { get; set; }
        public string? DecidedByDeviceKid { get; set; }
        /// <summary>The verified receipt envelope, stored verbatim.</summary>
        public string? ReceiptJson { get; set; }
        /// <summary>Seconds the client must wait between polls (grows by 5 after each <c>slow_down</c>).</summary>
        public int Interval { get; set; }
        /// <summary>Earliest epoch second at which the next poll is not "too fast".</summary>
        public long NextPollAt { get; set; }
        /// <summary>
        /// The allowlisted claims of the token that was challenged (step-up entry). The elevated token carries exactly these, plus the
        /// consent claims. Null for a request that cannot be redeemed for a token.
        /// </summary>
        public List<ClaimSnapshot>? TokenClaims { get; set; }
        /// <summary>Set when the elevated token was issued: an <c>auth_req_id</c> is redeemed once (CIBA), and the signing call consumes the consent itself.</summary>
        public bool TokenIssued { get; set; }

        public bool IsDecided => Status is ConsentStatus.Approved or ConsentStatus.Denied or ConsentStatus.CodeFailed;
    }

    /// <summary>Caps applied when a request is opened.</summary>
    public sealed class ConsentLimits
    {
        public int MaxOpenPerUser { get; set; }
        public int MaxPendingPerClientOrigin { get; set; }
        public int MaxPerClientUserHour { get; set; }
        /// <summary>How long a record outlives its expiry (receipts stay retrievable, and the decision stays auditable).</summary>
        public int RetentionSeconds { get; set; }
    }

    public enum OpenOutcome
    {
        Opened,
        /// <summary>The user already has the maximum number of open requests.</summary>
        UserCap,
        /// <summary>The client already has the maximum number of pending requests for this user and origin.</summary>
        OriginCap,
        /// <summary>The client opened too many requests for this user within the hour.</summary>
        RateLimited,
        /// <summary>The generated id already exists (practically impossible; the caller may retry).</summary>
        IdCollision,
    }

    /// <summary>What a client asks to be consented to. The authorization details are decoded by Biatec OIDC, never by the client.</summary>
    public sealed class OpenConsentCommand
    {
        public string Sub { get; set; } = "";
        public string ClientId { get; set; } = "";
        public string ClientEntityId { get; set; } = "";
        public bool ClientVerified { get; set; }
        public string? ClientDisplayName { get; set; }
        public string? ClientDomain { get; set; }
        public string Entry { get; set; } = "step-up";
        public string Origin { get; set; } = "";
        public string Scope { get; set; } = "";
        public string? Resource { get; set; }
        /// <summary>A JSON array of exactly one authorization-details object.</summary>
        public JsonElement AuthorizationDetails { get; set; }
        /// <summary>Optional text from the client; rendered (and signed) only when the client is verified.</summary>
        public string? BindingMessage { get; set; }
        public int? RequestedExpirySeconds { get; set; }
        /// <summary>Claims of the challenged token to carry into the elevated token (see <see cref="ConsentTokenClaims"/>).</summary>
        public List<ClaimSnapshot>? TokenClaims { get; set; }
        /// <summary>A hard ceiling on the validity (for example the group's remaining validity).</summary>
        public int? MaxConsentSeconds { get; set; }
    }

    public enum OpenConsentFailure
    {
        None,
        /// <summary>No attestation key is configured: the consent API fails closed.</summary>
        NotConfigured,
        /// <summary>The user has no active enrolled device (rule 8).</summary>
        NoActiveDevice,
        InvalidRequest,
        UserCap,
        OriginCap,
        RateLimited,
    }

    public sealed class OpenConsentResult
    {
        public OpenConsentFailure Failure { get; set; }
        public string? Detail { get; set; }
        public string AuthReqId { get; set; } = "";
        public int ExpiresIn { get; set; }
        public int Interval { get; set; }
        /// <summary>Returned to the client only, here and nowhere else.</summary>
        public string BindingCode { get; set; } = "";
    }

    public enum ReceiptOutcome
    {
        Approved,
        Denied,
        CodeFailedRecorded,
        /// <summary>Wrong binding code; attempts remain.</summary>
        WrongCode,
        /// <summary>Three wrong codes: 403 access_denied / code_failed.</summary>
        CodeLocked,
        NotFound,
        AlreadyDecided,
        Expired,
        DeviceInactive,
        InvalidReceipt,
        NotConfigured,
        /// <summary>The request kept changing underneath us; the caller may retry.</summary>
        Conflict,
    }

    public sealed class ReceiptResult
    {
        public ReceiptOutcome Outcome { get; set; }
        public int AttemptsRemaining { get; set; }
        public string? Detail { get; set; }
    }

    public enum ReceiptLookup
    {
        Found,
        /// <summary>Unknown id, or not the caller's: indistinguishable on purpose.</summary>
        NotFound,
        /// <summary>The caller owns the request but there is no decision yet.</summary>
        NotReady,
    }

    /// <summary>Answer of the CIBA token endpoint for a request (RFC 8628 3.5, CIBA 11).</summary>
    public enum PollOutcome
    {
        /// <summary><c>authorization_pending</c></summary>
        Pending,
        /// <summary><c>slow_down</c>: the client polled faster than the announced interval; the interval grew by 5 seconds.</summary>
        SlowDown,
        /// <summary><c>access_denied</c>: denied, or the binding code failed.</summary>
        AccessDenied,
        /// <summary><c>expired_token</c></summary>
        Expired,
        /// <summary><c>invalid_grant</c>: unknown, not this client's request, already redeemed, or not redeemable (indistinguishable on purpose).</summary>
        InvalidGrant,
        /// <summary><c>invalid_target</c>: the <c>resource</c> differs from the one of the request (RFC 8707).</summary>
        InvalidTarget,
        /// <summary>Approved: the receipt is available. The elevated token is issued by the caller.</summary>
        Approved,
    }

    public sealed class PollResult
    {
        public PollOutcome Outcome { get; set; }
        /// <summary>The interval the client must now observe, in seconds.</summary>
        public int Interval { get; set; }
        /// <summary>The approved request (its receipt, bound hash, subject, scope and resource), only when <see cref="Outcome"/> is <see cref="PollOutcome.Approved"/>.</summary>
        public ConsentRequestRecord? Request { get; set; }
    }

    /// <summary>Who asks for a receipt: a bearer token's subject and client, or a confidential client's own credentials.</summary>
    public sealed class ReceiptCaller
    {
        public string? Sub { get; set; }
        public string? ClientId { get; set; }
        /// <summary>True when <see cref="ClientId"/> was authenticated with its own secret.</summary>
        public bool ClientAuthenticated { get; set; }
    }
}
