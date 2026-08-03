using System.Globalization;
using System.Numerics;
using BiatecOIDC.Model;
using BiatecSelfCustodyCore.Model;

namespace BiatecOIDC.Helper
{
    /// <summary>
    /// Parses the wallet API's wire-facing <see cref="EvmTransactionRequest"/> (all numeric fields as
    /// decimal or <c>0x</c>-prefixed hex strings, JSON-friendly) into <see cref="EvmUnsignedTransaction"/>
    /// (the plain <c>BigInteger</c>-typed struct <c>DriveService</c> actually signs).
    /// </summary>
    public static class EvmTransactionRequestParser
    {
        /// <exception cref="FormatException">A required field is missing, or a numeric field couldn't be parsed.</exception>
        public static EvmUnsignedTransaction Parse(EvmTransactionRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.To))
            {
                throw new FormatException("EVM transaction 'to' is required.");
            }

            var hasGasPrice = !string.IsNullOrWhiteSpace(request.GasPrice);
            var hasEip1559Fees = !string.IsNullOrWhiteSpace(request.MaxFeePerGas) || !string.IsNullOrWhiteSpace(request.MaxPriorityFeePerGas);
            if (hasGasPrice == hasEip1559Fees)
            {
                throw new FormatException("EVM transaction must set exactly one of 'gasPrice' (legacy) or 'maxFeePerGas'+'maxPriorityFeePerGas' (EIP-1559).");
            }

            return new EvmUnsignedTransaction
            {
                ChainId = ParseRequired(request.ChainId, "chainId"),
                Nonce = ParseRequired(request.Nonce, "nonce"),
                To = request.To,
                Value = ParseOptional(request.Value) ?? BigInteger.Zero,
                Data = request.Data ?? string.Empty,
                GasLimit = ParseRequired(request.GasLimit, "gasLimit"),
                GasPrice = ParseOptional(request.GasPrice),
                MaxFeePerGas = ParseOptional(request.MaxFeePerGas),
                MaxPriorityFeePerGas = ParseOptional(request.MaxPriorityFeePerGas)
            };
        }

        private static BigInteger ParseRequired(string? value, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new FormatException($"EVM transaction '{fieldName}' is required.");
            }

            return ParseOptional(value)!.Value;
        }

        private static BigInteger? ParseOptional(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                var hex = value[2..];
                if (hex.Length == 0 || !BigInteger.TryParse("0" + hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexValue))
                {
                    throw new FormatException($"'{value}' is not a valid hex integer.");
                }

                return hexValue;
            }

            if (!BigInteger.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var decimalValue) || decimalValue < 0)
            {
                throw new FormatException($"'{value}' is not a valid non-negative integer.");
            }

            return decimalValue;
        }
    }
}
