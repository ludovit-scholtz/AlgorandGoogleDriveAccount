namespace BiatecOIDC.BusinessLogic
{
    /// <summary>
    /// Persists which (<c>seedAddress</c>, <c>slot</c>) key material signs for a given address, so wallet
    /// endpoints can be addressed by the real address alone instead of always needing the seed/slot pair.
    /// Stored AES-encrypted in the user's own cloud drive (never Biatec's infrastructure) in a file separate
    /// from the seed vault itself - see <c>AddressActivationService</c>'s remarks for why. Every entry ever
    /// stored here is, by construction, already verified correct at the moment it was created - there is no
    /// half-registered/pending state.
    /// </summary>
    public interface IAddressActivationService
    {
        /// <summary>Looks up which seed/slot signs for <paramref name="address"/>, or <c>null</c> if it was never activated.</summary>
        Task<AddressActivationEntry?> TryResolveAsync(string email, string provider, string? accessToken, string address, CancellationToken cancellationToken = default);

        /// <summary>
        /// Records that <paramref name="seedAddress"/>/<paramref name="slot"/> signs for
        /// <paramref name="address"/>. Idempotent - re-activating the same address just overwrites its entry
        /// (e.g. if it's later re-pointed at a different seed/slot). The caller is responsible for having
        /// already verified this pairing is actually correct (trivially, for a natively-derived address, or
        /// via an on-chain rekey check) before calling this - this method only ever persists.
        /// </summary>
        Task<AddressActivationEntry> ActivateAsync(string email, string provider, string? accessToken, string address, string family, string seedAddress, int slot, CancellationToken cancellationToken = default);

        /// <summary>Lists every address ever activated for this user.</summary>
        Task<IReadOnlyList<AddressActivationEntry>> ListAsync(string email, string provider, string? accessToken, CancellationToken cancellationToken = default);
    }
}
