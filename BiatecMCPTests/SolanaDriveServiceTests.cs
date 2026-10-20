using BiatecSelfCustodyCore.BusinessLogic;
using BiatecSelfCustodyCore.Model;
using BiatecSelfCustodyCore.Repository;
using Microsoft.Extensions.Logging;
using Moq;
using Solnet.Rpc.Models;
using Solnet.Wallet;
using SolanaArc76 = ARC76Account.Solana.ARC76;

namespace BiatecMCPTests
{
    /// <summary>
    /// Covers <see cref="DriveService.SignSolanaTransactionAsync"/> with a real ARC-76 Solana account (the
    /// repository is mocked to hand it back) - the signed bytes are deserialized and the ed25519 signature is
    /// verified against the signer's public key, and the message is checked to contain exactly one System Program
    /// transfer paying the requested amount (nothing else can be smuggled in).
    /// </summary>
    [TestFixture]
    public class SolanaDriveServiceTests
    {
        private const string TestEmail = "alice@example.com";
        private const string TestProvider = "Fake";
        private const string Mnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
        private const string Receiver = "FoBNuzYQ4gpiP3jmevoiRVp3EnqBeXkB2HJ1LksSwYXJ";
        private const string Blockhash = "EkSnNWid2cvwEVnVx9aBqawnmiCNiDgp3gUdkDPTKN1N";

        private Mock<ICloudAccountRepository> _repository = null!;
        private DriveService _service = null!;
        private Account _account = null!;

        [SetUp]
        public void SetUp()
        {
            _account = SolanaArc76.GetEmailAccount(TestEmail, Mnemonic, 0);
            _repository = new Mock<ICloudAccountRepository>();
            _repository
                .Setup(r => r.LoadSolanaAccountAsync(TestEmail, It.IsAny<int>(), TestProvider, It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync(_account);
            _service = new DriveService(_repository.Object, new Mock<ILogger<DriveService>>().Object);
        }

        private static SolanaUnsignedTransfer Transfer(ulong lamports = 1_234_567, string recipient = Receiver, string blockhash = Blockhash) =>
            new() { Recipient = recipient, Lamports = lamports, RecentBlockhash = blockhash };

        [Test]
        public async Task SignSolanaTransactionAsync_ProducesSignedSingleTransferPaidByTheSigner()
        {
            var signed = await _service.SignSolanaTransactionAsync(TestEmail, Transfer(), TestProvider);

            var tx = Transaction.Deserialize(signed);
            Assert.That(tx.FeePayer.Key, Is.EqualTo(_account.PublicKey.Key));
            Assert.That(tx.RecentBlockHash, Is.EqualTo(Blockhash));
            Assert.That(tx.Signatures, Has.Count.EqualTo(1));
            Assert.That(_account.PublicKey.Verify(tx.CompileMessage(), tx.Signatures[0].Signature), Is.True);

            // The spending limit counts SolanaUnsignedTransfer.BaseFeeLamports as the network fee: that is only true while the
            // message has exactly one signature (5,000 lamports each) and no compute-budget / priority-fee instruction.
            // If this ever changes, BaseFeeLamports (and BiatecMCP's advisory copy) must change with it.
            Assert.That((ulong)tx.Signatures.Count * 5000UL, Is.EqualTo(SolanaUnsignedTransfer.BaseFeeLamports));
            Assert.That(tx.Instructions, Has.Count.EqualTo(1));

            // Exactly one instruction: System Program transfer(sender -> receiver, lamports) - nothing else rides along.
            Assert.That(tx.Instructions, Has.Count.EqualTo(1));
            var instruction = tx.Instructions[0];
            Assert.That(new PublicKey(instruction.ProgramId).Key, Is.EqualTo("11111111111111111111111111111111"));
            Assert.That(instruction.Keys.Select(k => k.PublicKey), Is.EqualTo(new[] { _account.PublicKey.Key, Receiver }));
            // System Program Transfer layout: u32 LE discriminator (2) followed by u64 LE lamports.
            Assert.That(BitConverter.ToUInt32(instruction.Data, 0), Is.EqualTo(2u));
            Assert.That(BitConverter.ToUInt64(instruction.Data, 4), Is.EqualTo(1_234_567UL));
        }

        [Test]
        public async Task SignSolanaTransactionAsync_ForwardsSeedAddressAndSlotToRepository()
        {
            await _service.SignSolanaTransactionAsync(TestEmail, Transfer(), TestProvider, "access-token", "SEED-ADDR", 4);

            _repository.Verify(r => r.LoadSolanaAccountAsync(TestEmail, 4, TestProvider, "access-token", "SEED-ADDR"), Times.Once);
        }

        [Test]
        public void SignSolanaTransactionAsync_ZeroLamports_ThrowsFormatExceptionWithoutLoadingTheKey()
        {
            Assert.That(async () => await _service.SignSolanaTransactionAsync(TestEmail, Transfer(lamports: 0), TestProvider), Throws.TypeOf<FormatException>());
            _repository.Verify(r => r.LoadSolanaAccountAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
        }

        [Test]
        public void SignSolanaTransactionAsync_NullTransfer_ThrowsArgumentException()
        {
            Assert.That(async () => await _service.SignSolanaTransactionAsync(TestEmail, null!, TestProvider), Throws.ArgumentException);
        }

        [Test]
        public void SignSolanaTransactionAsync_EmptyEmail_ThrowsArgumentException()
        {
            Assert.That(async () => await _service.SignSolanaTransactionAsync("", Transfer(), TestProvider), Throws.ArgumentException);
        }

        [TestCase("not-a-key")]
        [TestCase("")]
        [TestCase("0x13f022d72158410433cbd66f5dd8bf6d2d0999c1")]
        [TestCase("4nrWTvCH3vGghq2FXd22mifh3rKrKAXHw8xWJxt6tZ4")] // 31 bytes
        public void SignSolanaTransactionAsync_InvalidRecipient_ThrowsFormatExceptionWithoutLoadingTheKey(string recipient)
        {
            Assert.That(async () => await _service.SignSolanaTransactionAsync(TestEmail, Transfer(recipient: recipient), TestProvider), Throws.TypeOf<FormatException>());
            _repository.Verify(r => r.LoadSolanaAccountAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
        }

        [TestCase("")]
        [TestCase("notablockhash!")]
        [TestCase("4nrWTvCH3vGghq2FXd22mifh3rKrKAXHw8xWJxt6tZ4")]
        public void SignSolanaTransactionAsync_InvalidBlockhash_ThrowsFormatException(string blockhash)
        {
            Assert.That(async () => await _service.SignSolanaTransactionAsync(TestEmail, Transfer(blockhash: blockhash), TestProvider), Throws.TypeOf<FormatException>());
        }

        [Test]
        public async Task SignSolanaTransactionAsync_TransferToSelf_IsAllowedAndSigned()
        {
            var signed = await _service.SignSolanaTransactionAsync(TestEmail, Transfer(recipient: _account.PublicKey.Key), TestProvider);

            Assert.That(Transaction.Deserialize(signed).Signatures, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task SignSolanaTransactionAsync_LargeAmount_RoundTripsLosslessly()
        {
            var signed = await _service.SignSolanaTransactionAsync(TestEmail, Transfer(lamports: 9_000_000_000_000_000_000UL), TestProvider);

            var data = Transaction.Deserialize(signed).Instructions[0].Data;
            Assert.That(BitConverter.ToUInt64(data, 4), Is.EqualTo(9_000_000_000_000_000_000UL));
        }

        [Test]
        public void SignSolanaTransactionAsync_LamportsAboveTheCap_ThrowsFormatExceptionWithoutLoadingTheKey()
        {
            Assert.That(async () => await _service.SignSolanaTransactionAsync(TestEmail, Transfer(lamports: SolanaUnsignedTransfer.MaxLamports + 1), TestProvider), Throws.TypeOf<FormatException>());
            _repository.Verify(r => r.LoadSolanaAccountAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
        }

        [Test]
        public async Task SignSolanaTransactionAsync_LamportsAtTheCap_IsAccepted()
        {
            var signed = await _service.SignSolanaTransactionAsync(TestEmail, Transfer(lamports: SolanaUnsignedTransfer.MaxLamports), TestProvider);

            Assert.That(BitConverter.ToUInt64(Solnet.Rpc.Models.Transaction.Deserialize(signed).Instructions[0].Data, 4), Is.EqualTo(SolanaUnsignedTransfer.MaxLamports));
        }

        [Test]
        public void Validate_BadRecipient_NamesTheRecipientField()
        {
            var ex = Assert.Throws<FormatException>(() => Transfer(recipient: "not-a-key").Validate())!;

            Assert.That(ex.Message, Does.Contain("recipient").And.Not.Contain("recentBlockhash"));
        }

        [Test]
        public void Validate_BadBlockhash_NamesTheBlockhashFieldNotTheRecipient()
        {
            var ex = Assert.Throws<FormatException>(() => Transfer(blockhash: "garbled!").Validate())!;

            Assert.That(ex.Message, Does.Contain("recentBlockhash"));
            Assert.That(ex.Message, Does.Not.Contain("recipient"));
        }

        [TestCase("")]
        [TestCase("   ")]
        public void Validate_MissingBlockhash_SaysItIsRequired(string blockhash)
        {
            var ex = Assert.Throws<FormatException>(() => Transfer(blockhash: blockhash).Validate())!;

            Assert.That(ex.Message, Does.Contain("recentBlockhash is required"));
        }
    }
}
