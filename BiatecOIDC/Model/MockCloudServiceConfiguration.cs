namespace BiatecOIDC.Model
{
    /// <summary>
    /// One configured mock test account - see <c>MOCK_TESTING.md</c>. <see cref="ScopeId"/> is the
    /// identifier used to pick this account on the mock provider's account-picker page (and via the
    /// <c>scopeId</c> fast-track query parameter); <see cref="Email"/> is the synthetic identity the seed
    /// vault is bound to; <see cref="Mnemonic"/> is the ARC-76 secret (25-word Algorand mnemonic)
    /// deterministically seeded into the mock vault, so the same address always comes back - across app
    /// restarts - for as long as this config entry doesn't change.
    /// </summary>
    public class MockCloudAccountConfiguration
    {
        public string ScopeId { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Mnemonic { get; set; } = string.Empty;
    }

    /// <summary>
    /// Bound from <c>CloudServices:Mock</c> - see <c>MOCK_TESTING.md</c>. Deliberately never enabled by
    /// default; only meant for local/dev testing of the full OIDC authorize/token flow (and downstream
    /// Algorand/Ethereum signing) against a deterministic in-memory account, without needing a real
    /// Google/Microsoft sign-in.
    /// </summary>
    public class MockCloudServiceConfiguration
    {
        public bool Enabled { get; set; }
        public List<MockCloudAccountConfiguration> Accounts { get; set; } = new();
    }
}
