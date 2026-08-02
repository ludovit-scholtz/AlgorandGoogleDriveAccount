using BiatecOIDC.BusinessLogic;
using BiatecOIDC.Model;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BiatecOIDC.Controllers
{
    /// <summary>
    /// Public, unauthenticated discovery endpoint for the Algorand-family chains BiatecOIDC currently
    /// considers usable - every chain published at https://scholtz.github.io/AlgorandPublicData that also
    /// has at least one currently-live, genesis-hash-matching public algod node (see
    /// <see cref="IAlgorandChainRegistry"/>). Intended for relying parties (not just BiatecMCP) that want to
    /// know which genesisIds are safe to pass to this deployment's wallet API. Deliberately does not expose
    /// a node's auth token/header - that's operational detail for BiatecMCP's own registry copy, not public
    /// information.
    /// </summary>
    [ApiController]
    [Route("chains")]
    [AllowAnonymous]
    public class ChainsController : ControllerBase
    {
        private readonly IAlgorandChainRegistry _chainRegistry;
        private readonly ILogger<ChainsController> _logger;

        public ChainsController(IAlgorandChainRegistry chainRegistry, ILogger<ChainsController> logger)
        {
            _chainRegistry = chainRegistry;
            _logger = logger;
        }

        /// <summary>Lists every Algorand-family chain BiatecOIDC currently considers usable.</summary>
        /// <response code="200">The currently-supported chains.</response>
        [HttpGet]
        public async Task<IActionResult> GetChains()
        {
            try
            {
                var chains = await _chainRegistry.GetSupportedChainsAsync();
                return Ok(new ChainsResponse
                {
                    Chains = chains
                        .Select(c => new ChainSummaryResponse
                        {
                            GenesisId = c.GenesisId,
                            Name = c.Name,
                            GenesisHash = c.GenesisHash,
                            AlgodApiAddress = c.AlgodApiAddress
                        })
                        .ToList()
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to discover the currently-supported Algorand chain list.");
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails { Title = "chain_registry_unavailable", Detail = "Unable to discover the currently-supported chain list." });
            }
        }
    }
}
