namespace BiatecOIDC.Model
{
    /// <summary>Settings of the consent API (section <c>Consent</c>). Supply the key from the secret store, never from a committed file.</summary>
    public sealed class ConsentConfiguration
    {
        /// <summary>
        /// PEM (PKCS#8 or SEC1) EC P-256 private key that signs device attestations. Without it the consent API fails
        /// closed: devices are enrolled without an attestation and no consent request is opened.
        /// </summary>
        public string? AttestationSigningKeyPem { get; set; }

        /// <summary>The <c>kid</c> the attestation key has in the JWKS. Change it when the key is rotated.</summary>
        public string AttestationKeyId { get; set; } = "biatec-consent-1";

        /// <summary>How long an attestation is valid (receipts carry it, so it outlives a single request).</summary>
        public int AttestationLifetimeDays { get; set; } = 400;

        /// <summary>Validity of a request in seconds, default and maximum (protocol default 300).</summary>
        public int RequestedExpirySeconds { get; set; } = 300;

        /// <summary>Most open requests one user can have at once (429 beyond).</summary>
        public int MaxOpenRequestsPerUser { get; set; } = 20;

        /// <summary>Most pending requests per client, user and origin (protocol rule 10: three).</summary>
        public int MaxPendingPerClientOrigin { get; set; } = 3;

        /// <summary>Anti-spam rate per client and user (protocol rule 10: 60 an hour, no per-second burst limit).</summary>
        public int MaxRequestsPerClientUserPerHour { get; set; } = 60;

        /// <summary>Binding code attempts in total within a request (protocol section 7: three).</summary>
        public int MaxWrongCodes { get; set; } = 3;

        /// <summary>Announced polling interval in seconds.</summary>
        public int PollIntervalSeconds { get; set; } = 2;

        /// <summary>How long a decided or expired request stays readable (receipt retrieval, audit).</summary>
        public int RetentionDays { get; set; } = 30;
    }
}
