namespace BiatecMCP.BusinessLogic.AlgoSafe
{
    public sealed class AlgoSafeMemberView
    {
        public string Address { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;

        /// <summary>1 = human, other values are protocol-defined account kinds (e.g. agent) - echoed as stored.</summary>
        public ulong AccountType { get; set; }
    }

    public sealed class AlgoSafeGroupView
    {
        public ulong GroupId { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool Active { get; set; }

        /// <summary>"Standard" or "Custodian".</summary>
        public string GroupType { get; set; } = string.Empty;

        public ulong Threshold { get; set; }
        public ulong MemberCount { get; set; }
        public ulong AllowedActionsMask { get; set; }
        public IReadOnlyList<string> AllowedActions { get; set; } = Array.Empty<string>();
        public ulong AdminPrivilegesMask { get; set; }
        public IReadOnlyList<string> AdminPrivileges { get; set; } = Array.Empty<string>();

        /// <summary>The asset the daily/monthly limits are counted in (0 = ALGO).</summary>
        public ulong LimitAssetId { get; set; }

        /// <summary>0 = unlimited.</summary>
        public ulong DailyLimit { get; set; }
        public ulong DailyUsage { get; set; }
        public ulong MonthlyLimit { get; set; }
        public ulong MonthlyUsage { get; set; }
        public ulong CooldownRounds { get; set; }
        public ulong LastExecutionRound { get; set; }
        public IReadOnlyList<AlgoSafeMemberView> Members { get; set; } = Array.Empty<AlgoSafeMemberView>();
    }

    public sealed class AlgoSafeView
    {
        public ulong AppId { get; set; }

        /// <summary>The safe's own account - the address that holds its funds and is the sender of every executed transaction.</summary>
        public string SafeAddress { get; set; } = string.Empty;

        public ulong SafeBalanceMicroAlgo { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Creator { get; set; } = string.Empty;

        /// <summary>The approval program's SHA-256 (hex) - identifies the deployed contract version.</summary>
        public string ApprovalProgramHash { get; set; } = string.Empty;

        /// <summary>E.g. "v3.4.0", or <c>null</c> when the hash isn't a supported version (see <see cref="Supported"/>).</summary>
        public string? ContractVersion { get; set; }

        /// <summary>False for an app that isn't a supported AlgoSafe version - no further fields are populated and no transactions will be built for it.</summary>
        public bool Supported { get; set; }

        public bool Bootstrapped { get; set; }
        public bool Paused { get; set; }
        public ulong ValidatorAppId { get; set; }
        public ulong NextProposalId { get; set; }
        public IReadOnlyList<AlgoSafeGroupView> Groups { get; set; } = Array.Empty<AlgoSafeGroupView>();
    }

    public sealed class AlgoSafeProposalSummaryView
    {
        public ulong ProposalId { get; set; }
        public ulong GroupId { get; set; }

        /// <summary>"Active", "Ready", "Executed" or "Cancelled".</summary>
        public string Status { get; set; } = string.Empty;

        /// <summary>"TransactionGroup" or "AdminChange".</summary>
        public string Type { get; set; } = string.Empty;

        public ulong ApprovalsCount { get; set; }
        public ulong Threshold { get; set; }
        public ulong ExpiryRound { get; set; }
        public bool Expired { get; set; }
        public string Proposer { get; set; } = string.Empty;
        public ulong PayloadVersion { get; set; }
        public ulong TotalTransactions { get; set; }
    }

    /// <summary>One decoded entry of a transaction-group proposal - what the safe will send from its own account when it executes.</summary>
    public sealed class AlgoSafeProposedTransactionView
    {
        /// <summary>"Payment", "AssetTransfer", or another kind this server doesn't decode (shown as "Unsupported(n)").</summary>
        public string Kind { get; set; } = string.Empty;

        /// <summary>The account the inner transaction is sent from; the safe's own address unless a rekeyed account is named.</summary>
        public string Sender { get; set; } = string.Empty;

        public string? Receiver { get; set; }
        public ulong Amount { get; set; }

        /// <summary>0 for ALGO.</summary>
        public ulong AssetId { get; set; }

        /// <summary>True for a close-out, which sweeps the sender's whole remaining balance to <see cref="CloseTo"/>.</summary>
        public bool ClosesAccount { get; set; }

        public string? CloseTo { get; set; }
        public string Note { get; set; } = string.Empty;
    }

    public sealed class AlgoSafeProposalView
    {
        public AlgoSafeProposalSummaryView Summary { get; set; } = new();
        public IReadOnlyList<string> Approvers { get; set; } = Array.Empty<string>();

        /// <summary>False when the safe has too many boxes to enumerate, so <see cref="Approvers"/> may be incomplete (the caller's own approval is still checked exactly).</summary>
        public bool ApproversComplete { get; set; } = true;
        public IReadOnlyList<AlgoSafeProposedTransactionView> Transactions { get; set; } = Array.Empty<AlgoSafeProposedTransactionView>();

        /// <summary>Set only when a caller address was supplied.</summary>
        public bool? CallerIsMember { get; set; }
        public bool? CallerHasApproved { get; set; }
        public bool? CallerCanApprove { get; set; }

        /// <summary>The proposal is READY and unexpired - anyone can execute it.</summary>
        public bool CanExecute { get; set; }
    }

    /// <summary>The unsigned transactions of one step, ready for <c>signTransaction</c>.</summary>
    public sealed class AlgoSafeBuiltStep
    {
        /// <summary>The unsigned transactions in group order, each msgpack-encoded - pass the whole list to <c>signTransaction</c>.</summary>
        public IReadOnlyList<byte[]> UnsignedTransactions { get; set; } = Array.Empty<byte[]>();

        public ulong TotalFeeMicroAlgo { get; set; }
        public int InnerTransactionCount { get; set; }
        public string Summary { get; set; } = string.Empty;
    }

    /// <summary>What <c>getAlgoSafeDeployment</c> found for a deployment transaction.</summary>
    public sealed class AlgoSafeDeploymentView
    {
        public bool Confirmed { get; set; }
        public ulong ApplicationId { get; set; }

        /// <summary>"AlgoSafe", "AlgoSafeTxnValidator", or "Other" (an application that isn't either).</summary>
        public string Kind { get; set; } = string.Empty;

        public string? SafeAddress { get; set; }
        public ulong ConfirmedRound { get; set; }
    }
}
