using System.Reflection;
using NUnit.Framework;

namespace BiatecOIDCTests
{
    /// <summary>
    /// Guards the Algorand5 upgrade: ARC76Account.Algorand and BiatecRouterConnector still depend on Algorand4
    /// transitively, and both packages ship an Algorand.dll, so a build-config regression (see the Algorand4
    /// ExcludeAssets reference in Directory.Build.props) would silently run on the old SDK with every test green.
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
