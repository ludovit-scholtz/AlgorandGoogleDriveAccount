namespace BiatecMCP.BusinessLogic.AlgoSafe
{
    /// <summary>
    /// Constants mirroring the AlgoSafe contract family (https://github.com/ludovit-scholtz/algo-safe,
    /// <c>projects/algo-safe-contracts/src/constants.ts</c> and <c>smart_contracts/algo_safe/contract.algo.ts</c>) - they must track
    /// the deployed contract exactly, never be "improved" locally.
    /// </summary>
    public static class AlgoSafeConstants
    {
        // SafeTxn envelope tags (the first field of each payload entry).
        public const ulong TxPayment = 1;
        public const ulong TxAsset = 2;
        public const ulong TxApp = 3;
        public const ulong TxKeyReg = 4;
        public const ulong TxAssetConfig = 5;
        public const ulong TxRekey = 6;

        // SignerGroup.allowedActions bitmask.
        public const ulong ActPay = 1;
        public const ulong ActAxfer = 2;
        public const ulong ActApp = 4;
        public const ulong ActKeyReg = 8;
        public const ulong ActAssetConfig = 16;
        public const ulong ActRekey = 32;
        public const ulong ActAll = 63;

        // SignerGroup.adminPrivileges bitmask.
        public const ulong PrivGroup = 1;
        public const ulong PrivPolicy = 2;
        public const ulong PrivAll = 7;

        // SignerGroup.groupType.
        public const ulong GroupTypeStandard = 0;
        public const ulong GroupTypeCustodian = 1;

        // Proposal.status.
        public const ulong StatusActive = 1;
        public const ulong StatusReady = 2;
        public const ulong StatusExecuted = 3;
        public const ulong StatusCancelled = 4;

        // Proposal.payloadType.
        public const ulong PayloadTypeTransactionGroup = 1;
        public const ulong PayloadTypeAdmin = 5;

        /// <summary>Payload chunk boxes are keyed <c>proposalId * TxgKeyMultiplier + chunkIndex</c> (chunks 1-6).</summary>
        public const ulong TxgKeyMultiplier = 7;

        /// <summary>A proposal's payload is split over at most six chunk boxes.</summary>
        public const ulong MaxPayloadChunks = 6;

        public const int MaxGroupTransactions = 16;

        /// <summary>The contract (v3.4.0) rejects <c>expiryRound &gt; Global.round + this</c>.</summary>
        public const ulong MaxProposalLifetimeRounds = 20_000_000;

        /// <summary>A proposal must outlive the time it takes to sign, broadcast and confirm it (and still leave approvers time).</summary>
        public const ulong MinProposalLifetimeRounds = 100;

        /// <summary>The payload array is one application argument; an app call's arguments total at most 2048 bytes, minus the selector, group id, expiry, flag and budget arguments.</summary>
        public const int MaxPayloadArgumentBytes = 1900;

        /// <summary>
        /// <c>ensureBudgetValue</c> the safe's own UI passes when a call executes a proposal (propose-with-execute / executeProposal):
        /// every ~700 requested budget spawns one opup inner call (one min fee each).
        /// </summary>
        public const ulong ExecutionEnsureBudget = 6000;

        // Box name prefixes (ARC-56 `state.maps.box`): the safe's BoxMaps are keyed by these ASCII prefixes + the ARC-4 encoded key.
        public const string GroupBoxPrefix = "g";
        public const string MemberBoxPrefix = "m";
        public const string ProposalBoxPrefix = "p";
        public const string ApprovalBoxPrefix = "a";
        public const string TransactionGroupBoxPrefix = "txg";

        /// <summary>
        /// SHA-256 (hex) of the compiled AlgoSafeTxnValidator approval program, pinned by the safe at <c>createApplication</c> -
        /// from <c>validator-hash.generated.ts</c>. A validator app is only acceptable if its approval program hashes to this.
        /// </summary>
        public const string ValidatorApprovalSha256Hex = "0dd692344f80e7d5770f47bcde26c31eaaf24d45b5d177dfcbc7241742e188b1";

        /// <summary>The approval-program hash of the latest contract version - the one <c>createAlgoSafe</c> deploys.</summary>
        public const string LatestApprovalSha256Hex = "5b99b7491bc8448245d5eb83f1f80558c3b20141dcad958f87f2819441af526e";

        /// <summary>
        /// Approval-program hashes that share the current ("modern") ABI call surface (tagged-envelope payloads,
        /// <c>approveProposal(proposalId, expectedPayloadVersion, ensureBudgetValue)</c>, box keys <c>proposalId*7+slot</c>) - the
        /// <c>MODERN_ABI_CONTRACT_HASHES</c> of the algo-safe package, narrowed to v3.2.0+ because the box structs (signer group, member, proposal) are decoded with the v3.4.0 client and were only verified unchanged from v3.2.0 on. Older deployed hashes use a different, incompatible ABI and are
        /// refused rather than guessed at; extend this table only when a new version ships WITHOUT breaking that surface.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> SupportedContractVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [LatestApprovalSha256Hex] = "v3.4.0",
            ["b096f50b39dff57a9df61e36a9a74a35e9f892854495f4c5efc42e5d8822bf35"] = "v3.3.0",
            ["3f8d0cb07960bf2c8af42fb41b7d7fc2673dd831d987e80ee0e0c43fcefaa0ec"] = "v3.2.0"
        };

        public static string StatusName(ulong status) => status switch
        {
            StatusActive => "Active",
            StatusReady => "Ready",
            StatusExecuted => "Executed",
            StatusCancelled => "Cancelled",
            _ => $"Unknown({status})"
        };

        public static IReadOnlyList<string> ActionNames(ulong allowedActions)
        {
            var names = new List<string>();
            if ((allowedActions & ActPay) != 0) names.Add("payment");
            if ((allowedActions & ActAxfer) != 0) names.Add("asset-transfer");
            if ((allowedActions & ActApp) != 0) names.Add("app-call");
            if ((allowedActions & ActKeyReg) != 0) names.Add("key-registration");
            if ((allowedActions & ActAssetConfig) != 0) names.Add("asset-config");
            if ((allowedActions & ActRekey) != 0) names.Add("rekey");
            return names;
        }

        public static IReadOnlyList<string> PrivilegeNames(ulong adminPrivileges)
        {
            var names = new List<string>();
            if ((adminPrivileges & PrivGroup) != 0) names.Add("manage-groups");
            if ((adminPrivileges & PrivPolicy) != 0) names.Add("manage-policy");
            return names;
        }
    }
}
