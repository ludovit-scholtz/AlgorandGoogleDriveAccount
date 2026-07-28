using BiatecMCP.Helper;

namespace BiatecMCPTests
{
    [TestFixture]
    public class TransferPolicyTests
    {
        [TestFixture]
        public class ExceedsMaxAmountTests
        {
            [Test]
            public void MaxAmountZero_Unbounded_NeverExceeds()
            {
                Assert.That(TransferPolicy.ExceedsMaxAmount(ulong.MaxValue, 0), Is.False);
            }

            [Test]
            public void AmountUnderLimit_DoesNotExceed()
            {
                Assert.That(TransferPolicy.ExceedsMaxAmount(100, 200), Is.False);
            }

            [Test]
            public void AmountEqualToLimit_DoesNotExceed()
            {
                Assert.That(TransferPolicy.ExceedsMaxAmount(200, 200), Is.False);
            }

            [Test]
            public void AmountOverLimit_Exceeds()
            {
                Assert.That(TransferPolicy.ExceedsMaxAmount(201, 200), Is.True);
            }
        }

        [TestFixture]
        public class IsReceiverAllowedTests
        {
            [Test]
            public void NoAllowlistConfigured_AnyReceiverAllowed()
            {
                Assert.That(TransferPolicy.IsReceiverAllowed("SOMEADDRESS", new List<string>()), Is.True);
            }

            [Test]
            public void NullAllowlist_AnyReceiverAllowed()
            {
                Assert.That(TransferPolicy.IsReceiverAllowed("SOMEADDRESS", null), Is.True);
            }

            [Test]
            public void EmptyReceiver_SelfTransfer_AlwaysAllowed()
            {
                Assert.That(TransferPolicy.IsReceiverAllowed("", new List<string> { "OTHERADDRESS" }), Is.True);
            }

            [Test]
            public void AllowlistConfigured_ReceiverOnList_Allowed()
            {
                Assert.That(TransferPolicy.IsReceiverAllowed("ADDR1", new List<string> { "ADDR1", "ADDR2" }), Is.True);
            }

            [Test]
            public void AllowlistConfigured_ReceiverOnList_CaseInsensitive_Allowed()
            {
                Assert.That(TransferPolicy.IsReceiverAllowed("addr1", new List<string> { "ADDR1" }), Is.True);
            }

            [Test]
            public void AllowlistConfigured_ReceiverNotOnList_Rejected()
            {
                Assert.That(TransferPolicy.IsReceiverAllowed("ADDR3", new List<string> { "ADDR1", "ADDR2" }), Is.False);
            }
        }
    }
}
