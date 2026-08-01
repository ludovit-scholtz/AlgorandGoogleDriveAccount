using System.Security.Claims;
using BiatecOIDC.Model;
using BiatecOIDC.Swagger;
using BiatecSelfCustodyCore.BusinessLogic;
using BiatecSelfCustodyCore.Model;
using BiatecSelfCustodyCore.Providers;
using BiatecSelfCustodyCore.Repository;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.OpenApi;

namespace BiatecOIDC
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddControllers();
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen(c =>
            {
                // The wallet API (/wallet/sign, /wallet/limits) and a few JwtIssuerController endpoints
                // (/userinfo, /verify) authenticate the caller via a manually-parsed Authorization: Bearer
                // header rather than [Authorize] (see BearerAuthOperationFilter / RequiresBearerTokenAttribute
                // for why), so Swashbuckle's own [Authorize]-based detection never fires for them. This
                // definition + filter is what makes Swagger UI's "Authorize" button appear and actually
                // attach the header on "Try it out" calls for those operations.
                c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description = "Paste the access_token from /token here (Swagger UI adds the 'Bearer ' prefix for you). Required for /wallet/sign, /wallet/limits, /userinfo, and /verify."
                });
                c.OperationFilter<BearerAuthOperationFilter>();

                var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
                var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
                if (File.Exists(xmlPath))
                {
                    c.IncludeXmlComments(xmlPath, includeControllerXmlComments: true);
                }
            });
            builder.Services.AddProblemDetails();

            var config = new Configuration();
            builder.Configuration.GetSection("App").Bind(config);
            builder.Services.Configure<Configuration>(builder.Configuration.GetSection("App"));
            builder.Services.Configure<AesOptions>(builder.Configuration.GetSection("AesOptions"));
            builder.Services.Configure<RedisConfiguration>(builder.Configuration.GetSection("Redis"));
            builder.Services.Configure<CorsConfiguration>(builder.Configuration.GetSection("Cors"));
            builder.Services.Configure<JwtIssuerConfiguration>(builder.Configuration.GetSection("JwtIssuer"));
            builder.Services.Configure<SpendingLimitsConfiguration>(builder.Configuration.GetSection("SpendingLimits"));
            builder.Services.Configure<ExchangeRateConfiguration>(builder.Configuration.GetSection("ExchangeRates"));
            builder.Services.Configure<ProviderTokenProtectionConfiguration>(builder.Configuration.GetSection("ProviderTokenProtection"));

            var googleCloudConfig = new GoogleCloudServiceConfiguration();
            builder.Configuration.GetSection("CloudServices:Google").Bind(googleCloudConfig);
            builder.Services.Configure<GoogleCloudServiceConfiguration>(builder.Configuration.GetSection("CloudServices:Google"));

            var entraConfig = new MicrosoftEntraConfiguration();
            builder.Configuration.GetSection("CloudServices:Entra").Bind(entraConfig);
            builder.Services.Configure<MicrosoftEntraConfiguration>(builder.Configuration.GetSection("CloudServices:Entra"));

            // TenantId defaults to "common" even when Entra was never set up, so ClientId/ClientSecret
            // (both empty by default, see MicrosoftEntraConfiguration) are the actual signal that an Entra
            // app registration has been configured. Gates both the Microsoft ICloudStorageProvider
            // registration below and the AddOpenIdConnect scheme further down - without this, Microsoft
            // would show up as a sign-in option on /select-provider (and pair.html) with no working app
            // registration behind it, and CloudStorageProviderCatalog.All has no other way to know.
            // Also rejects the checked-in appsettings.json placeholder values ("your-entra-client-id"/
            // "your-entra-client-secret") - those are non-empty strings, so an IsNullOrWhiteSpace check
            // alone treats an unedited template as "configured".
            var entraConfigured = IsConfiguredValue(entraConfig.ClientId) && IsConfiguredValue(entraConfig.ClientSecret);

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

            // Self-custody storage providers (BiatecSelfCustodyCore/Providers) - see BiatecMCP/Program.cs
            // for why both apps register the same set: ICloudAccountRepository is what JwtIssuerService
            // uses to resolve the algorand_address claim, regardless of which provider the user signed
            // in with. To add a new provider, mirror both the registrations below and the matching
            // AddOpenIdConnect(...) scheme block further down in both apps.
            builder.Services.AddHttpClient<GoogleCloudStorageProvider>();
            builder.Services.AddScoped<ICloudStorageProvider>(sp => sp.GetRequiredService<GoogleCloudStorageProvider>());
            if (entraConfigured)
            {
                builder.Services.AddHttpClient<MicrosoftCloudStorageProvider>();
                builder.Services.AddScoped<ICloudStorageProvider>(sp => sp.GetRequiredService<MicrosoftCloudStorageProvider>());
            }
            builder.Services.AddScoped<ICloudStorageProviderCatalog, CloudStorageProviderCatalog>();
            builder.Services.AddScoped<ICloudAccountRepository, CloudAccountRepository>();

            // Add business logic services
            builder.Services.AddScoped<BiatecSelfCustodyCore.BusinessLogic.IDriveService, BiatecSelfCustodyCore.BusinessLogic.DriveService>();

            // Caches the caller's Google/Microsoft access token (encrypted, under a key dedicated to this
            // purpose - never AesOptions, the self-custody file's key) inside the Biatec access token
            // itself, so wallet API callers don't have to separately manage/resend their own provider
            // token - see ProviderAccessTokenProtector's remarks and OIDC_INTEGRATION_GUIDE.md's "Provider
            // access token caching" section for the full design/threat-model writeup.
            builder.Services.AddScoped<BiatecOIDC.BusinessLogic.IProviderAccessTokenProtector, BiatecOIDC.BusinessLogic.ProviderAccessTokenProtector>();
            builder.Services.AddScoped<BiatecOIDC.BusinessLogic.IJwtIssuerService, BiatecOIDC.BusinessLogic.JwtIssuerService>();

            // Wallet API (WalletController): signs transaction groups gated on the "sign" claim and
            // manages the per-user daily/weekly/monthly spending limits gated on the "manage-limits" claim
            // - see JwtIssuerService.CreateAccessToken for how those claims get onto the access token.
            // BiatecRouterQuoteClient/CnbExchangeRateService are typed HttpClients (see the Google/Microsoft
            // provider registrations above for why): BiatecRouterQuoteClient prices a spent asset in USD via
            // the Biatec Router's public /quote endpoint; CnbExchangeRateService converts that USD value into
            // the caller's configured limit currency using the Czech National Bank's cached daily fixing.
            builder.Services.AddHttpClient<BiatecOIDC.BusinessLogic.BiatecRouterQuoteClient>();
            builder.Services.AddScoped<BiatecOIDC.BusinessLogic.IBiatecRouterQuoteClient>(sp => sp.GetRequiredService<BiatecOIDC.BusinessLogic.BiatecRouterQuoteClient>());
            builder.Services.AddScoped<BiatecOIDC.BusinessLogic.IAssetValuationService, BiatecOIDC.BusinessLogic.BiatecRouterValuationService>();

            builder.Services.AddHttpClient<BiatecOIDC.BusinessLogic.CnbExchangeRateService>();
            builder.Services.AddScoped<BiatecOIDC.BusinessLogic.IExchangeRateService>(sp => sp.GetRequiredService<BiatecOIDC.BusinessLogic.CnbExchangeRateService>());

            builder.Services.AddScoped<BiatecOIDC.BusinessLogic.ISpendingLimitService, BiatecOIDC.BusinessLogic.SpendingLimitService>();
            builder.Services.AddScoped<BiatecOIDC.BusinessLogic.IWalletService, BiatecOIDC.BusinessLogic.WalletService>();

            builder.Services.AddHttpContextAccessor();

            // Add Redis distributed cache
            var redisConfig = new RedisConfiguration();
            builder.Configuration.GetSection("Redis").Bind(redisConfig);
            builder.Services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = redisConfig.ConnectionString;
            });

            // Direct StackExchange.Redis connection multiplexer, used where atomic Redis primitives
            // (e.g. GETDEL for one-time-use OIDC codes/tokens) aren't exposed by IDistributedCache.
            builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(_ =>
                StackExchange.Redis.ConnectionMultiplexer.Connect(redisConfig.ConnectionString));

            var authenticationBuilder = builder.Services
                .AddAuthentication(options =>
                {
                    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                    options.DefaultChallengeScheme = GoogleCloudStorageProvider.ProviderName;
                })
                .AddCookie()
                .AddGoogleOpenIdConnect(GoogleCloudStorageProvider.ProviderName, options =>
                {
                    options.ClientId = googleCloudConfig.ClientId;
                    options.ClientSecret = googleCloudConfig.ClientSecret;

                    // Distinct from BiatecMCP's default /signin-google - both apps share the
                    // google.biatec.io host, and this callback must land on THIS pod (routed via the
                    // literal-path entries added to biatec-oidc-ingress), not fall through to
                    // BiatecMCP's catch-all ingress, which can't decrypt this app's correlation cookie.
                    options.CallbackPath = "/oidc/signin-google";

                    // Basic scopes - only request what's needed to identify the user and (optionally)
                    // read their self-custody account address for the algorand_address claim.
                    options.Scope.Clear();
                    options.Scope.Add("openid");
                    options.Scope.Add("profile");
                    options.Scope.Add("email");
                    options.Scope.Add(Google.Apis.Drive.v3.DriveService.Scope.DriveFile);

                    options.ClaimActions.MapJsonKey(ClaimTypes.Email, "email");
                    options.ClaimActions.MapJsonKey(ClaimTypes.Name, "name");
                    options.ClaimActions.MapJsonKey(ClaimTypes.GivenName, "given_name");
                    options.ClaimActions.MapJsonKey(ClaimTypes.Surname, "family_name");

                    options.Events = new Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectEvents
                    {
                        OnRedirectToIdentityProvider = context =>
                        {
                            OpenIdConnectIncrementalAuth.Apply(context);
                            return Task.CompletedTask;
                        },
                        OnTokenValidated = context =>
                        {
                            // Enforce that Google actually verified the user's email, unconditionally -
                            // email is the sole tenant-isolation input for the self-custody key derivation
                            // and the OIDC subject/claims this provider issues.
                            var emailVerifiedClaim = context.Principal?.FindFirst("email_verified")?.Value;
                            if (string.Equals(emailVerifiedClaim, "false", StringComparison.OrdinalIgnoreCase))
                            {
                                context.Fail("Google account email is not verified.");
                                return Task.CompletedTask;
                            }

                            CloudStorageProviderClaims.Stamp(context.Principal, GoogleCloudStorageProvider.ProviderName);
                            return Task.CompletedTask;
                        },
                        OnAuthenticationFailed = context =>
                        {
                            var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
                            logger.LogError(context.Exception, "OpenIdConnect authentication failed: {ErrorMessage}", context.Exception?.Message);
                            return Task.CompletedTask;
                        }
                    };
                });

            // Only register the Microsoft auth scheme when an Entra app registration is actually
            // configured (see entraConfigured above) - otherwise it would appear as a challengeable
            // scheme/provider with an Authority built from placeholder/empty ClientId/ClientSecret that
            // can only ever fail sign-in.
            if (entraConfigured)
            {
                authenticationBuilder.AddOpenIdConnect(MicrosoftCloudStorageProvider.ProviderName, options =>
                {
                    options.Authority = $"https://login.microsoftonline.com/{entraConfig.TenantId}/v2.0";
                    options.ClientId = entraConfig.ClientId;
                    options.ClientSecret = entraConfig.ClientSecret;
                    options.ResponseType = "code";
                    options.UsePkce = true;
                    options.SaveTokens = true;

                    // Distinct from BiatecMCP's /signin-microsoft, same reasoning as the Google
                    // CallbackPath above - routed via biatec-oidc-ingress.
                    options.CallbackPath = "/oidc/signin-microsoft";

                    options.Scope.Clear();
                    options.Scope.Add("openid");
                    options.Scope.Add("profile");
                    options.Scope.Add("email");
                    options.Scope.Add("offline_access");
                    options.Scope.Add("https://graph.microsoft.com/Files.ReadWrite.AppFolder");

                    options.ClaimActions.MapJsonKey(ClaimTypes.Email, "email");
                    options.ClaimActions.MapJsonKey(ClaimTypes.Name, "name");

                    options.Events = new Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectEvents
                    {
                        OnRedirectToIdentityProvider = context =>
                        {
                            OpenIdConnectIncrementalAuth.Apply(context);
                            return Task.CompletedTask;
                        },
                        OnTokenValidated = context =>
                        {
                            CloudStorageProviderClaims.Stamp(context.Principal, MicrosoftCloudStorageProvider.ProviderName);
                            return Task.CompletedTask;
                        },
                        OnAuthenticationFailed = context =>
                        {
                            var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
                            logger.LogError(context.Exception, "Microsoft OpenIdConnect authentication failed: {ErrorMessage}", context.Exception?.Message);
                            return Task.CompletedTask;
                        }
                    };
                });
            }

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

            app.UseSwagger(options =>
            {
                options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1;
            });
            app.UseSwaggerUI();

            // Serves wwwroot/index.html (the OIDC/wallet API documentation site, reachable at
            // https://oidc.biatec.io/) and its assets (logo). Only reachable on oidc.biatec.io's own
            // Ingress - the legacy google.biatec.io alias only carves out this app's OIDC-protocol paths,
            // not "/", so BiatecMCP's site there is unaffected.
            app.UseStaticFiles(new StaticFileOptions
            {
                OnPrepareResponse = context =>
                {
                    if (context.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                    {
                        context.Context.Response.Headers.Append("Content-Type", "text/html; charset=utf-8");
                    }
                }
            });

            app.UseCors();

            app.UseAuthentication();
            app.UseAuthorization();

            app.MapControllers();

            app.MapGet("/", async context =>
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.SendFileAsync(Path.Combine(app.Environment.WebRootPath, "index.html"));
            });

            // Startup warm-up (do not remove, see CLAUDE.md "Startup warm-up" convention): force
            // controller/action discovery and endpoint/route-table compilation to happen now, during
            // startup, instead of lazily on whichever request Kubernetes routes here first once the
            // pod's readiness probe passes.
            _ = app.Services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors;
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

        /// <summary>
        /// True when <paramref name="value"/> looks like a real configured secret/id rather than an unedited
        /// checked-in appsettings.json placeholder (the repo's convention for those is a <c>"your-..."</c>
        /// literal, e.g. <c>"your-entra-client-id"</c>) or empty/whitespace.
        /// </summary>
        private static bool IsConfiguredValue(string? value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && !value.StartsWith("your-", StringComparison.OrdinalIgnoreCase)
                && value != "ClientId";
        }
    }
}
