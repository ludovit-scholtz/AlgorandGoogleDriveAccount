using System.Reflection;
using NUnit.Framework;

namespace BiatecOIDCTests
{
    /// <summary>
    /// Guards the Algorand5 SDK: the retired Algorand4 package ships an assembly with the same name (Algorand.dll), so a
    /// dependency that still pulled it in transitively could silently win in the output with every test green.
    /// </summary>
    [TestFixture]
    public class AlgorandSdkVersionTests
    {
        [Test]
        public void LoadedAlgorandAssembly_IsAlgorand5()
        {
            var assembly = typeof(Algorand.Utils.Utils).Assembly;
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            Assert.That(version, Does.StartWith("5."), "Algorand.dll in the output is not from the Algorand5 package.");
        }

        [Test]
        public void LoadedMessagePackAssembly_IsAtLeastTheSecurityFixedVersion()
        {
            // 3.1.8 carries advisory GHSA-qhrr-8q5h-9q3h. There is no repo-wide pin anymore (Algorand5 5.0.3+ depends on
            // 3.1.12 itself, BiatecOIDC references it explicitly), so this catches a dependency change re-introducing 3.1.8.
            var assembly = typeof(MessagePack.MessagePackSerializer).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            Assert.That(informational, Is.Not.Null.And.Not.Empty, "MessagePack.dll has no informational version.");
            var version = Version.Parse(informational!.Split('+', '-')[0]);

            Assert.That(version, Is.GreaterThanOrEqualTo(new Version(3, 1, 12)));
        }
    }
}
