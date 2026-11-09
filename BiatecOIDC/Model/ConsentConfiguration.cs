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
    }
}
