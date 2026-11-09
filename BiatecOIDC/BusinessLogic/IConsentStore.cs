using BiatecOIDC.Model;

namespace BiatecOIDC.BusinessLogic
{
    /// <summary>Persistence of consent requests. All state changes are atomic and compare-and-set, so two devices or two calls cannot both win.</summary>
    public interface IConsentStore
    {
        /// <summary>Stores a new request and takes an open slot, or says which cap refused it. Nothing is written when refused.</summary>
        Task<OpenOutcome> TryOpenAsync(ConsentRequestRecord record, ConsentLimits limits, DateTimeOffset now);

        Task<ConsentRequestRecord?> GetAsync(string authReqId);

        /// <summary>
        /// Replaces <paramref name="expected"/> by <paramref name="updated"/> only if the stored version still equals
        /// <c>expected.Version</c>; the stored version becomes <c>expected.Version + 1</c>. False when someone else changed it first.
        /// When the update turns a pending request into a decided one, its open slots are given back in the same atomic step, so a
        /// failure between the two can never leave a decided request holding a slot.
        /// </summary>
        Task<bool> TryUpdateAsync(ConsentRequestRecord expected, ConsentRequestRecord updated);

        /// <summary>Ids of the user's requests that are still open (not decided, not expired).</summary>
        Task<IReadOnlyList<string>> ListOpenIdsAsync(string sub, DateTimeOffset now);

        /// <summary>Gives the open slots of a decided or expired request back.</summary>
        Task ReleaseAsync(ConsentRequestRecord record);
    }
}
