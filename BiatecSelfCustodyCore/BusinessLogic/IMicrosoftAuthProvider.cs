namespace BiatecSelfCustodyCore.BusinessLogic
{
    /// <summary>
    /// Microsoft analogue of <c>Google.Apis.Auth.AspNetCore3.IGoogleAuthProvider</c>: resolves the
    /// current signed-in user's Microsoft access token from the ambient <c>HttpContext</c> (the
    /// cookie-based flows in <c>DriveController</c>/<c>JwtIssuerService</c>, which have no explicit
    /// token to hand in - unlike the device-pairing path, which passes a Redis-stored token directly).
    /// </summary>
    public interface IMicrosoftAuthProvider
    {
        /// <summary>The current signed-in user's Microsoft access token, or <c>null</c> if there isn't one.</summary>
        Task<string?> GetAccessTokenAsync();
    }
}
