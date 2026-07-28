using System.Security.Claims;
using BiatecMCP.BusinessLogic;
using BiatecMCP.Model;
using BiatecSelfCustodyCore.BusinessLogic;
using BiatecSelfCustodyCore.Model;
using BiatecSelfCustodyCore.Providers;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace BiatecMCP.Controllers
{
    /// <summary>
    /// Links a session on one device (e.g. an AI assistant's config) to a Google Drive authorization
    /// completed via <c>pair.html</c> on another device (a browser), coordinated through Redis-backed
    /// session state. Also exposes Cross-Account Protection status/reporting and portfolio/tier info
    /// for a paired session.
    /// </summary>
    [ApiController]
    [Route("api/device")]
    public class DevicePairingController : ControllerBase
    {
        private readonly IDevicePairingService _devicePairingService;
        private readonly ICloudStorageProviderCatalog _providerCatalog;
        private readonly ILogger<DevicePairingController> _logger;
        private readonly IHostEnvironment _environment;

        public DevicePairingController(
            IDevicePairingService devicePairingService,
            ICloudStorageProviderCatalog providerCatalog,
            ILogger<DevicePairingController> logger,
            IHostEnvironment environment)
        {
            _devicePairingService = devicePairingService;
            _providerCatalog = providerCatalog;
            _logger = logger;
            _environment = environment;
        }

        /// <summary>Returns the list of registered providers, for <c>pair.html</c>'s picker to render buttons from.</summary>
        [AllowAnonymous]
        [HttpGet("providers")]
        public ActionResult<object> GetProviders()
        {
            return Ok(_providerCatalog.All.Select(p => new { name = p.Name, displayName = p.DisplayName }));
        }

        /// <summary>
        /// Serves the device pairing app page
        /// </summary>
        [AllowAnonymous]
        [HttpGet("app")]
        public IActionResult App()
        {
            return PhysicalFile(
                Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "pair.html"),
                "text/html"
            );
        }

        /// <summary>
        /// Initiates the device pairing process by redirecting to the chosen provider's OAuth login
        /// </summary>
        /// <param name="sessionId">Unique session ID for the device</param>
        /// <param name="deviceName">Optional device name for identification</param>
        /// <param name="requestDriveAccess">Whether to request storage (Drive/OneDrive) access immediately</param>
        /// <param name="idp">Which provider to pair with: <c>"google"</c> (default) or <c>"microsoft"</c></param>
        /// <returns>Redirect to the chosen provider's OAuth login</returns>
        [AllowAnonymous]
        [HttpGet("pair-device")]
        public async Task<IActionResult> PairDevice(string sessionId, string deviceName = "Unknown Device", bool requestDriveAccess = false, string idp = "google")
        {
            try
            {
                await _devicePairingService.InitiatePairingAsync(sessionId, deviceName);

                var redirectUri = Url.Action("PairedDevice", "DevicePairing", new { sessionId }, Request.Scheme);
                var provider = _providerCatalog.Resolve(idp);

                var authProperties = new AuthenticationProperties
                {
                    RedirectUri = redirectUri,
                    Items =
                    {
                        ["sessionId"] = sessionId,
                        ["deviceName"] = deviceName
                    }
                };

                // Support incremental authorization for storage access
                if (requestDriveAccess)
                {
                    authProperties.Items[OpenIdConnectIncrementalAuth.IncrementalScopesKey] = provider.RequiredScope;
                }

                return Challenge(authProperties, provider.Name);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new ProblemDetails
                {
                    Detail = ex.Message
                });
            }
        }

        /// <summary>
        /// Callback endpoint after successful OAuth authentication (Google or Microsoft).
        /// Stores the auth token in Redis with 1-day expiration. Before declaring success, verifies the
        /// token actually has storage-write access (drive.file / Files.ReadWrite.AppFolder) - a user can
        /// decline that specific permission on the consent screen even while completing sign-in - and if
        /// it's missing, sends the browser through one incremental-consent round-trip
        /// (<see cref="RequestStorageAccess"/>) before finishing, so an AI assistant never ends up paired
        /// to a session that can't actually read/write the self-custody account file.
        /// </summary>
        /// <param name="sessionId">Session ID from the pairing request</param>
        /// <param name="retried">Internal guard - set after one incremental-consent round-trip, to avoid looping.</param>
        /// <returns>Device pairing result</returns>
        [Authorize]
        [HttpGet("paired-device")]
        public async Task<ActionResult<DevicePairingResponse>> PairedDevice(string sessionId, bool retried = false)
        {
            try
            {
                // Get user info and tokens
                var email = User.FindFirst(ClaimTypes.Email)?.Value;
                var accessToken = await HttpContext.GetTokenAsync("access_token");
                var refreshToken = await HttpContext.GetTokenAsync("refresh_token");
                var provider = _providerCatalog.Resolve(User.FindFirst(AuthSchemeNames.IdpClaimType)?.Value);

                var result = await _devicePairingService.ProcessPairingCallbackAsync(sessionId, email!, accessToken!, refreshToken, provider.Name);

                if (!result.Success)
                {
                    _logger.LogWarning($"Device pairing failed for session {sessionId}: {result.Message}");
                    return Redirect($"/pair.html?error=pairing_failed&sessionId={sessionId}");
                }

                if (!retried && !await provider.HasWriteAccessAsync(accessToken!))
                {
                    return RequestStorageAccessRedirect(sessionId, provider.Name, retried: true);
                }

                // Redirect to pair.html with success message
                return Redirect($"/pair.html?sessionId={sessionId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error during device pairing callback for session {sessionId}");
                return Redirect($"/pair.html?error=callback_error&sessionId={sessionId}");
            }
        }

        /// <summary>
        /// Requests additional storage-write permissions (Drive/OneDrive) for an already paired device,
        /// using the provider it was originally paired with.
        /// </summary>
        /// <param name="sessionId">Session ID of the paired device</param>
        /// <returns>Redirect to incremental authorization</returns>
        [AllowAnonymous]
        [HttpGet("request-storage-access/{sessionId}")]
        public async Task<IActionResult> RequestStorageAccess(string sessionId)
        {
            try
            {
                var deviceInfo = await _devicePairingService.GetDeviceInfoAsync(sessionId);
                if (deviceInfo == null)
                {
                    return NotFound(new ProblemDetails
                    {
                        Detail = "Device not found or session expired. Please pair the device first."
                    });
                }

                return RequestStorageAccessRedirect(sessionId, deviceInfo.Provider, retried: false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error requesting storage access for session {sessionId}");
                return StatusCode(500, new ProblemDetails
                {
                    Detail = "An error occurred while requesting storage access"
                });
            }
        }

        private ActionResult RequestStorageAccessRedirect(string sessionId, string? providerName, bool retried)
        {
            var provider = _providerCatalog.Resolve(providerName);
            var redirectUri = Url.Action("StorageAccessCallback", "DevicePairing", new { sessionId, retried }, Request.Scheme);

            var authProperties = new AuthenticationProperties
            {
                RedirectUri = redirectUri,
                Items =
                {
                    ["sessionId"] = sessionId,
                    [OpenIdConnectIncrementalAuth.IncrementalScopesKey] = provider.RequiredScope,
                    [OpenIdConnectIncrementalAuth.ForceConsentKey] = "true"
                }
            };

            return Challenge(authProperties, provider.Name);
        }

        /// <summary>
        /// Callback endpoint after incremental storage-access authorization
        /// </summary>
        /// <param name="sessionId">Session ID from the authorization request</param>
        /// <param name="retried">Whether this is already a retry, to avoid looping if consent is declined again.</param>
        /// <returns>Redirect with result</returns>
        [Authorize]
        [HttpGet("storage-access-callback")]
        public async Task<IActionResult> StorageAccessCallback(string sessionId, bool retried = false)
        {
            try
            {
                // Get updated tokens with storage access
                var email = User.FindFirst(ClaimTypes.Email)?.Value;
                var accessToken = await HttpContext.GetTokenAsync("access_token");
                var refreshToken = await HttpContext.GetTokenAsync("refresh_token");
                var provider = _providerCatalog.Resolve(User.FindFirst(AuthSchemeNames.IdpClaimType)?.Value);

                // Update the device info with new tokens that include storage access
                var result = await _devicePairingService.ProcessPairingCallbackAsync(sessionId, email!, accessToken!, refreshToken, provider.Name);

                if (!result.Success)
                {
                    _logger.LogWarning($"Storage access update failed for session {sessionId}: {result.Message}");
                    return Redirect($"/pair.html?error=storage_access_failed&sessionId={sessionId}");
                }

                // Redirect to pair.html with success message
                return Redirect($"/pair.html?drive_access=granted&sessionId={sessionId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error during storage access callback for session {sessionId}");
                return Redirect($"/pair.html?error=storage_callback_error&sessionId={sessionId}");
            }
        }

        /// <summary>
        /// Gets the access token for a paired device using session ID
        /// </summary>
        /// <param name="sessionId">Session ID of the paired device</param>
        /// <returns>Access token if device is paired and not expired</returns>
        [AllowAnonymous]
        [EnableRateLimiting("device-session")]
        [HttpGet("access-token/{sessionId}")]
        public async Task<ActionResult<string>> GetDeviceAccessToken(string sessionId)
        {
            try
            {
                var accessToken = await _devicePairingService.GetDeviceAccessTokenAsync(sessionId);

                if (accessToken == null)
                {
                    return NotFound(new ProblemDetails
                    {
                        Detail = "Device not found or session expired. Please pair the device again."
                    });
                }

                return Ok(accessToken);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new ProblemDetails
                {
                    Detail = ex.Message
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving access token for session ID: {SessionId}", sessionId);
                return StatusCode(500, new ProblemDetails
                {
                    Detail = "An error occurred while retrieving the access token"
                });
            }
        }

        /// <summary>
        /// Gets device information for a paired device
        /// </summary>
        /// <param name="sessionId">Session ID of the paired device</param>
        /// <returns>Device information if device is paired and not expired</returns>
        [AllowAnonymous]
        [EnableRateLimiting("device-session")]
        [HttpGet("info/{sessionId}")]
        public async Task<ActionResult<PairedDeviceInfo>> GetDeviceInfo(string sessionId)
        {
            try
            {
                var deviceInfo = await _devicePairingService.GetDeviceInfoAsync(sessionId);

                if (deviceInfo == null)
                {
                    return NotFound(new ProblemDetails
                    {
                        Detail = "Device not found or session expired"
                    });
                }

                return Ok(deviceInfo);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new ProblemDetails
                {
                    Detail = ex.Message
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error retrieving device info for session ID: {sessionId}");
                return StatusCode(500, new ProblemDetails
                {
                    Detail = "An error occurred while retrieving device information"
                });
            }
        }

        /// <summary>
        /// Unpairs a device by removing it from cache
        /// </summary>
        /// <param name="sessionId">Session ID of the device to unpair</param>
        /// <returns>Result of the unpair operation</returns>
        [AllowAnonymous]
        [EnableRateLimiting("device-session")]
        [HttpDelete("unpair/{sessionId}")]
        public async Task<ActionResult<DevicePairingResponse>> UnpairDevice(string sessionId)
        {
            var result = await _devicePairingService.UnpairDeviceAsync(sessionId);

            if (!result.Success)
            {
                if (result.Message.Contains("required"))
                {
                    return BadRequest(result);
                }
                return StatusCode(500, result);
            }

            return Ok(result);
        }

        /// <summary>
        /// Diagnostic endpoint to help troubleshoot encryption/decryption issues
        /// </summary>
        /// <param name="sessionId">Session ID of the paired device</param>
        /// <returns>Diagnostic information about the account file</returns>
        [AllowAnonymous]
        [EnableRateLimiting("device-session")]
        [HttpGet("diagnose/{sessionId}")]
        public async Task<ActionResult<object>> DiagnoseAccount(string sessionId)
        {
            if (!_environment.IsDevelopment())
            {
                // Leftover troubleshooting tooling that discloses more Drive metadata than the pairing
                // feature needs - keep it available for local debugging only, not on the live attack surface.
                return NotFound();
            }

            try
            {
                var deviceInfo = await _devicePairingService.GetDeviceInfoInternalAsync(sessionId);
                if (deviceInfo == null)
                {
                    return NotFound(new { error = "Device not found or session expired" });
                }

                var credential = GoogleCredential.FromAccessToken(deviceInfo.AccessToken);
                if (credential == null)
                {
                    return BadRequest(new { error = "Invalid access token" });
                }

                var service = new Google.Apis.Drive.v3.DriveService(new BaseClientService.Initializer
                {
                    HttpClientInitializer = credential,
                    ApplicationName = "Biatec",
                });

                // Try to find the folder "Biatec"
                var folderRequest = service.Files.List();
                folderRequest.Q = $"mimeType = 'application/vnd.google-apps.folder' and name = 'Biatec' and trashed = false";
                folderRequest.Fields = "files(id, name)";
                var folderResult = await folderRequest.ExecuteAsync();

                if (!folderResult.Files.Any())
                {
                    return Ok(new
                    {
                        email = deviceInfo.Email,
                        folderFound = false,
                        message = "Biatec folder not found. Account file doesn't exist yet.",
                        suggestedAction = "Try accessing the account through normal authentication first to create the initial encrypted file."
                    });
                }

                var folder = folderResult.Files.First();

                // Check if file exists in folder
                var fileCheckRequest = service.Files.List();
                fileCheckRequest.Q = $"name = 'AVMAccount.dat' and '{folder.Id.Replace("\\", "\\\\").Replace("'", "\\'")}' in parents and trashed = false";
                fileCheckRequest.Fields = "files(id, name, size, createdTime, modifiedTime)";
                var existingFiles = await fileCheckRequest.ExecuteAsync();

                if (!existingFiles.Files.Any())
                {
                    return Ok(new
                    {
                        email = deviceInfo.Email,
                        folderFound = true,
                        fileFound = false,
                        message = "AVMAccount.dat file not found in Biatec folder.",
                        suggestedAction = "Try accessing the account through normal authentication first to create the initial encrypted file."
                    });
                }

                var file = existingFiles.Files.First();

                return Ok(new
                {
                    email = deviceInfo.Email,
                    emailLength = deviceInfo.Email?.Length,
                    folderFound = true,
                    fileFound = true,
                    fileId = file.Id,
                    fileName = file.Name,
                    fileSize = file.Size,
                    createdTime = file.CreatedTimeDateTimeOffset,
                    modifiedTime = file.ModifiedTimeDateTimeOffset,
                    message = "Account file found. The issue might be with email case sensitivity or encryption key derivation.",
                    suggestedActions = new[]
                    {
                        "Verify that the email case matches exactly between device pairing and normal authentication",
                        "Check if the AES key and IV configuration are identical",
                        "Try re-pairing the device to ensure fresh tokens"
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error diagnosing account for session {SessionId}", sessionId);
                return StatusCode(500, new { error = "An error occurred while diagnosing the account." });
            }
        }

        /// <summary>
        /// Check Cross-Account Protection security status for a device session
        /// </summary>
        /// <param name="sessionId">Session ID of the paired device</param>
        /// <returns>Security status information</returns>
        [AllowAnonymous]
        [EnableRateLimiting("device-session")]
        [HttpGet("security-status/{sessionId}")]
        public async Task<ActionResult<object>> GetSecurityStatus(string sessionId)
        {
            try
            {
                var deviceInfo = await _devicePairingService.GetDeviceInfoInternalAsync(sessionId);
                if (deviceInfo == null)
                {
                    return NotFound(new { error = "Device not found or session expired" });
                }

                var capService = HttpContext.RequestServices.GetRequiredService<ICrossAccountProtectionService>();
                var securityStatus = await capService.CheckSecurityStatusAsync(deviceInfo.AccessToken);

                return Ok(new
                {
                    sessionId = sessionId,
                    email = deviceInfo.Email,
                    isSecure = securityStatus.IsSecure,
                    requiresReauth = securityStatus.RequiresReauthentication,
                    warnings = securityStatus.SecurityWarnings,
                    lastCheck = securityStatus.LastSecurityCheck
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking security status for session {SessionId}", sessionId);
                return StatusCode(500, new { error = "An error occurred while checking security status." });
            }
        }

        /// <summary>
        /// Report a security event for Cross-Account Protection
        /// </summary>
        /// <param name="sessionId">Session ID of the device</param>
        /// <param name="request">The security event type and optional details to report</param>
        /// <returns>Result of the security event report</returns>
        [AllowAnonymous]
        [EnableRateLimiting("device-session")]
        [HttpPost("report-security-event/{sessionId}")]
        public async Task<ActionResult<object>> ReportSecurityEvent(string sessionId, [FromBody] SecurityEventRequest request)
        {
            try
            {
                var deviceInfo = await _devicePairingService.GetDeviceInfoAsync(sessionId);
                if (deviceInfo == null)
                {
                    return NotFound(new { error = "Device not found or session expired" });
                }

                var capService = HttpContext.RequestServices.GetRequiredService<ICrossAccountProtectionService>();
                var success = await capService.ReportSecurityEventAsync(
                    deviceInfo.Email ?? sessionId,
                    request.EventType,
                    request.Details);

                return Ok(new
                {
                    success = success,
                    message = success ? "Security event reported successfully" : "Failed to report security event",
                    sessionId = sessionId
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reporting security event for session {SessionId}", sessionId);
                return StatusCode(500, new { error = "An error occurred while reporting the security event." });
            }
        }

        /// <summary>
        /// Test endpoint to verify token validation is working
        /// </summary>
        /// <returns>Token validation test result</returns>
        [AllowAnonymous]
        [HttpGet("test-token-validation")]
        public ActionResult<object> TestTokenValidation()
        {
            try
            {
                // Test with a dummy token to verify our validation logic
                var capService = HttpContext.RequestServices.GetRequiredService<ICrossAccountProtectionService>();

                return Ok(new
                {
                    message = "Token validation service is configured correctly",
                    timestamp = DateTime.UtcNow,
                    endpoint = "Use /api/device/security-status/{sessionId} to check real token status"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing token validation");
                return StatusCode(500, new { error = "An error occurred while testing token validation." });
            }
        }

        /// <summary>
        /// Get portfolio summary and service tier for a device session
        /// </summary>
        /// <param name="sessionId">Session ID of the paired device</param>
        /// <returns>Portfolio information and current service tier</returns>
        [AllowAnonymous]
        [EnableRateLimiting("device-session")]
        [HttpGet("portfolio/{sessionId}")]
        public async Task<ActionResult<object>> GetPortfolioInfo(string sessionId)
        {
            try
            {
                var deviceInfo = await _devicePairingService.GetDeviceInfoInternalAsync(sessionId);
                if (deviceInfo == null)
                {
                    return NotFound(new { error = "Device not found or session expired" });
                }

                var portfolioService = HttpContext.RequestServices.GetRequiredService<IPortfolioValuationService>();
                var portfolioSummary = await portfolioService.GetPortfolioSummaryAsync(deviceInfo.Email!, _providerCatalog.Resolve(deviceInfo.Provider).Name, deviceInfo.AccessToken);

                return Ok(new
                {
                    sessionId = sessionId,
                    email = deviceInfo.Email,
                    portfolio = new
                    {
                        totalValueEur = portfolioSummary.TotalValueEur,
                        currentTier = portfolioSummary.CurrentTier.ToString(),
                        lastUpdated = portfolioSummary.LastUpdated,
                        accountCount = portfolioSummary.AccountCount,
                        algorandBalance = portfolioSummary.AlgorandBalance,
                        assetValue = portfolioSummary.AssetValue
                    },
                    tierBenefits = GetTierBenefits(portfolioSummary.CurrentTier)
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting portfolio info for session {SessionId}", sessionId);
                return StatusCode(500, new { error = "An error occurred while getting portfolio info." });
            }
        }

        /// <summary>
        /// Get Cross-Account Protection configuration status
        /// </summary>
        /// <returns>Cross-Account Protection status information</returns>
        [AllowAnonymous]
        [HttpGet("cap-status")]
        public ActionResult<object> GetCrossAccountProtectionStatus()
        {
            try
            {
                var capConfig = HttpContext.RequestServices.GetRequiredService<IOptionsMonitor<CrossAccountProtectionConfiguration>>();

                return Ok(new
                {
                    crossAccountProtection = new
                    {
                        enabled = capConfig.CurrentValue.Enabled,
                        requireSecurityCheck = capConfig.CurrentValue.RequireSecurityCheck,
                        securityCheckIntervalMinutes = capConfig.CurrentValue.SecurityCheckIntervalMinutes,
                        autoReportEvents = capConfig.CurrentValue.AutoReportEvents,
                        enableGranularConsent = capConfig.CurrentValue.EnableGranularConsent,
                        filterInternalScopes = capConfig.CurrentValue.FilterInternalScopes
                    },
                    message = capConfig.CurrentValue.Enabled
                        ? "Cross-Account Protection is enabled for enhanced security monitoring"
                        : "Cross-Account Protection is disabled - basic security validation only"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting Cross-Account Protection status");
                return StatusCode(500, new { error = "An error occurred while getting Cross-Account Protection status." });
            }
        }

        /// <summary>
        /// Sets (or clears) the allowlist of Algorand receiver addresses the <c>transferAsset</c> MCP tool
        /// is permitted to send to for this paired session. An empty/omitted list removes the restriction
        /// (the default - transfers to any receiver are allowed unless a user opts into an allowlist).
        /// </summary>
        /// <param name="sessionId">Session ID of the paired device</param>
        /// <param name="request">The receiver addresses to allow</param>
        [AllowAnonymous]
        [EnableRateLimiting("device-session")]
        [HttpPost("receiver-allowlist/{sessionId}")]
        public async Task<ActionResult<DevicePairingResponse>> SetReceiverAllowlist(string sessionId, [FromBody] ReceiverAllowlistRequest request)
        {
            var result = await _devicePairingService.SetReceiverAllowlistAsync(sessionId, request?.AllowedReceivers ?? new List<string>());

            if (!result.Success)
            {
                if (result.Message.Contains("required") || result.Message.Contains("not found") || result.Message.Contains("expired"))
                {
                    return NotFound(result);
                }
                return StatusCode(500, result);
            }

            return Ok(result);
        }

        private static object GetTierBenefits(ServiceTier tier)
        {
            return tier switch
            {
                ServiceTier.Free => new
                {
                    tier = "Free",
                    portfolioRange = "< �10,000",
                    devices = 1,
                    support = "Community",
                    sla = "Best effort",
                    features = new[] { "Basic account management", "Standard security", "Portfolio tracking" }
                },
                ServiceTier.Professional => new
                {
                    tier = "Professional",
                    portfolioRange = "�10,000 - �1,000,000",
                    devices = 5,
                    support = "Priority",
                    sla = "99.5%",
                    features = new[] { "Full account management", "Advanced security", "Portfolio analytics", "Risk management" }
                },
                ServiceTier.Enterprise => new
                {
                    tier = "Enterprise",
                    portfolioRange = "> �1,000,000",
                    devices = "Unlimited",
                    support = "Dedicated",
                    sla = "99.9%",
                    features = new[] { "All Professional features", "Dedicated account manager", "Custom integrations", "Institutional security" }
                },
                _ => new { tier = "Unknown" }
            };
        }

        /// <summary>Request body for reporting a Cross-Account Protection security event.</summary>
        public class SecurityEventRequest
        {
            /// <summary>The kind of security event being reported.</summary>
            public SecurityEventType EventType { get; set; }

            /// <summary>Optional free-text details about the event.</summary>
            public string? Details { get; set; }
        }

        /// <summary>Request body for setting a paired session's <c>transferAsset</c> receiver allowlist.</summary>
        public class ReceiverAllowlistRequest
        {
            /// <summary>Allowed Algorand receiver addresses. Empty/omitted clears the restriction.</summary>
            public List<string> AllowedReceivers { get; set; } = new();
        }
    }
}
