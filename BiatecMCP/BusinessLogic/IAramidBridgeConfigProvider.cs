namespace BiatecMCP.BusinessLogic
{
    /// <summary>
    /// Fetches Aramid Finance's live bridge configuration (which chains/tokens it currently bridges, the
    /// bridge deposit address per chain, and each route's fee schedule) - see
    /// <c>AramidBridgeConfigProvider</c> for how it's discovered/fetched. Aramid's own integration guide says
    /// not to cache this indefinitely and to re-validate immediately before building a transaction, so
    /// callers should fetch fresh each time rather than holding onto a previous result.
    /// </summary>
    public interface IAramidBridgeConfigProvider
    {
        Task<AramidConfigRoot> GetConfigAsync(CancellationToken cancellationToken = default);
    }
}
