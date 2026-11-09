using BiatecOIDC.Model;

namespace BiatecOIDC.BusinessLogic
{
    /// <summary>Persistence of enrolled devices, the replay memory of device proofs and the push-registration rate limit.</summary>
    public interface IDeviceStore
    {
        /// <summary>Adds a device; false when the kid is taken.</summary>
        Task<bool> TryAddAsync(DeviceRecord device);

        Task<DeviceRecord?> GetByKidAsync(string kid);

        /// <summary>Finds the device that owns the token with this SHA-256 hex hash.</summary>
        Task<DeviceRecord?> GetByTokenHashAsync(string tokenHash);

        Task<int> CountForUserAsync(string sub);

        /// <summary>All devices of a user.</summary>
        Task<IReadOnlyList<DeviceRecord>> ListForUserAsync(string sub);

        /// <summary>
        /// Sets where the device's notifications go. A push token belongs to one device: it is removed from every other
        /// device record first.
        /// </summary>
        Task SetPushAsync(string kid, string provider, string token);

        /// <summary>True the first time a (device, jti) is seen within <paramref name="ttl"/>; false for a replay.</summary>
        Task<bool> TryRememberProofAsync(string kid, string jti, TimeSpan ttl);

        /// <summary>Counts one push registration of the device in the current minute; returns how many there were including this one.</summary>
        Task<int> CountPushRegistrationAsync(string kid);
    }
}
