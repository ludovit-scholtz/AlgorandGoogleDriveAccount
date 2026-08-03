using BiatecSelfCustodyCore.BusinessLogic;
using BiatecSelfCustodyCore.Model;
using BiatecSelfCustodyCore.Repository;
using Microsoft.Extensions.Logging;
using Moq;
using NBitcoin;
using NBitcoin.Altcoins;
using Nethereum.Model;
using Nethereum.Signer;

namespace BiatecMCPTests
{
    /// <summary>
    /// Covers <see cref="DriveService.SignEvmTransactionAsync"/> - builds the right
    /// <c>Nethereum.Model.ISignedTransaction</c> (legacy+EIP-155 if <see cref="EvmUnsignedTransaction.GasPrice"/>
    /// is set, EIP-1559 if <see cref="EvmUnsignedTransaction.MaxFeePerGas"/>/
    /// <see cref="EvmUnsignedTransaction.MaxPriorityFeePerGas"/> are) from the seed's derived
    /// <see cref="EthECKey"/>, mirroring the existing Algorand signing path
    /// (<see cref="DriveService.SignTransactionAsync"/>) but via <c>Nethereum.Model</c>/<c>Nethereum.Signer</c>
    /// instead of the Algorand4 SDK. <see cref="ICloudAccountRepository"/> is mocked - real seed derivation
    /// is covered by <see cref="CloudAccountRepositoryTests"/>.
    /// </summary>
    [TestFixture]
    public class DriveServiceTests
    {
        private const string TestEmail = "user@example.com";
        private const string TestProvider = "Fake";
        private const string TestTo = "0x13f022d72158410433cbd66f5dd8bf6d2d0999c";

        private Mock<ICloudAccountRepository> _mockAccountRepository = null!;
        private DriveService _service = null!;
        private EthECKey _signingKey = null!;

        [SetUp]
        public void SetUp()
        {
            _signingKey = EthECKey.GenerateKey();
            _mockAccountRepository = new Mock<ICloudAccountRepository>();
            _mockAccountRepository
                .Setup(r => r.LoadEvmAccountAsync(TestEmail, It.IsAny<int>(), TestProvider, It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync(_signingKey);
            _service = new DriveService(_mockAccountRepository.Object, new Mock<ILogger<DriveService>>().Object);
        }

        private static EvmUnsignedTransaction BuildLegacyTransaction(int chainId = 1) => new()
        {
            ChainId = chainId,
            Nonce = 0,
            To = TestTo,
            Value = System.Numerics.BigInteger.Parse("1000000000000000000"),
            Data = string.Empty,
            GasLimit = 21000,
            GasPrice = System.Numerics.BigInteger.Parse("20000000000")
        };

        private static EvmUnsignedTransaction Build1559Transaction(int chainId = 1) => new()
        {
            ChainId = chainId,
            Nonce = 0,
            To = TestTo,
            Value = System.Numerics.BigInteger.Parse("1000000000000000000"),
            Data = string.Empty,
            GasLimit = 21000,
            MaxFeePerGas = System.Numerics.BigInteger.Parse("30000000000"),
            MaxPriorityFeePerGas = System.Numerics.BigInteger.Parse("1000000000")
        };

        private static string RecoverSender(byte[] signedRlp) =>
            TransactionVerificationAndRecovery.GetSenderAddress(TransactionFactory.CreateTransaction(signedRlp));

        [Test]
        public async Task SignEvmTransactionAsync_LegacyChainIdTransaction_RecoversToTheSigningKeysAddress()
        {
            var signed = await _service.SignEvmTransactionAsync(TestEmail, BuildLegacyTransaction(), TestProvider);

            Assert.That(RecoverSender(signed), Is.EqualTo(_signingKey.GetPublicAddress()).IgnoreCase);
        }

        [Test]
        public async Task SignEvmTransactionAsync_Eip1559Transaction_RecoversToTheSigningKeysAddress()
        {
            var signed = await _service.SignEvmTransactionAsync(TestEmail, Build1559Transaction(), TestProvider);

            Assert.That(RecoverSender(signed), Is.EqualTo(_signingKey.GetPublicAddress()).IgnoreCase);
        }

        [Test]
        public async Task SignEvmTransactionAsync_DifferentChainId_ProducesDifferentSignatureButSameSender()
        {
            var signedMainnet = await _service.SignEvmTransactionAsync(TestEmail, BuildLegacyTransaction(1), TestProvider);
            var signedOther = await _service.SignEvmTransactionAsync(TestEmail, BuildLegacyTransaction(137), TestProvider);

            Assert.That(signedOther, Is.Not.EqualTo(signedMainnet));
            Assert.That(RecoverSender(signedMainnet), Is.EqualTo(_signingKey.GetPublicAddress()).IgnoreCase);
            Assert.That(RecoverSender(signedOther), Is.EqualTo(_signingKey.GetPublicAddress()).IgnoreCase);
        }

        [Test]
        public void SignEvmTransactionAsync_MissingEmail_ThrowsArgumentException()
        {
            Assert.That(async () => await _service.SignEvmTransactionAsync(string.Empty, BuildLegacyTransaction(), TestProvider),
                Throws.ArgumentException);
        }

        [Test]
        public void SignEvmTransactionAsync_NullTransaction_ThrowsArgumentException()
        {
            Assert.That(async () => await _service.SignEvmTransactionAsync(TestEmail, null!, TestProvider),
                Throws.ArgumentException);
        }

        [Test]
        public async Task SignEvmTransactionAsync_ForwardsSeedAddressAndSlotToRepository()
        {
            await _service.SignEvmTransactionAsync(TestEmail, BuildLegacyTransaction(), TestProvider, "access-token", "SEED-ADDR", 4);

            _mockAccountRepository.Verify(r => r.LoadEvmAccountAsync(TestEmail, 4, TestProvider, "access-token", "SEED-ADDR"), Times.Once);
        }

        // ───────────────────────── SignBitcoinTransactionAsync (Bitcoin + Bitcoin Cash) ─────────────────────────

        private Key _bitcoinKey = null!;

        [TearDown]
        public void TearDown() => _bitcoinKey?.Dispose();

        private void SetUpBitcoinKey()
        {
            _bitcoinKey = new Key();
            _mockAccountRepository
                .Setup(r => r.LoadBitcoinKeyAsync(TestEmail, It.IsAny<int>(), TestProvider, It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync(_bitcoinKey);
        }

        private static BitcoinUnsignedTransaction BuildSelfSpendTransaction(NBitcoin.Network network, BitcoinAddress myAddress, out uint256 fundingTxId)
        {
            var fundingTx = network.CreateTransaction();
            fundingTx.Outputs.Add(new TxOut(Money.Coins(1.0m), myAddress));
            fundingTxId = fundingTx.GetHash();

            return new BitcoinUnsignedTransaction
            {
                Inputs = { new BitcoinUtxoInput { TxId = fundingTxId.ToString(), Vout = 0, AmountSatoshis = Money.Coins(1.0m).Satoshi } },
                Outputs =
                {
                    new BitcoinTransactionOutput { Address = myAddress.ToString(), AmountSatoshis = Money.Coins(0.5m).Satoshi },
                    new BitcoinTransactionOutput { Address = myAddress.ToString(), AmountSatoshis = Money.Coins(0.4999m).Satoshi, IsChange = true }
                }
            };
        }

        [Test]
        public async Task SignBitcoinTransactionAsync_Bitcoin_ProducesAValidSelfVerifyingTransaction()
        {
            SetUpBitcoinKey();
            var myAddress = _bitcoinKey.PubKey.GetAddress(ScriptPubKeyType.Segwit, NBitcoin.Network.Main);
            var unsigned = BuildSelfSpendTransaction(NBitcoin.Network.Main, myAddress, out _);

            var signedBytes = await _service.SignBitcoinTransactionAsync(TestEmail, BitcoinChainFamily.Bitcoin, unsigned, TestProvider);

            var signedTx = NBitcoin.Network.Main.CreateTransaction();
            signedTx.FromBytes(signedBytes);
            var builder = NBitcoin.Network.Main.CreateTransactionBuilder();
            builder.AddCoins(new Coin(new OutPoint(uint256.Parse(unsigned.Inputs[0].TxId), 0), new TxOut(Money.Satoshis(unsigned.Inputs[0].AmountSatoshis), myAddress.ScriptPubKey)));
            Assert.That(builder.Verify(signedTx), Is.True);
        }

        [Test]
        public async Task SignBitcoinTransactionAsync_BitcoinCash_ProducesAValidSelfVerifyingTransaction()
        {
            SetUpBitcoinKey();
            var network = BCash.Instance.Mainnet;
            var myAddress = (BitcoinAddress)_bitcoinKey.PubKey.Hash.GetAddress(network);
            var unsigned = BuildSelfSpendTransaction(network, myAddress, out _);

            var signedBytes = await _service.SignBitcoinTransactionAsync(TestEmail, BitcoinChainFamily.BitcoinCash, unsigned, TestProvider);

            var signedTx = network.CreateTransaction();
            signedTx.FromBytes(signedBytes);
            var builder = network.CreateTransactionBuilder();
            builder.AddCoins(new Coin(new OutPoint(uint256.Parse(unsigned.Inputs[0].TxId), 0), new TxOut(Money.Satoshis(unsigned.Inputs[0].AmountSatoshis), myAddress.ScriptPubKey)));
            Assert.That(builder.Verify(signedTx), Is.True);
        }

        [Test]
        public void SignBitcoinTransactionAsync_NoInputs_ThrowsArgumentException()
        {
            SetUpBitcoinKey();

            Assert.That(async () => await _service.SignBitcoinTransactionAsync(TestEmail, BitcoinChainFamily.Bitcoin, new BitcoinUnsignedTransaction(), TestProvider),
                Throws.ArgumentException);
        }

        [Test]
        public async Task SignBitcoinTransactionAsync_ForwardsSeedAddressAndSlotToRepository()
        {
            SetUpBitcoinKey();
            var myAddress = _bitcoinKey.PubKey.GetAddress(ScriptPubKeyType.Segwit, NBitcoin.Network.Main);
            var unsigned = BuildSelfSpendTransaction(NBitcoin.Network.Main, myAddress, out _);

            await _service.SignBitcoinTransactionAsync(TestEmail, BitcoinChainFamily.Bitcoin, unsigned, TestProvider, "access-token", "SEED-ADDR", 4);

            _mockAccountRepository.Verify(r => r.LoadBitcoinKeyAsync(TestEmail, 4, TestProvider, "access-token", "SEED-ADDR"), Times.Once);
        }
    }
}
