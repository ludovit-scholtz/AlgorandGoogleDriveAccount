using System.Numerics;

namespace BiatecSelfCustodyCore.Model
{
    /// <summary>
    /// An unsigned EVM (Ethereum-family) transaction's fields, in whichever fee-market shape the caller
    /// wants signed - legacy (<see cref="GasPrice"/> set) or EIP-1559 (<see cref="MaxFeePerGas"/>/
    /// <see cref="MaxPriorityFeePerGas"/> set). Deliberately a plain field struct rather than a raw
    /// RLP-encoded blob - Nethereum's transaction types can only be safely round-tripped through their own
    /// constructors (their raw-byte constructors are for decoding an already-*signed* transaction, e.g. to
    /// recover its sender - not for building one to sign), so the fields are what <c>DriveService</c>
    /// actually needs to construct the right <c>Nethereum.Model.ISignedTransaction</c> itself before
    /// signing.
    /// </summary>
    public sealed class EvmUnsignedTransaction
    {
        /// <summary>The destination chain's id (EIP-155) - always required, for both fee-market shapes.</summary>
        public BigInteger ChainId { get; set; }

        /// <summary>The sending account's transaction count (nonce).</summary>
        public BigInteger Nonce { get; set; }

        /// <summary>Recipient address, <c>"0x..."</c>. Empty for a contract-creation transaction.</summary>
        public string To { get; set; } = string.Empty;

        /// <summary>Amount to transfer, in wei.</summary>
        public BigInteger Value { get; set; }

        /// <summary>Call data / contract-creation bytecode, hex-encoded (<c>"0x..."</c> or empty).</summary>
        public string Data { get; set; } = string.Empty;

        /// <summary>Maximum gas this transaction may consume.</summary>
        public BigInteger GasLimit { get; set; }

        /// <summary>Legacy (pre-EIP-1559) gas price, in wei. Set this **or** the two EIP-1559 fields below, not both.</summary>
        public BigInteger? GasPrice { get; set; }

        /// <summary>EIP-1559 max total fee per gas, in wei.</summary>
        public BigInteger? MaxFeePerGas { get; set; }

        /// <summary>EIP-1559 max priority fee (tip) per gas, in wei.</summary>
        public BigInteger? MaxPriorityFeePerGas { get; set; }
    }
}
