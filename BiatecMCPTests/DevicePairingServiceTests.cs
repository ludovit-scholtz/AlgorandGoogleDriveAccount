using System.Text;
using System.Text.Json;
using BiatecMCP.BusinessLogic;
using BiatecMCP.Model;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Moq;

namespace BiatecMCPTests
{
    [TestFixture]
    public class DevicePairingServiceTests
    {
        private Mock<IDistributedCache> _mockCache = null!;
        private Mock<ILogger<DevicePairingService>> _mockLogger = null!;
        private DevicePairingService _service = null!;

        [SetUp]
        public void Setup()
        {
            _mockCache = new Mock<IDistributedCache>();
            _mockLogger = new Mock<ILogger<DevicePairingService>>();
            _service = new DevicePairingService(_mockCache.Object, _mockLogger.Object);
        }

        [TestFixture]
        public class InitiatePairingAsyncTests : DevicePairingServiceTests
        {
            [Test]
            public async Task InitiatePairingAsync_ValidParameters_ReturnsExpectedKey()
            {
                // Arrange
                var sessionId = "test-session-123";
                var deviceName = "Test Device";
                var expectedKey = $"temp_session:{sessionId}";

                // Act
                var result = await _service.InitiatePairingAsync(sessionId, deviceName);

                // Assert
                Assert.That(result, Is.EqualTo(expectedKey));
                _mockCache.Verify(c => c.SetAsync(
                    expectedKey,
                    It.IsAny<byte[]>(),
                    It.IsAny<DistributedCacheEntryOptions>(),
                    default), Times.Once);
            }

            [Test]
            public async Task InitiatePairingAsync_ValidParameters_SetsCorrectCacheExpiration()
            {
                // Arrange
                var sessionId = "test-session-123";
                var deviceName = "Test Device";
                DistributedCacheEntryOptions? capturedOptions = null;

                _mockCache.Setup(c => c.SetAsync(
                    It.IsAny<string>(),
                    It.IsAny<byte[]>(),
                    It.IsAny<DistributedCacheEntryOptions>(),
                    default))
                    .Callback<string, byte[], DistributedCacheEntryOptions, CancellationToken>(
                        (key, value, options, token) => capturedOptions = options);

                // Act
                await _service.InitiatePairingAsync(sessionId, deviceName);

                // Assert
                Assert.That(capturedOptions, Is.Not.Null);
                Assert.That(capturedOptions!.AbsoluteExpirationRelativeToNow, Is.EqualTo(TimeSpan.FromMinutes(5)));
            }

            [Test]
            public async Task InitiatePairingAsync_ValidParameters_StoresCorrectSessionData()
            {
                // Arrange
                var sessionId = "test-session-123";
                var deviceName = "Test Device";
                byte[]? capturedBytes = null;

                _mockCache.Setup(c => c.SetAsync(
                    It.IsAny<string>(),
                    It.IsAny<byte[]>(),
                    It.IsAny<DistributedCacheEntryOptions>(),
                    default))
                    .Callback<string, byte[], DistributedCacheEntryOptions, CancellationToken>(
                        (key, value, options, token) => capturedBytes = value);

                // Act
                await _service.InitiatePairingAsync(sessionId, deviceName);

                // Assert
                Assert.That(capturedBytes, Is.Not.Null);
                var capturedJson = Encoding.UTF8.GetString(capturedBytes!);
                var sessionData = JsonSerializer.Deserialize<JsonElement>(capturedJson);
                Assert.That(sessionData.GetProperty("SessionId").GetString(), Is.EqualTo(sessionId));
                Assert.That(sessionData.GetProperty("DeviceName").GetString(), Is.EqualTo(deviceName));
                Assert.That(sessionData.TryGetProperty("InitiatedAt", out _), Is.True);
            }

            [TestCase("")]
            [TestCase("   ")]
            public void InitiatePairingAsync_InvalidSessionId_ThrowsArgumentException(string sessionId)
            {
                // Arrange & Act & Assert
                var ex = Assert.ThrowsAsync<ArgumentException>(() =>
                    _service.InitiatePairingAsync(sessionId, "Test Device"));
                Assert.That(ex!.ParamName, Is.EqualTo("sessionId"));
                Assert.That(ex.Message, Does.Contain("Session ID is required for device pairing"));
            }

            [Test]
            public void InitiatePairingAsync_NullSessionId_ThrowsArgumentException()
            {
                // Arrange & Act & Assert
                var ex = Assert.ThrowsAsync<ArgumentException>(() =>
                    _service.InitiatePairingAsync(null!, "Test Device"));
                Assert.That(ex!.ParamName, Is.EqualTo("sessionId"));
                Assert.That(ex.Message, Does.Contain("Session ID is required for device pairing"));
            }

            [Test]
            public async Task InitiatePairingAsync_NullDeviceName_UsesDeviceNameAsProvided()
            {
                // Arrange
                var sessionId = "test-session-123";
                string? deviceName = null;
                byte[]? capturedBytes = null;

                _mockCache.Setup(c => c.SetAsync(
                    It.IsAny<string>(),
                    It.IsAny<byte[]>(),
                    It.IsAny<DistributedCacheEntryOptions>(),
                    default))
                    .Callback<string, byte[], DistributedCacheEntryOptions, CancellationToken>(
                        (key, value, options, token) => capturedBytes = value);

                // Act
                await _service.InitiatePairingAsync(sessionId, deviceName!);

                // Assert
                Assert.That(capturedBytes, Is.Not.Null);
                var capturedJson = Encoding.UTF8.GetString(capturedBytes!);
                var sessionData = JsonSerializer.Deserialize<JsonElement>(capturedJson);
                Assert.That(sessionData.GetProperty("DeviceName").ValueKind, Is.EqualTo(JsonValueKind.Null));
            }
        }

        [TestFixture]
        public class ProcessPairingCallbackAsyncTests : DevicePairingServiceTests
        {
            [Test]
            public async Task ProcessPairingCallbackAsync_ValidData_ReturnsSuccessResponse()
            {
                // Arrange
                var sessionId = "test-session-123";
                var email = "test@example.com";
                var accessToken = "access-token-123";
                var refreshToken = "refresh-token-123";
                var deviceName = "Test Device";

                var tempSessionData = new
                {
                    SessionId = sessionId,
                    DeviceName = deviceName,
                    InitiatedAt = DateTime.UtcNow
                };

                var tempKey = $"temp_session:{sessionId}";
                var cacheKey = $"device_session:{sessionId}";

                _mockCache.Setup(c => c.GetAsync(tempKey, default))
                    .ReturnsAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tempSessionData)));

                // Act
                var result = await _service.ProcessPairingCallbackAsync(sessionId, email, accessToken, refreshToken, "Google");

                // Assert
                Assert.That(result.Success, Is.True);
                Assert.That(result.Message, Is.EqualTo("Device paired successfully"));
                Assert.That(result.SessionId, Is.EqualTo(sessionId));

                // Verify cache interactions
                _mockCache.Verify(c => c.SetAsync(
                    cacheKey,
                    It.IsAny<byte[]>(),
                    It.IsAny<DistributedCacheEntryOptions>(),
                    default), Times.Once);
                _mockCache.Verify(c => c.RemoveAsync(tempKey, default), Times.Once);
            }

            [Test]
            public async Task ProcessPairingCallbackAsync_ValidData_StoresCorrectDeviceInfo()
            {
                // Arrange
                var sessionId = "test-session-123";
                var email = "test@example.com";
                var accessToken = "access-token-123";
                var refreshToken = "refresh-token-123";
                var deviceName = "Test Device";

                var tempSessionData = new
                {
                    SessionId = sessionId,
                    DeviceName = deviceName,
                    InitiatedAt = DateTime.UtcNow
                };

                var tempKey = $"temp_session:{sessionId}";
                byte[]? capturedDeviceInfoBytes = null;

                _mockCache.Setup(c => c.GetAsync(tempKey, default))
                    .ReturnsAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tempSessionData)));

                _mockCache.Setup(c => c.SetAsync(
                    It.IsAny<string>(),
                    It.IsAny<byte[]>(),
                    It.IsAny<DistributedCacheEntryOptions>(),
                    default))
                    .Callback<string, byte[], DistributedCacheEntryOptions, CancellationToken>(
                        (key, value, options, token) => capturedDeviceInfoBytes = value);

                // Act
                await _service.ProcessPairingCallbackAsync(sessionId, email, accessToken, refreshToken, "Google");

                // Assert
                Assert.That(capturedDeviceInfoBytes, Is.Not.Null);
                var capturedDeviceInfoJson = Encoding.UTF8.GetString(capturedDeviceInfoBytes!);
                var deviceInfo = JsonSerializer.Deserialize<PairedDeviceInfo>(capturedDeviceInfoJson)!;
                Assert.That(deviceInfo.AccessToken, Is.EqualTo(accessToken));
                Assert.That(deviceInfo.RefreshToken, Is.EqualTo(refreshToken));
                Assert.That(deviceInfo.Email, Is.EqualTo(email));
                Assert.That(deviceInfo.DeviceName, Is.EqualTo(deviceName));
                Assert.That(deviceInfo.ExpiresAt, Is.GreaterThan(DateTime.UtcNow));
            }

            [TestCase("")]
            public async Task ProcessPairingCallbackAsync_InvalidSessionId_ReturnsFailureResponse(string sessionId)
            {
                // Act
                var result = await _service.ProcessPairingCallbackAsync(sessionId, "test@example.com", "token", "refresh", "Google");

                // Assert
                Assert.That(result.Success, Is.False);
                Assert.That(result.Message, Is.EqualTo("Session ID is required"));
            }

            [Test]
            public async Task ProcessPairingCallbackAsync_NullSessionId_ReturnsFailureResponse()
            {
                // Act
                var result = await _service.ProcessPairingCallbackAsync(null!, "test@example.com", "token", "refresh", "Google");

                // Assert
                Assert.That(result.Success, Is.False);
                Assert.That(result.Message, Is.EqualTo("Session ID is required"));
            }

            [Test]
            public async Task ProcessPairingCallbackAsync_SessionNotFound_ReturnsFailureResponse()
            {
                // Arrange
                var sessionId = "test-session-123";
                var tempKey = $"temp_session:{sessionId}";

                _mockCache.Setup(c => c.GetAsync(tempKey, default))
                    .ReturnsAsync((byte[]?)null);

                // Act
                var result = await _service.ProcessPairingCallbackAsync(sessionId, "test@example.com", "token", "refresh", "Google");

                // Assert
                Assert.That(result.Success, Is.False);
                Assert.That(result.Message, Is.EqualTo("Session not found or expired. Please initiate pairing again."));
            }

            [TestCase("")]
            public async Task ProcessPairingCallbackAsync_InvalidEmail_ReturnsFailureResponse(string email)
            {
                // Arrange
                var sessionId = "test-session-123";
                var tempSessionData = new { SessionId = sessionId, DeviceName = "Test Device", InitiatedAt = DateTime.UtcNow };
                var tempKey = $"temp_session:{sessionId}";

                _mockCache.Setup(c => c.GetAsync(tempKey, default))
                    .ReturnsAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tempSessionData)));

                // Act
                var result = await _service.ProcessPairingCallbackAsync(sessionId, email, "token", "refresh", "Google");

                // Assert
                Assert.That(result.Success, Is.False);
                Assert.That(result.Message, Is.EqualTo("Email not found in claims. Authentication failed."));
            }

            [Test]
            public async Task ProcessPairingCallbackAsync_NullEmail_ReturnsFailureResponse()
            {
                // Arrange
                var sessionId = "test-session-123";
                var tempSessionData = new { SessionId = sessionId, DeviceName = "Test Device", InitiatedAt = DateTime.UtcNow };
                var tempKey = $"temp_session:{sessionId}";

                _mockCache.Setup(c => c.GetAsync(tempKey, default))
                    .ReturnsAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tempSessionData)));

                // Act
                var result = await _service.ProcessPairingCallbackAsync(sessionId, null!, "token", "refresh", "Google");

                // Assert
                Assert.That(result.Success, Is.False);
                Assert.That(result.Message, Is.EqualTo("Email not found in claims. Authentication failed."));
            }

            [TestCase("")]
            public async Task ProcessPairingCallbackAsync_InvalidAccessToken_ReturnsFailureResponse(string accessToken)
            {
                // Arrange
                var sessionId = "test-session-123";
                var tempSessionData = new { SessionId = sessionId, DeviceName = "Test Device", InitiatedAt = DateTime.UtcNow };
                var tempKey = $"temp_session:{sessionId}";

                _mockCache.Setup(c => c.GetAsync(tempKey, default))
                    .ReturnsAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tempSessionData)));

                // Act
                var result = await _service.ProcessPairingCallbackAsync(sessionId, "test@example.com", accessToken, "refresh", "Google");

                // Assert
                Assert.That(result.Success, Is.False);
                Assert.That(result.Message, Is.EqualTo("No access token found. Authentication failed."));
            }

            [Test]
            public async Task ProcessPairingCallbackAsync_NullAccessToken_ReturnsFailureResponse()
            {
                // Arrange
                var sessionId = "test-session-123";
                var tempSessionData = new { SessionId = sessionId, DeviceName = "Test Device", InitiatedAt = DateTime.UtcNow };
                var tempKey = $"temp_session:{sessionId}";

                _mockCache.Setup(c => c.GetAsync(tempKey, default))
                    .ReturnsAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tempSessionData)));

                // Act
                var result = await _service.ProcessPairingCallbackAsync(sessionId, "test@example.com", null!, "refresh", "Google");

                // Assert
                Assert.That(result.Success, Is.False);
                Assert.That(result.Message, Is.EqualTo("No access token found. Authentication failed."));
            }

            [Test]
            public async Task ProcessPairingCallbackAsync_CacheException_ReturnsFailureResponse()
            {
                // Arrange
                var sessionId = "test-session-123";
                var tempKey = $"temp_session:{sessionId}";

                _mockCache.Setup(c => c.GetAsync(tempKey, default))
                    .ThrowsAsync(new InvalidOperationException("Cache error"));

                // Act
                var result = await _service.ProcessPairingCallbackAsync(sessionId, "test@example.com", "token", "refresh", "Google");

                // Assert
                Assert.That(result.Success, Is.False);
                Assert.That(result.Message, Is.EqualTo("An error occurred while pairing the device"));
            }
        }

        [TestFixture]
        public class GetDeviceAccessTokenAsyncTests : DevicePairingServiceTests
        {
            [Test]
            public async Task GetDeviceAccessTokenAsync_ValidSessionId_ReturnsAccessToken()
            {
                // Arrange
                var sessionId = "test-session-123";
                var accessToken = "access-token-123";
                var deviceInfo = new PairedDeviceInfo
                {
                    AccessToken = accessToken,
                    RefreshToken = "refresh-token",
                    Email = "test@example.com",
                    DeviceName = "Test Device",
                    PairedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddDays(1)
                };

                var cacheKey = $"device_session:{sessionId}";
                _mockCache.Setup(c => c.GetAsync(cacheKey, default))
                    .ReturnsAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(deviceInfo)));

                // Act
                var result = await _service.GetDeviceAccessTokenAsync(sessionId);

                // Assert
                Assert.That(result, Is.EqualTo(accessToken));
            }

            [Test]
            public async Task GetDeviceAccessTokenAsync_SessionNotFound_ReturnsNull()
            {
                // Arrange
                var sessionId = "test-session-123";
                var cacheKey = $"device_session:{sessionId}";

                _mockCache.Setup(c => c.GetAsync(cacheKey, default))
                    .ReturnsAsync((byte[]?)null);

                // Act
                var result = await _service.GetDeviceAccessTokenAsync(sessionId);

                // Assert
                Assert.That(result, Is.Null);
            }

            [Test]
            public async Task GetDeviceAccessTokenAsync_ExpiredToken_RemovesFromCacheAndReturnsNull()
            {
                // Arrange
                var sessionId = "test-session-123";
                var deviceInfo = new PairedDeviceInfo
                {
                    AccessToken = "access-token-123",
                    RefreshToken = "refresh-token",
                    Email = "test@example.com",
                    DeviceName = "Test Device",
                    PairedAt = DateTime.UtcNow.AddDays(-2),
                    ExpiresAt = DateTime.UtcNow.AddDays(-1) // Expired
                };

                var cacheKey = $"device_session:{sessionId}";
                _mockCache.Setup(c => c.GetAsync(cacheKey, default))
                    .ReturnsAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(deviceInfo)));

                // Act
                var result = await _service.GetDeviceAccessTokenAsync(sessionId);

                // Assert
                Assert.That(result, Is.Null);
                _mockCache.Verify(c => c.RemoveAsync(cacheKey, default), Times.Once);
            }

            [TestCase("")]
            [TestCase("   ")]
            public void GetDeviceAccessTokenAsync_InvalidSessionId_ThrowsArgumentException(string sessionId)
            {
                // Act & Assert
                var ex = Assert.ThrowsAsync<ArgumentException>(() =>
                    _service.GetDeviceAccessTokenAsync(sessionId));
                Assert.That(ex!.ParamName, Is.EqualTo("sessionId"));
            }

            [Test]
            public void GetDeviceAccessTokenAsync_NullSessionId_ThrowsArgumentException()
            {
                // Act & Assert
                var ex = Assert.ThrowsAsync<ArgumentException>(() =>
                    _service.GetDeviceAccessTokenAsync(null!));
                Assert.That(ex!.ParamName, Is.EqualTo("sessionId"));
            }

            [Test]
            public void GetDeviceAccessTokenAsync_InvalidJson_ThrowsJsonException()
            {
                // Arrange
                var sessionId = "test-session-123";
                var cacheKey = $"device_session:{sessionId}";

                _mockCache.Setup(c => c.GetAsync(cacheKey, default))
                    .ReturnsAsync(Encoding.UTF8.GetBytes("invalid json"));

                // Act & Assert
                Assert.ThrowsAsync<JsonException>(() =>
                    _service.GetDeviceAccessTokenAsync(sessionId));
            }
        }

        [TestFixture]
        public class GetDeviceInfoAsyncTests : DevicePairingServiceTests
        {
            [Test]
            public async Task GetDeviceInfoAsync_ValidSessionId_ReturnsRedactedDeviceInfo()
            {
                // Arrange
                var sessionId = "test-session-123";
                var deviceInfo = new PairedDeviceInfo
                {
                    AccessToken = "access-token-123",
                    RefreshToken = "refresh-token-123",
                    Email = "test@example.com",
                    DeviceName = "Test Device",
                    PairedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddDays(1)
                };

                var cacheKey = $"device_session:{sessionId}";
                _mockCache.Setup(c => c.GetAsync(cacheKey, default))
                    .ReturnsAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(deviceInfo)));

                // Act
                var result = await _service.GetDeviceInfoAsync(sessionId);

                // Assert
                Assert.That(result, Is.Not.Null);
                Assert.That(result!.AccessToken, Is.EqualTo("***"));
                Assert.That(result.RefreshToken, Is.EqualTo("***"));
                Assert.That(result.Email, Is.EqualTo(deviceInfo.Email));
                Assert.That(result.DeviceName, Is.EqualTo(deviceInfo.DeviceName));
                Assert.That(result.PairedAt, Is.EqualTo(deviceInfo.PairedAt));
                Assert.That(result.ExpiresAt, Is.EqualTo(deviceInfo.ExpiresAt));
            }

            [Test]
            public async Task GetDeviceInfoAsync_SessionNotFound_ReturnsNull()
            {
                // Arrange
                var sessionId = "test-session-123";
                var cacheKey = $"device_session:{sessionId}";

                _mockCache.Setup(c => c.GetAsync(cacheKey, default))
                    .ReturnsAsync((byte[]?)null);

                // Act
                var result = await _service.GetDeviceInfoAsync(sessionId);

                // Assert
                Assert.That(result, Is.Null);
            }

            [Test]
            public async Task GetDeviceInfoAsync_ExpiredToken_RemovesFromCacheAndReturnsNull()
            {
                // Arrange
                var sessionId = "test-session-123";
                var deviceInfo = new PairedDeviceInfo
                {
                    AccessToken = "access-token-123",
                    RefreshToken = "refresh-token",
                    Email = "test@example.com",
                    DeviceName = "Test Device",
                    PairedAt = DateTime.UtcNow.AddDays(-2),
                    ExpiresAt = DateTime.UtcNow.AddDays(-1) // Expired
                };

                var cacheKey = $"device_session:{sessionId}";
                _mockCache.Setup(c => c.GetAsync(cacheKey, default))
                    .ReturnsAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(deviceInfo)));

                // Act
                var result = await _service.GetDeviceInfoAsync(sessionId);

                // Assert
                Assert.That(result, Is.Null);
                _mockCache.Verify(c => c.RemoveAsync(cacheKey, default), Times.Once);
            }

            [TestCase("")]
            [TestCase("   ")]
            public void GetDeviceInfoAsync_InvalidSessionId_ThrowsArgumentException(string sessionId)
            {
                // Act & Assert
                var ex = Assert.ThrowsAsync<ArgumentException>(() =>
                    _service.GetDeviceInfoAsync(sessionId));
                Assert.That(ex!.ParamName, Is.EqualTo("sessionId"));
            }

            [Test]
            public void GetDeviceInfoAsync_NullSessionId_ThrowsArgumentException()
            {
                // Act & Assert
                var ex = Assert.ThrowsAsync<ArgumentException>(() =>
                    _service.GetDeviceInfoAsync(null!));
                Assert.That(ex!.ParamName, Is.EqualTo("sessionId"));
            }
        }

        [TestFixture]
        public class UnpairDeviceAsyncTests : DevicePairingServiceTests
        {
            [Test]
            public async Task UnpairDeviceAsync_ValidSessionId_ReturnsSuccessResponse()
            {
                // Arrange
                var sessionId = "test-session-123";
                var cacheKey = $"device_session:{sessionId}";

                // Act
                var result = await _service.UnpairDeviceAsync(sessionId);

                // Assert
                Assert.That(result.Success, Is.True);
                Assert.That(result.Message, Is.EqualTo("Device unpaired successfully"));
                Assert.That(result.SessionId, Is.EqualTo(sessionId));
                _mockCache.Verify(c => c.RemoveAsync(cacheKey, default), Times.Once);
            }

            [TestCase("")]
            public async Task UnpairDeviceAsync_InvalidSessionId_ReturnsFailureResponse(string sessionId)
            {
                // Act
                var result = await _service.UnpairDeviceAsync(sessionId);

                // Assert
                Assert.That(result.Success, Is.False);
                Assert.That(result.Message, Is.EqualTo("Session ID is required"));
            }

            [Test]
            public async Task UnpairDeviceAsync_NullSessionId_ReturnsFailureResponse()
            {
                // Act
                var result = await _service.UnpairDeviceAsync(null!);

                // Assert
                Assert.That(result.Success, Is.False);
                Assert.That(result.Message, Is.EqualTo("Session ID is required"));
            }

            [Test]
            public async Task UnpairDeviceAsync_CacheException_ReturnsFailureResponse()
            {
                // Arrange
                var sessionId = "test-session-123";
                var cacheKey = $"device_session:{sessionId}";

                _mockCache.Setup(c => c.RemoveAsync(cacheKey, default))
                    .ThrowsAsync(new InvalidOperationException("Cache error"));

                // Act
                var result = await _service.UnpairDeviceAsync(sessionId);

                // Assert
                Assert.That(result.Success, Is.False);
                Assert.That(result.Message, Is.EqualTo("An error occurred while unpairing the device"));
            }
        }

        [TestFixture]
        public class SetReceiverAllowlistAsyncTests : DevicePairingServiceTests
        {
            private static string BuildPairedDeviceJson(string sessionId) => JsonSerializer.Serialize(new PairedDeviceInfo
            {
                AccessToken = "access-token",
                RefreshToken = "refresh-token",
                Email = "user@example.com",
                DeviceName = "Test Device",
                PairedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddHours(1)
            });

            [Test]
            public async Task SetReceiverAllowlistAsync_PairedSession_StoresAllowlist()
            {
                var sessionId = "test-session-123";
                var cacheKey = $"device_session:{sessionId}";
                var json = BuildPairedDeviceJson(sessionId);
                _mockCache.Setup(c => c.GetAsync(cacheKey, default)).ReturnsAsync(Encoding.UTF8.GetBytes(json));

                byte[]? capturedBytes = null;
                _mockCache.Setup(c => c.SetAsync(cacheKey, It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>(), default))
                    .Callback<string, byte[], DistributedCacheEntryOptions, CancellationToken>((_, value, _, _) => capturedBytes = value)
                    .Returns(Task.CompletedTask);

                var result = await _service.SetReceiverAllowlistAsync(sessionId, new[] { "RECEIVER1ADDRESS", "RECEIVER2ADDRESS" });

                Assert.That(result.Success, Is.True);
                Assert.That(capturedBytes, Is.Not.Null);
                var stored = JsonSerializer.Deserialize<PairedDeviceInfo>(capturedBytes!)!;
                Assert.That(stored.AllowedReceivers, Is.EquivalentTo(new[] { "RECEIVER1ADDRESS", "RECEIVER2ADDRESS" }));
            }

            [Test]
            public async Task SetReceiverAllowlistAsync_EmptyList_ClearsAllowlist()
            {
                var sessionId = "test-session-123";
                var cacheKey = $"device_session:{sessionId}";
                var deviceInfo = new PairedDeviceInfo
                {
                    Email = "user@example.com",
                    ExpiresAt = DateTime.UtcNow.AddHours(1),
                    AllowedReceivers = new List<string> { "OLD-RECEIVER" }
                };
                _mockCache.Setup(c => c.GetAsync(cacheKey, default)).ReturnsAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(deviceInfo)));

                byte[]? capturedBytes = null;
                _mockCache.Setup(c => c.SetAsync(cacheKey, It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>(), default))
                    .Callback<string, byte[], DistributedCacheEntryOptions, CancellationToken>((_, value, _, _) => capturedBytes = value)
                    .Returns(Task.CompletedTask);

                var result = await _service.SetReceiverAllowlistAsync(sessionId, Array.Empty<string>());

                Assert.That(result.Success, Is.True);
                var stored = JsonSerializer.Deserialize<PairedDeviceInfo>(capturedBytes!)!;
                Assert.That(stored.AllowedReceivers, Is.Empty);
            }

            [Test]
            public async Task SetReceiverAllowlistAsync_SessionNotFound_ReturnsFailure()
            {
                var sessionId = "missing-session";
                _mockCache.Setup(c => c.GetAsync($"device_session:{sessionId}", default)).ReturnsAsync((byte[]?)null);

                var result = await _service.SetReceiverAllowlistAsync(sessionId, new[] { "RECEIVER" });

                Assert.That(result.Success, Is.False);
            }

            [Test]
            public async Task SetReceiverAllowlistAsync_EmptySessionId_ReturnsFailure()
            {
                var result = await _service.SetReceiverAllowlistAsync("", new[] { "RECEIVER" });

                Assert.That(result.Success, Is.False);
                Assert.That(result.Message, Is.EqualTo("Session ID is required"));
            }
        }
    }
}
