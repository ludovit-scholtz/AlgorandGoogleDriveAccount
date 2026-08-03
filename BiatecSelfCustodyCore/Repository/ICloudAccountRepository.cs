using Algorand.Algod.Model;
using Nethereum.Signer;

namespace BiatecSelfCustodyCore.Repository
{
    /// <summary>
    /// Identifies one <see cref="Model.SeedVaultEntry"/> to a caller without ever exposing its mnemonic -
    /// <see cref="SeedAddress"/> (the seed's slot-0 derived address) is what the caller uses to refer to
    /// it, e.g. when switching which seed is primary.
    /// </summary>
    /// <param name="SeedAddress">This seed's identifying address (see <see cref="Model.SeedVaultEntry.SeedAddress"/>).</param>
    /// <param name="CreatedUtc">When this seed was generated.</param>
    /// <param name="IsPrimary">Whether this is the seed currently used for normal signing.</param>
    public sealed record SeedSummary(string SeedAddress, DateTimeOffset CreatedUtc, bool IsPrimary);

    /// <summary>
    /// Loads (creating on first use) the AES-encrypted Algorand seed vault file from whichever cloud
    /// storage provider the caller names. A user may hold more than one independently-generated seed (see
    /// <see cref="Model.SeedVault"/>) - exactly one is "primary" at a time and used for normal signing;
    /// the rest are kept forever since they may still authorize other networks or a multisig configured
    /// outside Biatec.
    /// </summary>
    public interface ICloudAccountRepository
    {
        /// <summary>
        /// Loads the account for <paramref name="email"/>/<paramref name="slot"/>, creating a new
        /// encrypted seed vault (with one seed) if one doesn't exist yet. Always derives from whichever
        /// seed is currently primary - <paramref name="slot"/> is the derivation index within that seed,
        /// exactly as before this type held more than one seed.
        /// </summary>
        /// <param name="email"></param>
        /// <param name="slot"></param>
        /// <param name="provider">
        /// Provider name (see <c>Providers.ICloudStorageProvider.Name</c>, e.g. <c>"Google"</c>).
        /// Resolved via <c>Providers.ICloudStorageProviderCatalog</c>, which falls back to a default
        /// for an unrecognized/missing name.
        /// </param>
        /// <param name="accessToken">
        /// Explicit bearer token (device-pairing path, where the token was already resolved from
        /// Redis). If omitted, the token is resolved ambiently from the current signed-in user's
        /// cookie session for <paramref name="provider"/>.
        /// </param>
        /// <param name="seedAddress">
        /// Selects which seed to derive from, by its own identifying (slot-0) address - see
        /// <see cref="Model.SeedVaultEntry.SeedAddress"/>. <c>null</c> (the default) keeps the
        /// original behavior: derive from whichever seed is currently primary, auto-creating the
        /// vault's first seed if none exists yet. A non-null value must name an existing seed - this
        /// method does not auto-create for a caller that named a specific seed - and throws
        /// <see cref="InvalidOperationException"/> if it doesn't exist.
        /// </param>
        Task<Account> LoadAccountAsync(string email, int slot, string provider, string? accessToken = null, string? seedAddress = null);

        /// <summary>
        /// Loads the EVM (Ethereum-family) signing key for <paramref name="email"/>/<paramref name="slot"/> -
        /// the same seed mnemonic <see cref="LoadAccountAsync"/> derives the Algorand key from, just via
        /// <c>ARC76.GetEVMEmailAccount</c> instead of <c>ARC76.GetEmailAccount</c>. Mirrors
        /// <see cref="LoadAccountAsync"/>'s exact seed-resolution/error semantics - callers use the
        /// returned <see cref="EthECKey"/> to sign immediately and let it go out of scope; never persisted,
        /// never logged.
        /// </summary>
        Task<EthECKey> LoadEvmAccountAsync(string email, int slot, string provider, string? accessToken = null, string? seedAddress = null);

        /// <summary>
        /// Derives (without signing or mutating anything) the ARC-76 address at <paramref name="slot"/>
        /// for the seed identified by <paramref name="seedAddress"/> (<c>null</c> = the vault's
        /// current primary seed). Throws <see cref="InvalidOperationException"/> if
        /// <paramref name="seedAddress"/> is given but no seed in the vault has that address.
        /// </summary>
        Task<string> DeriveAddressAsync(string email, string provider, string? seedAddress, int slot, string? accessToken = null);

        /// <summary>
        /// Derives (without signing or mutating anything) the EVM address at <paramref name="slot"/> for the
        /// seed identified by <paramref name="seedAddress"/> (<c>null</c> = the vault's current primary
        /// seed) - the same mnemonic used for <see cref="DeriveAddressAsync"/>, just derived via
        /// <c>ARC76.GetEVMEmailAccount</c> instead of <c>ARC76.GetEmailAccount</c>, so every existing seed
        /// already has an EVM identity without any new consent flow or storage format. Throws
        /// <see cref="InvalidOperationException"/> if <paramref name="seedAddress"/> is given but no seed
        /// in the vault has that address.
        /// </summary>
        Task<string> DeriveEvmAddressAsync(string email, string provider, string? seedAddress, int slot, string? accessToken = null);

        /// <summary>
        /// Resolves and validates a seed selector to its identifying address, without deriving any
        /// slot address - <c>null</c> resolves to the vault's current primary seed (auto-creating the
        /// first seed if the vault is empty, same side effect as <see cref="LoadAccountAsync"/>); a
        /// non-null value is validated against the vault and returned unchanged. Used to resolve a
        /// stable signing identity once per request (e.g. for keying per-address spending limits)
        /// before any slot-specific derivation happens. Throws <see cref="InvalidOperationException"/>
        /// if <paramref name="seedAddress"/> is given but no seed in the vault has that address.
        /// </summary>
        Task<string> ResolveSeedAddressAsync(string email, string provider, string? seedAddress, string? accessToken = null);

        /// <summary>Lists every seed ever generated for this user, most-recently-created last. Never includes a mnemonic.</summary>
        Task<IReadOnlyList<SeedSummary>> ListSeedsAsync(string email, string provider, string? accessToken = null);

        /// <summary>
        /// Generates a brand-new, independent seed and appends it to the vault - existing seeds are never
        /// removed or modified. The very first seed a user ever has is automatically primary; every
        /// subsequent one starts out non-primary until <see cref="SwitchPrimarySeedAsync"/> is called
        /// explicitly, so minting a spare key for an eventual on-chain rekey never silently changes what
        /// Biatec currently signs with.
        /// </summary>
        Task<SeedSummary> CreateSeedAsync(string email, string provider, string? accessToken = null);

        /// <summary>
        /// Marks the seed identified by <paramref name="seedAddress"/> as primary (and every other seed
        /// as non-primary) for future <see cref="LoadAccountAsync"/> calls. Throws
        /// <see cref="InvalidOperationException"/> if no seed in the vault has that address.
        /// </summary>
        Task SwitchPrimarySeedAsync(string email, string provider, string seedAddress, string? accessToken = null);

        /// <summary>
        /// Returns the vault file's current name and raw encrypted bytes (still encrypted - never decrypted
        /// by this method), for copying to a second cloud provider as an explicit backup. Ensures the vault
        /// exists under the active AES key generation's file name first (migrating it from a historical
        /// generation if necessary), so the copy is always the canonical, most-current form.
        /// </summary>
        Task<(string FileName, byte[] EncryptedBytes)> GetEncryptedVaultForBackupAsync(string email, string provider, string? accessToken = null);
    }
}
