using BiatecMCP.BusinessLogic;
using BiatecMCP.Controllers;
using BiatecMCP.Model;
using BiatecSelfCustodyCore.BusinessLogic;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;

namespace BiatecMCPTests
{
    /// <summary>
    /// Covers F-01's hardening of <see cref="DevicePairingController"/>: the <c>diagnose</c> troubleshooting
    /// endpoint (which discloses more Drive metadata than the pairing feature needs) must not be reachable
    /// outside Development, and the receiver-allowlist endpoint (F-04) added alongside it.
    /// </summary>
    [TestFixture]
    public class DevicePairingControllerTests
    {
        private Mock<IDevicePairingService> _mockDevicePairingService = null!;
        private Mock<ILogger<DevicePairingController>> _mockLogger = null!;
        private Mock<IHostEnvironment> _mockEnvironment = null!;
        private StorageAccessVerifier _storageAccessVerifier = null!;
        private DevicePairingController _controller = null!;

        [SetUp]
        public void SetUp()
        {
            _mockDevicePairingService = new Mock<IDevicePairingService>();
            _mockLogger = new Mock<ILogger<DevicePairingController>>();
            _mockEnvironment = new Mock<IHostEnvironment>();
            _storageAccessVerifier = new StorageAccessVerifier(new HttpClient(), new Mock<ILogger<StorageAccessVerifier>>().Object);
            _controller = new DevicePairingController(_mockDevicePairingService.Object, _storageAccessVerifier, _mockLogger.Object, _mockEnvironment.Object);
        }

        [TestFixture]
        public class DiagnoseAccountTests : DevicePairingControllerTests
        {
            [Test]
            public async Task DiagnoseAccount_NotDevelopment_ReturnsNotFoundWithoutCallingService()
            {
                _mockEnvironment.Setup(e => e.EnvironmentName).Returns(Environments.Production);

                var result = await _controller.DiagnoseAccount("any-session");

                Assert.That(result.Result, Is.InstanceOf<NotFoundResult>());
                _mockDevicePairingService.Verify(s => s.GetDeviceInfoInternalAsync(It.IsAny<string>()), Times.Never);
            }

            [Test]
            public async Task DiagnoseAccount_Development_SessionNotFound_ReturnsNotFoundObject()
            {
                _mockEnvironment.Setup(e => e.EnvironmentName).Returns(Environments.Development);
                _mockDevicePairingService.Setup(s => s.GetDeviceInfoInternalAsync("missing")).ReturnsAsync((PairedDeviceInfo?)null);

                var result = await _controller.DiagnoseAccount("missing");

                Assert.That(result.Result, Is.InstanceOf<NotFoundObjectResult>());
            }
        }

        [TestFixture]
        public class SetReceiverAllowlistTests : DevicePairingControllerTests
        {
            [Test]
            public async Task SetReceiverAllowlist_ServiceSucceeds_ReturnsOk()
            {
                _mockDevicePairingService
                    .Setup(s => s.SetReceiverAllowlistAsync("session-1", It.IsAny<IReadOnlyCollection<string>>()))
                    .ReturnsAsync(new DevicePairingResponse { Success = true, Message = "Receiver allowlist updated", SessionId = "session-1" });

                var result = await _controller.SetReceiverAllowlist("session-1", new DevicePairingController.ReceiverAllowlistRequest
                {
                    AllowedReceivers = new List<string> { "ADDR1" }
                });

                Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
            }

            [Test]
            public async Task SetReceiverAllowlist_SessionNotFound_ReturnsNotFound()
            {
                _mockDevicePairingService
                    .Setup(s => s.SetReceiverAllowlistAsync("missing", It.IsAny<IReadOnlyCollection<string>>()))
                    .ReturnsAsync(new DevicePairingResponse { Success = false, Message = "Device not found or session expired" });

                var result = await _controller.SetReceiverAllowlist("missing", new DevicePairingController.ReceiverAllowlistRequest());

                Assert.That(result.Result, Is.InstanceOf<NotFoundObjectResult>());
            }
        }
    }
}
