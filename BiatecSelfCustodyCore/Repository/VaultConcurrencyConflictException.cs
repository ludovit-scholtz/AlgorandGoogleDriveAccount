namespace BiatecSelfCustodyCore.Repository
{
    /// <summary>
    /// Thrown when a seed-vault mutation (<see cref="ICloudAccountRepository.CreateSeedAsync"/>,
    /// <see cref="ICloudAccountRepository.SwitchPrimarySeedAsync"/>, or the very first seed auto-created by
    /// <see cref="ICloudAccountRepository.LoadAccountAsync"/>) detects that the vault file on the storage
    /// provider changed between this call's own read and its write - i.e. a concurrent mutation against the
    /// same account raced this one and would otherwise silently overwrite it. Rather than letting the losing
    /// write clobber the winning one (security audit finding M-01/R-021), the losing call fails with this
    /// exception so its caller can retry against the now-current state instead of a seed creation or
    /// primary-seed switch being silently lost.
    /// </summary>
    public sealed class VaultConcurrencyConflictException : Exception
    {
        public VaultConcurrencyConflictException(string message) : base(message)
        {
        }
    }
}
