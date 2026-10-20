using System.Reflection;
using NUnit.Framework;

namespace BiatecMCPTests
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
    }
}
