using System.Security.Claims;
using BiatecMCP.Model;
using BiatecMCP.Swagger;
using BiatecSelfCustodyCore.BusinessLogic;
using BiatecSelfCustodyCore.Model;
using BiatecSelfCustodyCore.Providers;
using BiatecSelfCustodyCore.Repository;
using Google.Apis.Auth.AspNetCore3;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.OpenApi;

namespace BiatecMCP
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.

            builder.Services.AddControllers();
            // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen(c =>
            {
                // Login for [Authorize] endpoints is the same Google session cookie the rest of the
                // site uses (see /api/drive/login). This definition just documents that and lets
                // Swagger UI show a padlock; the actual "Authorize" button in Swagger UI has nowhere
                // to send a browser-redirect OAuth login, so sign-in happens via the Login link
                // injected into the page instead (see wwwroot/swagger/swagger-login.js).
                c.AddSecurityDefinition("GoogleCookieAuth", new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey,
                    In = ParameterLocation.Cookie,
                    Name = CookieAuthenticationDefaults.CookiePrefix + CookieAuthenticationDefaults.AuthenticationScheme,
                    Description = "Google-authenticated session cookie. Click the \"Login with Google\" link " +
                        "above the operations list, sign in, then come back here and use \"Try it out\" - " +
                        "the browser sends the session cookie automatically."
                });

                c.OperationFilter<AuthorizeCheckOperationFilter>();

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
            builder.Services.Configure<CrossAccountProtectionConfiguration>(builder.Configuration.GetSection("CrossAccountProtection"));
            builder.Services.Configure<AlgodConfiguration>(builder.Configuration.GetSection("Algod"));
            builder.Services.Configure<McpTransferLimitsConfiguration>(builder.Configuration.GetSection("McpTransferLimits"));

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

                // Add a named policy for API endpoints that might need different CORS settings
                options.AddPolicy("ApiPolicy", policyBuilder =>
                {
                    var origins = corsConfig.AllowedOrigins?.Where(o => !string.IsNullOrWhiteSpace(o)).ToArray() ?? Array.Empty<string>();

                    if (origins.Length > 0)
                    {
                        policyBuilder.SetIsOriginAllowed(origin => IsOriginAllowed(origin, origins));
                    }
                    else if (builder.Environment.IsDevelopment())
                    {
                        policyBuilder.AllowAnyOrigin();
                    }

                    policyBuilder.AllowAnyMethod()
                               .AllowAnyHeader();

                    if (origins.Length > 0)
                    {
                        policyBuilder.AllowCredentials();
                    }
                });
            });

            // Self-custody storage providers (BiatecSelfCustodyCore/Providers). Each provider is a
            // single ICloudStorageProvider implementation registered here; ICloudAccountRepository
            // owns the shared AES/ARC76 account logic and resolves the right one via the catalog,
            // so it never needs to change when a provider is added. To add a new provider: implement
            // ICloudStorageProvider, register it the same way as the two below, and add a matching
            // AddOpenIdConnect(...) scheme block further down (copy the Microsoft block as a template).
            builder.Services.AddHttpClient<GoogleCloudStorageProvider>();
            builder.Services.AddScoped<ICloudStorageProvider>(sp => sp.GetRequiredService<GoogleCloudStorageProvider>());
            builder.Services.AddHttpClient<MicrosoftCloudStorageProvider>();
            builder.Services.AddScoped<ICloudStorageProvider>(sp => sp.GetRequiredService<MicrosoftCloudStorageProvider>());
            builder.Services.AddScoped<ICloudStorageProviderCatalog, CloudStorageProviderCatalog>();
            builder.Services.AddScoped<ICloudAccountRepository, CloudAccountRepository>();

            // Add business logic services
            builder.Services.AddScoped<BiatecMCP.BusinessLogic.IDevicePairingService, BiatecMCP.BusinessLogic.DevicePairingService>();
            builder.Services.AddScoped<BiatecSelfCustodyCore.BusinessLogic.IDriveService, BiatecSelfCustodyCore.BusinessLogic.DriveService>();
            builder.Services.AddScoped<BiatecMCP.BusinessLogic.ICrossAccountProtectionService, BiatecMCP.BusinessLogic.CrossAccountProtectionService>();
            builder.Services.AddScoped<BiatecMCP.BusinessLogic.IPortfolioValuationService, BiatecMCP.BusinessLogic.PortfolioValuationService>();

            // Add HTTP context accessor for authorization service
            builder.Services.AddHttpContextAccessor();

            // Add HttpClient for Cross-Account Protection API calls
            builder.Services.AddHttpClient<BiatecMCP.BusinessLogic.CrossAccountProtectionService>();

            // Add Redis distributed cache
            var redisConfig = new RedisConfiguration();
            builder.Configuration.GetSection("Redis").Bind(redisConfig);
            builder.Services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = redisConfig.ConnectionString;
            });

            // Each provider registered above gets one OIDC scheme block here, named after its
            // ICloudStorageProvider.Name. To add provider #3: copy the Microsoft block (plain
            // AddOpenIdConnect - Google's uses AddGoogleOpenIdConnect from Google.Apis.Auth.AspNetCore3
            // purely because that package ships a small wrapper around the same handler), point it at
            // the new provider's OIDC endpoint/scopes, and stamp its ProviderName via
            // CloudStorageProviderClaims.Stamp in OnTokenValidated.
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

                    // Basic scopes - only request what's needed initially
                    options.Scope.Clear(); // Clear default scopes
                    options.Scope.Add("openid");
                    options.Scope.Add("profile");
                    options.Scope.Add("email");
                    options.Scope.Add(Google.Apis.Drive.v3.DriveService.Scope.DriveFile);

                    // Note: Cross-Account Protection doesn't require a special scope
                    // It works with standard OAuth scopes and proper security practices

                    options.ClaimActions.MapJsonKey(ClaimTypes.Email, "email");
                    options.ClaimActions.MapJsonKey(ClaimTypes.Name, "name");
                    options.ClaimActions.MapJsonKey(ClaimTypes.GivenName, "given_name");
                    options.ClaimActions.MapJsonKey(ClaimTypes.Surname, "family_name");

                    options.Events = new Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectEvents
                    {
                        OnRedirectToIdentityProvider = context =>
                        {
                            // Store session information in authentication properties
                            if (context.Properties.Items.ContainsKey("sessionId"))
                            {
                                context.ProtocolMessage.State = context.Properties.Items["sessionId"];
                            }
                            OpenIdConnectIncrementalAuth.Apply(context);
                            return Task.CompletedTask;
                        },
                        OnTokenValidated = context =>
                        {
                            // Enforce that Google actually verified the user's email, unconditionally -
                            // independent of the optional Cross-Account Protection feature toggle - since
                            // email is the sole tenant-isolation input for the self-custody key derivation.
                            var emailVerifiedClaim = context.Principal?.FindFirst("email_verified")?.Value;
                            if (string.Equals(emailVerifiedClaim, "false", StringComparison.OrdinalIgnoreCase))
                            {
                                context.Fail("Google account email is not verified.");
                                return Task.CompletedTask;
                            }

                            // Record which provider signed this session in, so the right
                            // ICloudStorageProvider can be resolved from the cookie later.
                            CloudStorageProviderClaims.Stamp(context.Principal, GoogleCloudStorageProvider.ProviderName);

                            return Task.CompletedTask;
                        },
                        OnAuthenticationFailed = context =>
                        {
                            // Log the authentication failure for debugging
                            var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
                            logger.LogError(context.Exception, "OpenIdConnect authentication failed: {ErrorMessage}", context.Exception?.Message);

                            // Handle nonce validation errors specifically
                            if (context.Exception?.Message?.Contains("nonce") == true)
                            {
                                logger.LogWarning("Nonce validation failed - this may be due to device pairing flow. Continuing with authentication.");
                                context.HandleResponse();
                                // Redirect to an error page or handle gracefully
                                context.Response.Redirect("/pair.html?error=nonce_validation_failed");
                                return Task.CompletedTask;
                            }

                            return Task.CompletedTask;
                        }
                    };
                    // Configure protocol validator to be more lenient with nonce validation for device flows
                    options.ProtocolValidator.RequireNonce = false;
                })
                .AddOpenIdConnect(MicrosoftCloudStorageProvider.ProviderName, options =>
                {
                    options.Authority = $"https://login.microsoftonline.com/{entraConfig.TenantId}/v2.0";
                    options.ClientId = entraConfig.ClientId;
                    options.ClientSecret = entraConfig.ClientSecret;
                    options.ResponseType = "code";
                    options.UsePkce = true;
                    options.SaveTokens = true;
                    options.CallbackPath = "/signin-microsoft";

                    // Basic scopes plus the OneDrive app-folder permission - see BiatecOIDC/ENTRA_SETUP_GUIDE.md
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
                            if (context.Properties.Items.ContainsKey("sessionId"))
                            {
                                context.ProtocolMessage.State = context.Properties.Items["sessionId"];
                            }
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

                            if (context.Exception?.Message?.Contains("nonce") == true)
                            {
                                logger.LogWarning("Nonce validation failed - this may be due to device pairing flow. Continuing with authentication.");
                                context.HandleResponse();
                                context.Response.Redirect("/pair.html?error=nonce_validation_failed");
                                return Task.CompletedTask;
                            }

                            return Task.CompletedTask;
                        }
                    };
                    options.ProtocolValidator.RequireNonce = false;
                });

            builder.Services.AddControllersWithViews();

            // Rate limit the device-pairing session-keyed endpoints (defense-in-depth against brute-force /
            // abuse even once the session ID itself is a high-entropy CSPRNG value).
            builder.Services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                options.AddPolicy("device-session", httpContext =>
                {
                    var partitionKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown-ip";
                    return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ =>
                        new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 20,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0
                        });
                });
            });

            // Configure MCP Server
            builder.Services.AddMcpServer()
                .WithHttpTransport(options =>
                {
                    options.Stateless = true;
                })
                .WithToolsFromAssembly();


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
            app.UseSwaggerUI(options =>
            {
                options.InjectJavascript("/swagger/swagger-login.js");
            });

            // Enable static files with proper content types
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
            app.UseRateLimiter();

            app.MapControllers();
            app.MapMcp("/mcp");

            // Map default route to index.html
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
    }
}
