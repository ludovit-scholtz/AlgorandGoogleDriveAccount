using BiatecOIDC.Helper;
using BiatecOIDC.Model;
using Microsoft.Extensions.Options;

namespace BiatecOIDC.BusinessLogic
{
    /// <summary>Why a device call was refused.</summary>
    public enum DeviceAuthFailure
    {
        None,
        /// <summary>No, malformed or unknown <c>Authorization: Device</c> token.</summary>
        InvalidToken,
        /// <summary>Missing, wrong or replayed <c>X-Device-Proof</c>.</summary>
        InvalidProof,
    }

    /// <summary>Authenticates calls made with <c>Authorization: Device &lt;token&gt;</c> plus a proof of possession (see <see cref="DeviceProofVerifier"/>).</summary>
    public interface IDeviceAuthenticator
    {
        Task<(DeviceRecord? Device, DeviceAuthFailure Failure)> AuthenticateAsync(HttpRequest request);
    }

    /// <inheritdoc cref="IDeviceAuthenticator"/>
    public sealed class DeviceAuthenticator : IDeviceAuthenticator
    {
        public const string ProofHeader = "X-Device-Proof";

        private readonly IDeviceStore _store;
        private readonly IJwtIssuerService _jwt;
        private readonly DevicesConfiguration _config;
        private readonly TimeProvider _time;

        public DeviceAuthenticator(IDeviceStore store, IJwtIssuerService jwt, IOptions<DevicesConfiguration> config, TimeProvider time)
        {
            _store = store;
            _jwt = jwt;
            _config = config.Value;
            _time = time;
        }

        public async Task<(DeviceRecord? Device, DeviceAuthFailure Failure)> AuthenticateAsync(HttpRequest request)
        {
            var header = request.Headers.Authorization.ToString();
            const string prefix = "Device ";
            if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || header.Length == prefix.Length)
            {
                return (null, DeviceAuthFailure.InvalidToken);
            }

            var token = header[prefix.Length..].Trim();
            var device = await _store.GetByTokenHashAsync(DeviceStore.Hash(token));
            if (device is null)
            {
                return (null, DeviceAuthFailure.InvalidToken);
            }

            var url = _jwt.GetIssuer(request) + request.Path.Value;
            var result = DeviceProofVerifier.Verify(request.Headers[ProofHeader].ToString(), device.Kid, device.KeyX, device.KeyY,
                request.Method, url, token, _time.GetUtcNow(), _config.ProofSkewSeconds, out var jti);
            if (result != DeviceProofResult.Valid)
            {
                return (null, DeviceAuthFailure.InvalidProof);
            }

            // Remembered for twice the accepted window, so a replay inside the window and just after it is both refused.
            if (!await _store.TryRememberProofAsync(device.Kid, jti!, TimeSpan.FromSeconds(2 * _config.ProofSkewSeconds)))
            {
                return (null, DeviceAuthFailure.InvalidProof);
            }

            return (device, DeviceAuthFailure.None);
        }
    }
}
