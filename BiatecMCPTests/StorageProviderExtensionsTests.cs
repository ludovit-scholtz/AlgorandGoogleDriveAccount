using BiatecSelfCustodyCore.Model;

namespace BiatecMCPTests
{
    [TestFixture]
    public class StorageProviderExtensionsTests
    {
        [TestCase("Google", StorageProvider.Google)]
        [TestCase("google", StorageProvider.Google)]
        [TestCase("GOOGLE", StorageProvider.Google)]
        [TestCase("Microsoft", StorageProvider.Microsoft)]
        [TestCase("microsoft", StorageProvider.Microsoft)]
        [TestCase("MICROSOFT", StorageProvider.Microsoft)]
        public void Parse_RecognizedValue_ReturnsMatchingProvider(string value, StorageProvider expected)
        {
            Assert.That(StorageProviderExtensions.Parse(value), Is.EqualTo(expected));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("onedrive")]
        [TestCase("not-a-provider")]
        public void Parse_UnrecognizedOrMissingValue_DefaultsToGoogle(string? value)
        {
            // Sessions paired before Microsoft support existed have no Provider recorded at all -
            // this default is what keeps them resolving to the only backend they could have used.
            Assert.That(StorageProviderExtensions.Parse(value), Is.EqualTo(StorageProvider.Google));
        }
    }
}
