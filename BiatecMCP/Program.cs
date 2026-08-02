using BiatecMCP.BusinessLogic;
using BiatecMCP.Model;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Authentication;

namespace BiatecMCP
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddProblemDetails();

            builder.Services.Configure<Configuration>(builder.Configuration.GetSection("App"));
            builder.Services.Configure<CorsConfiguration>(builder.Configuration.GetSection("Cors"));
            builder.Services.Configure<AlgodConfiguration>(builder.Configuration.GetSection("Algod"));
            builder.Services.Configure<OidcConfiguration>(builder.Configuration.GetSection("Oidc"));
            builder.Services.Configure<McpResourceConfiguration>(builder.Configuration.GetSection("Mcp"));

            var oidcConfig = new OidcConfiguration();
            builder.Configuration.GetSection("Oidc").Bind(oidcConfig);
            var mcpResourceConfig = new McpResourceConfiguration();
            builder.Configuration.GetSection("Mcp").Bind(mcpResourceConfig);

            // Add CORS configuration
            var corsConfig = new CorsConfiguration();
            builder.Configuration.GetSection("Cors").Bind(corsConfig);

            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(policyBuilder =>
                {
                    var origins = corsConfig.AllowedOrigins?.Where(o => !string.IsNullOrWhiteSpace(o)).ToArray() ?? Array.Empty<string>();

                    if (origins.Length > 0)
                    {
                        policyBuilder.SetIsOriginAllowed(origin => IsOriginAllowed(origin, origins));
                    }
                    else
                    {
                        // If no origins are configured, allow any origin in development
                        if (builder.Environment.IsDevelopment())
                        {
                            policyBuilder.AllowAnyOrigin();
                        }
                        else
                        {
                            // In production, don't allow any origin if none configured
                            policyBuilder.WithOrigins();
                        }
                    }

                    policyBuilder.AllowAnyMethod()
                               .AllowAnyHeader();

                    // Only allow credentials if we have specific origins (not AllowAnyOrigin)
                    if (origins.Length > 0)
                    {
                        policyBuilder.AllowCredentials();
                    }
                });
            });

            // ── OAuth 2.1 resource server ──────────────────────────────────────────────────────────
            // BiatecMCP delegates ALL authentication (and signing) to BiatecOIDC - it holds no key
            // material, no Google/Microsoft credentials, and no session state of its own. This wiring
            // implements the MCP Authorization spec (RFC 9728 Protected Resource Metadata + OAuth 2.1
            // bearer-token validation): JwtBearer validates incoming tokens locally against BiatecOIDC's
            // published JWKS (Authority - no per-request network call once keys are cached), while
            // AddMcp serves /.well-known/oauth-protected-resource and shapes the 401/WWW-Authenticate
            // challenge that tells an unauthenticated MCP client where to authenticate. See
            // CLAUDE.md's BiatecMCP architecture notes for the full request flow and the MCP C# SDK
            // API surface this was verified against (ModelContextProtocol.AspNetCore 1.4.1).
            builder.Services
                .AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                    options.DefaultChallengeScheme = McpAuthenticationDefaults.AuthenticationScheme;
                })
                .AddJwtBearer(options =>
                {
                    options.Authority = oidcConfig.Issuer;
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        // RFC 8707: this server's canonical resource URI is required in the token's `aud`
                        // claim (alongside whichever client_id obtained it) - see BiatecOIDC's
                        // JwtIssuerService "resource" parameter handling. This is what lets BiatecMCP
                        // validate tokens from ANY dynamically-registered MCP client (see BiatecOIDC's
                        // POST /register) against one stable value, instead of needing to know every
                        // individual client_id in advance.
                        ValidateAudience = true,
                        ValidAudience = mcpResourceConfig.CanonicalResourceUri
                    };
                })
                .AddMcp(options =>
                {
                    options.ResourceMetadata = new ProtectedResourceMetadata
                    {
                        Resource = mcpResourceConfig.CanonicalResourceUri,
                        AuthorizationServers = { oidcConfig.Issuer },
                        BearerMethodsSupported = { "header" },
                        // Deliberately just "openid sign" - the minimal scope set the 3 tools here need.
                        // A client wanting a different wallet-API scope (e.g. a future tool needing
                        // manage-limits/rekey) requests it explicitly; BiatecOIDC's own consent screen is
                        // what actually shows the user what's being granted either way.
                        ScopesSupported = { "openid", "sign" }
                    };
                });

            builder.Services.AddAuthorizationBuilder()
                .AddPolicy("sign", policy => policy.RequireClaim("sign", "true"));

            builder.Services.AddHttpContextAccessor();

            // Thin HTTP wrapper over BiatecOIDC's wallet REST API (/wallet/sign, /wallet/seeds) - forwards
            // the caller's own bearer token, never holds one of its own. Same typed-HttpClient idiom
            // BiatecOIDC's own BiatecRouterQuoteClient uses for its outbound HTTP dependency.
            builder.Services.AddHttpClient<IBiatecWalletClient, BiatecWalletClient>((sp, client) =>
            {
                client.BaseAddress = new Uri(oidcConfig.Issuer.TrimEnd('/') + "/");
            });

            // DEX aggregator quote providers for createSwapTransaction - BiatecRouterQuoteProvider is the
            // only one that can also build a real unsigned swap transaction (see its remarks);
            // FolksRouterQuoteProvider/HaystackRouterQuoteProvider are quote-only. Registered individually
            // (not just as IDexQuoteProvider) so DexSwapAggregatorService's IEnumerable<IDexQuoteProvider>
            // constructor picks up all three, while createSwapTransaction can still resolve
            // BiatecRouterQuoteProvider's own concrete type for BuildRouteTransactionsAsync.
            builder.Services.AddHttpClient<BiatecRouterQuoteProvider>();
            builder.Services.AddScoped<IDexQuoteProvider>(sp => sp.GetRequiredService<BiatecRouterQuoteProvider>());
            builder.Services.AddHttpClient<FolksRouterQuoteProvider>(client =>
            {
                client.BaseAddress = new Uri("https://api.folksrouter.io/");
            });
            builder.Services.AddScoped<IDexQuoteProvider>(sp => sp.GetRequiredService<FolksRouterQuoteProvider>());
            builder.Services.AddScoped<HaystackRouterQuoteProvider>();
            builder.Services.AddScoped<IDexQuoteProvider>(sp => sp.GetRequiredService<HaystackRouterQuoteProvider>());
            builder.Services.AddScoped<DexSwapAggregatorService>();

            // createBridgeTransaction (Aramid Finance) - config is discovered on-chain + via IPFS (see the
            // provider's remarks), not via a typed HttpClient with a fixed base address, so no
            // AddHttpClient registration is needed here.
            builder.Services.AddScoped<IAramidBridgeConfigProvider, AramidBridgeConfigProvider>();

            // Dynamic multi-chain support: every chain published at https://scholtz.github.io/AlgorandPublicData
            // that also currently has a live, genesis-hash-matching public algod node - see
            // AlgorandChainRegistry's remarks. BiatecMCP has no Redis by design, so the registry's ~10-minute
            // cache is in-process (IMemoryCache) rather than distributed.
            builder.Services.AddMemoryCache();
            builder.Services.AddScoped<IPublicAlgodDataSource, PublicAlgodDataSource>();
            builder.Services.AddScoped<IAlgorandChainRegistry, AlgorandChainRegistry>();

            // Configure MCP Server
            builder.Services.AddMcpServer()
                .WithHttpTransport(options =>
                {
                    options.Stateless = true;
                })
                .WithToolsFromAssembly()
                .AddAuthorizationFilters();

            var app = builder.Build();

            // Log CORS configuration for debugging
            var logger = app.Services.GetRequiredService<ILogger<Program>>();
            var corsConfigForLogging = new CorsConfiguration();
            app.Configuration.GetSection("Cors").Bind(corsConfigForLogging);

            if (corsConfigForLogging.AllowedOrigins?.Any() == true)
            {
                logger.LogInformation("CORS configured with allowed origins: {AllowedOrigins}",
                    string.Join(", ", corsConfigForLogging.AllowedOrigins));
            }
            else
            {
                logger.LogWarning("No CORS origins configured. Using default policy based on environment.");
            }

            // Enable static files with proper content types (serves wwwroot/index.html and legal pages)
            app.UseStaticFiles(new StaticFileOptions
            {
                OnPrepareResponse = context =>
                {
                    // Ensure HTML files are served with UTF-8 charset
                    if (context.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                    {
                        context.Context.Response.Headers.Append("Content-Type", "text/html; charset=utf-8");
                    }
                }
            });

            // Enable CORS
            app.UseCors();

            app.UseAuthentication();
            app.UseAuthorization();

            app.MapMcp("/mcp").RequireAuthorization();

            // Map default route to index.html
            app.MapGet("/", async context =>
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.SendFileAsync(Path.Combine(app.Environment.WebRootPath, "index.html"));
            });

            // Startup warm-up (do not remove, see CLAUDE.md "Startup warm-up" convention): force
            // endpoint/route-table compilation to happen now, during startup, instead of lazily on
            // whichever request Kubernetes routes here first once the pod's readiness probe passes. No
            // IActionDescriptorCollectionProvider warm-up here (unlike BiatecMCP/BiatecOIDC's other
            // Program.cs files) - this service has no MVC controllers left; MapMcp/MapGet are Minimal API
            // endpoints, fully covered by the DataSources warm-up below.
            foreach (var dataSource in ((Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)app).DataSources)
            {
                _ = dataSource.Endpoints;
            }

            app.Run();
        }

        /// <summary>
        /// Checks whether <paramref name="origin"/> matches one of the configured allowed origin
        /// patterns. Supports exact matches as well as single-level wildcard subdomains
        /// (e.g. <c>https://*.capitalism5.com</c> matches <c>https://www.capitalism5.com</c>) -
        /// something ASP.NET Core's built-in <c>WithOrigins</c> does not support.
        /// </summary>
        private static bool IsOriginAllowed(string origin, string[] allowedOriginPatterns)
        {
            foreach (var pattern in allowedOriginPatterns)
            {
                if (string.Equals(pattern, origin, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                var wildcardIndex = pattern.IndexOf("*.", StringComparison.Ordinal);
                if (wildcardIndex >= 0)
                {
                    var prefix = pattern[..wildcardIndex];
                    var suffix = pattern[(wildcardIndex + 1)..]; // keep the leading '.'

                    if (origin.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                        origin.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                        origin.Length > prefix.Length + suffix.Length)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
