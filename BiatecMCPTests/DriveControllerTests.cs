using BiatecMCP.Controllers;
using BiatecSelfCustodyCore.BusinessLogic;
using BiatecSelfCustodyCore.Providers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace BiatecMCPTests
{
    /// <summary>
    /// Regression coverage for F-08 (open redirect on <c>/api/drive/login</c>/<c>/logout</c>): confirms
    /// <see cref="DriveController"/> only ever honors a local <c>redirectUri</c> and falls back to
    /// <c>/swagger/</c> for anything else, rather than passing an attacker-supplied absolute URI straight
    /// into <see cref="AuthenticationProperties.RedirectUri"/>.
    /// </summary>
    [TestFixture]
    public class DriveControllerTests
    {
        private Mock<IDriveService> _mockDriveService = null!;
        private Mock<ILogger<DriveController>> _mockLogger = null!;
        private DriveController _controller = null!;
        private Mock<IUrlHelper> _mockUrlHelper = null!;

        [SetUp]
        public void SetUp()
        {
            _mockDriveService = new Mock<IDriveService>();
            _mockLogger = new Mock<ILogger<DriveController>>();
            var providerCatalog = new CloudStorageProviderCatalog(new ICloudStorageProvider[] { new FakeCloudStorageProvider() });
            _controller = new DriveController(_mockDriveService.Object, providerCatalog, _mockLogger.Object)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };

            _mockUrlHelper = new Mock<IUrlHelper>();
            _mockUrlHelper.Setup(u => u.Content("~/swagger/")).Returns("/swagger/");
            _controller.Url = _mockUrlHelper.Object;
        }

        private static string? RedirectUriOf(IActionResult result) => result switch
        {
            ChallengeResult challenge => challenge.Properties?.RedirectUri,
            SignOutResult signOut => signOut.Properties?.RedirectUri,
            _ => throw new InvalidOperationException($"Unexpected result type: {result.GetType()}")
        };

        [Test]
        public void Login_AbsoluteAttackerUri_FallsBackToSwagger()
        {
            _mockUrlHelper.Setup(u => u.IsLocalUrl("https://attacker.example/phish")).Returns(false);

            var result = _controller.Login("https://attacker.example/phish");

            Assert.That(RedirectUriOf(result), Is.EqualTo("/swagger/"));
        }

        [Test]
        public void Login_ProtocolRelativeUri_FallsBackToSwagger()
        {
            _mockUrlHelper.Setup(u => u.IsLocalUrl("//attacker.example/phish")).Returns(false);

            var result = _controller.Login("//attacker.example/phish");

            Assert.That(RedirectUriOf(result), Is.EqualTo("/swagger/"));
        }

        [Test]
        public void Login_LocalPath_IsHonored()
        {
            _mockUrlHelper.Setup(u => u.IsLocalUrl("/some/local/path")).Returns(true);

            var result = _controller.Login("/some/local/path");

            Assert.That(RedirectUriOf(result), Is.EqualTo("/some/local/path"));
        }

        [Test]
        public void Login_NoRedirectUriGiven_FallsBackToSwagger()
        {
            var result = _controller.Login(null);

            Assert.That(RedirectUriOf(result), Is.EqualTo("/swagger/"));
        }

        [Test]
        public void Logout_AbsoluteAttackerUri_FallsBackToSwagger()
        {
            _mockUrlHelper.Setup(u => u.IsLocalUrl("https://attacker.example/phish")).Returns(false);

            var result = _controller.Logout("https://attacker.example/phish");

            Assert.That(RedirectUriOf(result), Is.EqualTo("/swagger/"));
        }

        [Test]
        public void Logout_LocalPath_IsHonored()
        {
            _mockUrlHelper.Setup(u => u.IsLocalUrl("/dashboard")).Returns(true);

            var result = _controller.Logout("/dashboard");

            Assert.That(RedirectUriOf(result), Is.EqualTo("/dashboard"));
        }
    }
}
