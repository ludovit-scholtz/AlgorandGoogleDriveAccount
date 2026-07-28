using BiatecOIDC.Model;
using BiatecSelfCustodyCore.BusinessLogic;
using BiatecSelfCustodyCore.Model;
using BiatecSelfCustodyCore.Repository;
using Google.Apis.Auth.AspNetCore3;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.OpenApi;
using System.Linq;
using System.Security.Claims;

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

            var entraConfig = new MicrosoftEntraConfiguration();
            builder.Configuration.GetSection("MicrosoftEntra").Bind(entraConfig);
            builder.Services.Configure<MicrosoftEntraConfiguration>(builder.Configuration.GetSection("MicrosoftEntra"));

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

            // Self-custody storage backends (BiatecSelfCustodyCore) - see BiatecMCP/Program.cs for why
            // both apps register the same set: ICloudAccountRepository is what JwtIssuerService uses
            // to resolve the algorand_address claim, regardless of which provider the user signed in with.
            builder.Services.AddSingleton<GoogleDriveFileStore>();
            builder.Services.AddHttpClient<OneDriveFileStore>();
            builder.Services.AddHttpClient<StorageAccessVerifier>();
            builder.Services.AddScoped<IMicrosoftAuthProvider, MicrosoftAuthProvider>();
            builder.Services.AddScoped<ICloudAccountRepository, CloudAccountRepository>();

            // Add business logic services
            builder.Services.AddScoped<BiatecSelfCustodyCore.BusinessLogic.IDriveService, BiatecSelfCustodyCore.BusinessLogic.DriveService>();
            builder.Services.AddScoped<BiatecOIDC.BusinessLogic.IJwtIssuerService, BiatecOIDC.BusinessLogic.JwtIssuerService>();

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

            builder.Services
                .AddAuthentication(options =>
                {
                    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                    options.DefaultChallengeScheme = GoogleOpenIdConnectDefaults.AuthenticationScheme;
                })
                .AddCookie()
                .AddGoogleOpenIdConnect(options =>
                {
                    options.ClientId = config.ClientId;
                    options.ClientSecret = config.ClientSecret;

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

                            (context.Principal?.Identity as ClaimsIdentity)?.AddClaim(new Claim(AuthSchemeNames.IdpClaimType, AuthSchemeNames.Google));
                            return Task.CompletedTask;
                        },
                        OnAuthenticationFailed = context =>
                        {
                            var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
                            logger.LogError(context.Exception, "OpenIdConnect authentication failed: {ErrorMessage}", context.Exception?.Message);
                            return Task.CompletedTask;
                        }
                    };
                })
                .AddOpenIdConnect(AuthSchemeNames.Microsoft, options =>
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
                            (context.Principal?.Identity as ClaimsIdentity)?.AddClaim(new Claim(AuthSchemeNames.IdpClaimType, AuthSchemeNames.Microsoft));
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

            app.UseCors();

            app.UseAuthentication();
            app.UseAuthorization();

            app.MapControllers();

            _ = app.Services.GetService<GoogleDriveFileStore>();

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
