using System.Text;
using System.Text.Json;
using Algorand;
using Algorand.Indexer;

namespace BiatecMCP.BusinessLogic
{
    /// <summary>
    /// Discovers and fetches Aramid Finance's live bridge configuration exactly as its own integration guide
    /// describes: find the most recent Algorand mainnet transaction on Aramid's config account whose note
    /// starts with <c>aramid-config/v1:j</c>, take the IPFS hash that follows, then fetch that hash's JSON
    /// from a public IPFS gateway. No SDK/REST endpoint exists for this - it's deliberately discovered
    /// on-chain so the config can't be tampered with in transit without also forging a signed Algorand
    /// transaction from Aramid's own config account.
    /// </summary>
    public sealed class AramidBridgeConfigProvider : IAramidBridgeConfigProvider
    {
        /// <summary>Aramid's config account on Algorand mainnet (chain id 416001, AramidChain id 101003) - see the integration guide.</summary>
        public const string ConfigAccountAddress = "ARAMICOCHLHSX3G5KCKK23M72ETI537GK5VGLOVHXAGPIELWYJKIMGKK6I";

        private const string ConfigNotePrefix = "aramid-config/v1:j";

        // A free, public Algorand Indexer/IPFS gateway - no API key required for either, matching this
        // repo's precedent of defaulting to well-known public Algorand infrastructure (see Program.cs's
        // Algod defaults) rather than requiring the operator to provision dedicated infrastructure just to
        // discover Aramid's config.
        private const string IndexerApiAddress = "https://mainnet-idx.algonode.cloud";
        private const string IpfsGatewayBaseUrl = "https://ipfs.io/ipfs/";

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public async Task<AramidConfigRoot> GetConfigAsync(CancellationToken cancellationToken = default)
        {
            var ipfsHash = await FindConfigIpfsHashAsync(cancellationToken);

            using var ipfsHttpClient = HttpClientConfigurator.ConfigureHttpClient(IpfsGatewayBaseUrl, string.Empty);
            var json = await ipfsHttpClient.GetStringAsync(ipfsHash, cancellationToken);

            return JsonSerializer.Deserialize<AramidConfigRoot>(json, JsonOptions)
                ?? throw new InvalidOperationException("Aramid bridge configuration could not be parsed.");
        }

        private static async Task<string> FindConfigIpfsHashAsync(CancellationToken cancellationToken)
        {
            using var indexerHttpClient = HttpClientConfigurator.ConfigureHttpClient(IndexerApiAddress, string.Empty);
            var lookupApi = new LookupApi(indexerHttpClient);

            // The Indexer's note-prefix filter expects the prefix base64-encoded.
            var notePrefixBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(ConfigNotePrefix));

            var response = await lookupApi.lookupAccountTransactionsAsync(
                cancellationToken,
                accountId: ConfigAccountAddress,
                notePrefix: notePrefixBase64,
                limit: 1);

            var configTransaction = response.Transactions?.FirstOrDefault()
                ?? throw new InvalidOperationException("Could not find Aramid's bridge configuration transaction on Algorand mainnet.");

            if (configTransaction.Note == null || configTransaction.Note.Length == 0)
            {
                throw new InvalidOperationException("Aramid's bridge configuration transaction has no note.");
            }

            var noteText = Encoding.UTF8.GetString(configTransaction.Note);
            if (!noteText.StartsWith(ConfigNotePrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Aramid's bridge configuration transaction note has an unexpected format.");
            }

            var hash = noteText[ConfigNotePrefix.Length..].Trim();
            if (string.IsNullOrEmpty(hash))
            {
                throw new InvalidOperationException("Aramid's bridge configuration transaction note is missing its IPFS hash.");
            }

            return hash;
        }
    }
}
