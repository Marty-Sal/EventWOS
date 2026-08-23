using EventWOS.Api;
using EventWOS.Api.Authorization;
using Asp.Versioning;
using EventWOS.Api.Hubs;
using EventWOS.Api.Middleware;
using EventWOS.Application.Auth.Interfaces;
using EventWOS.Domain.Interfaces;
using EventWOS.Infrastructure.Auth;
using EventWOS.Infrastructure.Http;
using EventWOS.Persistence;
using EventWOS.Application.Interfaces;
using EventWOS.Persistence.Seed;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;
using System.Security.Cryptography;
using System.Threading.RateLimiting;

// ─── Bootstrap Serilog to console immediately ────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
    .CreateBootstrapLogger();

Log.Information("EventWOS API bootstrap starting...");

try
{
    // ─── Npgsql legacy timestamp behavior ─────────────────────────────────────
    // Our schema uses `timestamp` (without time zone) but EF Core property mapping
    // sometimes infers `timestamptz`, causing
    // "Cannot write DateTime with Kind=Unspecified to PostgreSQL type 'timestamp
    //  with time zone', only UTC is supported."
    // Restore .NET 5-era behavior so Kind is not enforced. All our code uses
    // DateTime.UtcNow anyway — this is purely a read-back safety net.
    AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

    var builder = WebApplication.CreateBuilder(args);

    // ─── Serilog (console only — no file sink in containers) ─────────────────
    builder.Host.UseSerilog((ctx, services, config) => config
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}"));

    Log.Information("Configuring services...");

    // ─── Database (PostgreSQL + EF Core) ─────────────────────────────────────
    var pgConn = builder.Configuration.GetConnectionString("DefaultConnection");
    Log.Information("DB connection string present: {Present}", !string.IsNullOrWhiteSpace(pgConn));

    builder.Services.AddDbContext<AppDbContext>(opts =>
        opts.UseNpgsql(pgConn, npgsql =>
        {
            // NOTE: deliberately NOT calling npgsql.MigrationsAssembly(...) here.
            // AppDbContext and all 21 Migration classes live in the SAME assembly
            // (EventWOS.Persistence), so the correct behavior is EF Core's default:
            // use context.GetType().Assembly directly (the already-loaded Type
            // reference, no re-load).
            // The removed call passed typeof(AppDbContext).Assembly.FullName as a
            // STRING, which makes EF call Assembly.Load(new AssemblyName(...))
            // internally to re-resolve the assembly by name at runtime. On this
            // deployment that produced a second, distinct load of
            // EventWOS.Persistence with its own Type identities - so EF's internal
            // filter (`t.IsSubclassOf(typeof(Migration))`, comparing Types loaded
            // from two different Assembly instances) matched none of the 21
            // migration classes, even though plain reflection over
            // typeof(AppDbContext).Assembly found all 21 correctly. That's why
            // db.Database.GetMigrations() logged "total in assembly: 0" and
            // MigrateAsync() silently did nothing on every boot.
            npgsql.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null);
        }));

    builder.Services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
    builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
    // Shared write path for both rating flows plus the reputation cache
    // recompute. Scoped: it works through the request's IAppDbContext so the
    // rating and the recomputed average land in one unit of work.
    builder.Services.AddScoped<EventWOS.Application.Ratings.RatingWriter>();
    builder.Services.AddScoped<DatabaseSeeder>();

    // ─── Redis (with fallback to in-memory if Redis unavailable) ─────────────
    var redisConn = builder.Configuration.GetConnectionString("Redis");
    Log.Information("Redis connection string present: {Present}", !string.IsNullOrWhiteSpace(redisConn));

    if (!string.IsNullOrWhiteSpace(redisConn))
    {
        builder.Services.AddStackExchangeRedisCache(opts =>
        {
            opts.Configuration = redisConn;
            opts.InstanceName = "eventwos:";
        });
        Log.Information("Redis distributed cache registered.");
    }
    else
    {
        builder.Services.AddDistributedMemoryCache();
        Log.Warning("Redis not configured — using in-memory distributed cache.");
    }

    // ─── MediatR ─────────────────────────────────────────────────────────────
    {
        var appAssembly = typeof(EventWOS.Application.Auth.Commands.RequestOtpCommand).Assembly;
        Log.Information("MediatR scanning assembly: {Assembly}", appAssembly.FullName);
        try
        {
            var types = appAssembly.GetTypes();
            var handlers = types.Where(t =>
                !t.IsAbstract && !t.IsInterface &&
                t.GetInterfaces().Any(i => i.IsGenericType &&
                    i.GetGenericTypeDefinition() == typeof(IRequestHandler<,>))).ToList();
            Log.Information("MediatR discovered {Count} handler(s): {Handlers}",
                handlers.Count, string.Join(", ", handlers.Select(h => h.Name)));
        }
        catch (System.Reflection.ReflectionTypeLoadException ex)
        {
            Log.Fatal(ex, "Assembly type load failed! Loader exceptions: {Errors}",
                string.Join("; ", ex.LoaderExceptions?.Select(e => e?.Message) ?? Array.Empty<string>()));
            throw;
        }

        builder.Services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(appAssembly);
            cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        });
    }

    // ─── FluentValidation ────────────────────────────────────────────────────
    builder.Services.AddValidatorsFromAssembly(
        typeof(EventWOS.Application.Auth.Validators.RequestOtpValidator).Assembly);

    // ─── JWT Authentication (RSA256) ─────────────────────────────────────────
    builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
    builder.Services.AddSingleton<IJwtService, JwtService>();

    var jwtSection = builder.Configuration.GetSection(JwtOptions.SectionName);
    var publicKeyBase64 = jwtSection["PublicKey"];

    RSA? rsaPublic = null;
    if (!string.IsNullOrWhiteSpace(publicKeyBase64)
        && !publicKeyBase64.StartsWith("REPLACE_"))
    {
        rsaPublic = RSA.Create();
        try
        {
            rsaPublic.ImportRSAPublicKey(Convert.FromBase64String(publicKeyBase64.Trim()), out _);
            Log.Information("RSA public key loaded successfully.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to import RSA public key — JWT auth will be disabled.");
            rsaPublic = null;
        }
    }
    else
    {
        Log.Warning("Jwt__PublicKey not configured — JWT authentication disabled.");
    }

    var authBuilder = builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme);
    if (rsaPublic is not null)
    {
        authBuilder.AddJwtBearer(opts =>
        {
            opts.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer    = true,
                ValidIssuer       = jwtSection["Issuer"],
                ValidateAudience  = true,
                ValidAudience     = jwtSection["Audience"],
                ValidateLifetime  = true,
                IssuerSigningKey  = new RsaSecurityKey(rsaPublic),
                ValidAlgorithms   = new[] { SecurityAlgorithms.RsaSha256 },
                ClockSkew         = TimeSpan.Zero,
                // Prevent JwtSecurityTokenHandler from remapping "role" → ClaimTypes.Role URI
                RoleClaimType     = "role",
                NameClaimType     = "mobile"
            };
            opts.Events = new JwtBearerEvents
            {
                OnMessageReceived = ctx =>
                {
                    var token = ctx.Request.Query["access_token"];
                    if (!string.IsNullOrEmpty(token) &&
                        ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                        ctx.Token = token;
                    return Task.CompletedTask;
                },
                // Map MSAL failure types to a short reason code the Blazor client
                // can surface ('expired' vs 'revoked' vs 'inactive'). The actual
                // 401 header is written in OnChallenge below — we just stash it
                // here so the right value is available at challenge time.
                OnAuthenticationFailed = ctx =>
                {
                    // Be precise about WHY auth failed. Previously only genuine
                    // expiry was tagged and everything else fell through to the
                    // "expired" default below — so a bad signature, wrong
                    // issuer/audience or malformed token all told the user
                    // "your session expired", which sent debugging down
                    // completely the wrong path more than once.
                    ctx.HttpContext.Items["auth_fail_reason"] = ctx.Exception switch
                    {
                        Microsoft.IdentityModel.Tokens.SecurityTokenExpiredException => "expired",
                        Microsoft.IdentityModel.Tokens.SecurityTokenInvalidSignatureException => "invalid",
                        Microsoft.IdentityModel.Tokens.SecurityTokenInvalidIssuerException => "invalid",
                        Microsoft.IdentityModel.Tokens.SecurityTokenInvalidAudienceException => "invalid",
                        Microsoft.IdentityModel.Tokens.SecurityTokenMalformedException => "invalid",
                        null => "expired",
                        _ => "invalid"
                    };
                    return Task.CompletedTask;
                },
                OnChallenge = ctx =>
                {
                    // Default to 'expired' for unauthenticated requests — gives
                    // a sensible message even when no JWT was supplied at all
                    // (e.g. user came back hours later with no token in storage).
                    var reason = ctx.HttpContext.Items.TryGetValue("auth_fail_reason", out var r) && r is string rs
                        ? rs : "expired";
                    if (!ctx.Response.Headers.ContainsKey("X-Auth-Fail-Reason"))
                        ctx.Response.Headers.Append("X-Auth-Fail-Reason", reason);
                    return Task.CompletedTask;
                },
                // Enforce session revocation in real time: every request checks the DB
                // for the IsActive flag of the session referenced by the "session_id" claim.
                // If the session has been revoked, the token is rejected — effectively
                // an immediate logout for the user on their next API call (≤30s with
                // the polling refresh loop on the client).
                OnTokenValidated = async ctx =>
                {
                    var sidClaim = ctx.Principal?.FindFirst("session_id")?.Value;
                    if (string.IsNullOrEmpty(sidClaim) || !Guid.TryParse(sidClaim, out var sessionId))
                        return; // legacy / non-session tokens — allow

                    var db = ctx.HttpContext.RequestServices
                        .GetRequiredService<EventWOS.Application.Interfaces.IAppDbContext>();

                    // Session must still be active
                    var sessionActive = await db.UserSessions
                        .AsNoTracking()
                        .AnyAsync(us => us.SessionId == sessionId && us.IsActive,
                                  ctx.HttpContext.RequestAborted);

                    if (!sessionActive)
                    {
                        ctx.HttpContext.Items["auth_fail_reason"] = "revoked";
                        ctx.Fail("session_revoked");
                        return;
                    }

                    // Defense-in-depth: even if a session somehow remained active,
                    // a Suspended or Deactivated user must not be allowed through.
                    var subClaim = ctx.Principal?.FindFirst(
                        System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
                    if (Guid.TryParse(subClaim, out var userId))
                    {
                        var userOk = await db.Users
                            .AsNoTracking()
                            .AnyAsync(u => u.Id == userId
                                        && !u.IsDeleted
                                        && u.Status == EventWOS.Domain.Enums.UserStatus.Active,
                                      ctx.HttpContext.RequestAborted);

                        if (!userOk)
                        {
                            ctx.HttpContext.Items["auth_fail_reason"] = "inactive";
                            ctx.Fail("user_inactive");
                        }
                    }
                }
            };
        });
    }

    // ─── Permission policies ─────────────────────────────────────────────────
    // No per-permission AddPolicy list here on purpose. PermissionPolicyProvider
    // manufactures every "perm:<permission>" policy on demand straight from the
    // [Permission("...")] attribute, so adding a new permission to a controller
    // needs no second registration step.
    //
    // The 38 hand-written registrations that used to live here were exactly the
    // kind of list that rots: three separate features shipped with a missing
    // entry and 500'd in production on first request ("The AuthorizationPolicy
    // named 'perm:x' was not found") - most recently the whole files module,
    // which meant every upload and download, including crew ID proofs, was dead.
    // See PermissionPolicyProvider for the full story.
    builder.Services.AddAuthorization();
    builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
    builder.Services.AddSingleton<IAuthorizationHandler, PermissionHandler>();

    // ─── Application Services ─────────────────────────────────────────────────
    builder.Services.Configure<OtpOptions>(builder.Configuration.GetSection(OtpOptions.SectionName));

    // Public frontend URL for approval-flow links (welcome emails, SMS, etc.).
    // Pulls from AppUrls:BaseUrl in appsettings or AppUrls__BaseUrl env var.
    builder.Services.Configure<EventWOS.Application.Common.AppUrlOptions>(
        builder.Configuration.GetSection(EventWOS.Application.Common.AppUrlOptions.SectionName));

    builder.Services.AddScoped<IOtpService, OtpService>();
    builder.Services.AddScoped<IPermissionService, PermissionService>();
    builder.Services.AddScoped<IAuditLogger, AuditLogger>();
    builder.Services.AddScoped<EventWOS.Application.Common.ISmsProvider, EventWOS.Infrastructure.Auth.StubSmsProvider>();
    builder.Services.AddSingleton<EventWOS.Application.Auth.Interfaces.IPasswordHasher, EventWOS.Infrastructure.Auth.BCryptPasswordHasher>();

    // Reverse-geocoding for AttendanceRecord.LocationAddress via
    // OpenStreetMap Nominatim (see GeoLocationService.cs for the
    // usage-policy notes — 1 req/sec, identifying User-Agent, in-
    // process rate limiter + 24 h cache). Singleton is essential —
    // the singleton holds the static rate-limit state and cache.
    builder.Services.AddSingleton<
        EventWOS.Application.Attendance.Geo.IGeoLocationService,
        EventWOS.Infrastructure.Geo.GeoLocationService>();

    // ── Location & Geofencing: provider-agnostic place search / geocoding ───
    //    The application layer depends only on ILocationService; the concrete
    //    provider is selected here from configuration
    //    (LocationProvider:Provider). To move to Google Maps or Mappls, add the
    //    implementation in Infrastructure/Locations and a case below — no
    //    handler, controller or Blazor component changes.
    //
    //    Registered via AddHttpClient so we get the shared handler pool and
    //    correct socket recycling; IMemoryCache backs the search cache, which
    //    is what keeps us inside Nominatim's ~1 req/sec public-instance policy.
    //    Credentials stay server-side: Blazor calls /api/v1/locations/*, never
    //    the provider directly.
    builder.Services.AddMemoryCache();
    builder.Services.Configure<EventWOS.Infrastructure.Locations.LocationOptions>(
        builder.Configuration.GetSection(
            EventWOS.Infrastructure.Locations.LocationOptions.SectionName));

    var locationProvider = builder.Configuration[
        $"{EventWOS.Infrastructure.Locations.LocationOptions.SectionName}:Provider"] ?? "Nominatim";

    switch (locationProvider.Trim().ToLowerInvariant())
    {
        case "nominatim":
            builder.Services.AddHttpClient<EventWOS.Application.Locations.ILocationService,
                                           EventWOS.Infrastructure.Locations.NominatimLocationService>();
            Log.Information("Location provider: Nominatim (OpenStreetMap).");
            break;

        default:
            // Fail fast and loudly. Silently falling back to a different
            // provider than the one configured would make venue search
            // mysteriously return different coordinates in production.
            throw new InvalidOperationException(
                $"Unknown LocationProvider:Provider value '{locationProvider}'. " +
                "Supported: Nominatim. Add the implementation in " +
                "Infrastructure/Locations and register it in Program.cs.");
    }

    // ── Email service: SendGrid if API key is present, otherwise dev stub (logs only).
    //    Lets the app boot fine in environments without SendGrid configured.
    var sendGridKey = builder.Configuration["SendGrid:ApiKey"]
                   ?? builder.Configuration["SENDGRID_API_KEY"];
    if (!string.IsNullOrWhiteSpace(sendGridKey))
    {
        builder.Services.AddHttpClient<EventWOS.Application.Common.IEmailService,
                                       EventWOS.Infrastructure.Email.SendGridEmailService>();
        Log.Information("Email: SendGridEmailService registered.");
    }
    else
    {
        builder.Services.AddSingleton<EventWOS.Application.Common.IEmailService,
                                      EventWOS.Infrastructure.Email.StubEmailService>();
        Log.Information("Email: SENDGRID_API_KEY not set — using StubEmailService (logs only).");
    }
    builder.Services.AddScoped<ICurrentUser, CurrentUser>();

    // ── WhatsApp: Meta Cloud API if credentials are present, otherwise dev stub (logs only).
    //    Same on/off pattern as SendGrid above — boots fine with no credentials configured.
    var whatsAppToken = builder.Configuration["WhatsApp:AccessToken"]
                      ?? builder.Configuration["WHATSAPP_ACCESS_TOKEN"];
    var whatsAppPhoneId = builder.Configuration["WhatsApp:PhoneNumberId"]
                        ?? builder.Configuration["WHATSAPP_PHONE_NUMBER_ID"];
    if (!string.IsNullOrWhiteSpace(whatsAppToken) && !string.IsNullOrWhiteSpace(whatsAppPhoneId))
    {
        builder.Services.AddHttpClient<EventWOS.Application.Common.IWhatsAppProvider,
                                       EventWOS.Infrastructure.Notifications.WhatsAppCloudApiProvider>();
        Log.Information("WhatsApp: WhatsAppCloudApiProvider registered.");
    }
    else
    {
        builder.Services.AddSingleton<EventWOS.Application.Common.IWhatsAppProvider,
                                      EventWOS.Infrastructure.Notifications.StubWhatsAppProvider>();
        Log.Information("WhatsApp: WHATSAPP_ACCESS_TOKEN/WHATSAPP_PHONE_NUMBER_ID not set — using StubWhatsAppProvider (logs only).");
    }

    // ── File & Image Storage module ────────────────────────────────────────
    // Provider selected purely by config — Storage:Provider = "Local" (default,
    // dev/MVP only) | "S3" (AWS S3 / Cloudflare R2 / MinIO) | "AzureBlob".
    // Business/handler code depends only on IFileStorage — swapping providers
    // here is the ONLY change needed to go from dev to production storage.
    var storageProvider = builder.Configuration["Storage:Provider"] ?? "Local";
    switch (storageProvider)
    {
        case "S3":
            builder.Services.AddSingleton<EventWOS.Application.Common.IFileStorage,
                                          EventWOS.Infrastructure.Storage.S3CompatibleFileStorage>();
            Log.Information("Storage: S3CompatibleFileStorage registered (AWS S3 / R2 / MinIO).");
            break;
        case "AzureBlob":
            builder.Services.AddSingleton<EventWOS.Application.Common.IFileStorage,
                                          EventWOS.Infrastructure.Storage.AzureBlobFileStorage>();
            Log.Information("Storage: AzureBlobFileStorage registered.");
            break;
        default:
            builder.Services.AddSingleton<EventWOS.Application.Common.IFileStorage,
                                          EventWOS.Infrastructure.Storage.LocalFileStorage>();
            Log.Warning("Storage: LocalFileStorage registered (dev/MVP only — NOT durable in production containers).");
            break;
    }
    builder.Services.AddSingleton<EventWOS.Application.Common.IImageProcessor,
                                  EventWOS.Infrastructure.Storage.ImageSharpProcessor>();
    // Scoped (not Singleton) - it depends on IAppDbContext, which is scoped per-request.
    builder.Services.AddScoped<EventWOS.Application.Files.IFileUploadStorer,
                                EventWOS.Application.Files.FileUploadStorer>();
    builder.Services.AddHttpContextAccessor();

    // ─── API Versioning ───────────────────────────────────────────────────────
    builder.Services.AddApiVersioning(opts =>
    {
        opts.DefaultApiVersion = new ApiVersion(1, 0);
        opts.AssumeDefaultVersionWhenUnspecified = true;
        opts.ReportApiVersions = true;
    }).AddApiExplorer(opts =>
    {
        opts.GroupNameFormat = "'v'VVV";
        opts.SubstituteApiVersionInUrl = true;
    });

    // ─── Controllers ──────────────────────────────────────────────────────────
    builder.Services.AddControllers()
        .AddJsonOptions(opts =>
        {
            opts.JsonSerializerOptions.PropertyNamingPolicy =
                System.Text.Json.JsonNamingPolicy.CamelCase;
            opts.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            opts.JsonSerializerOptions.DefaultIgnoreCondition =
                System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
        });

    // ─── Swagger ──────────────────────────────────────────────────────────────
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(opts =>
    {
        opts.SwaggerDoc("v1", new OpenApiInfo
        {
            Title   = "EventWOS API",
            Version = "v1",
            Description = "Event Workforce Operating System — Production API"
        });
        opts.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Name = "Authorization", Type = SecuritySchemeType.Http,
            Scheme = "Bearer", BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "Enter: Bearer {your_token}"
        });
        opts.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference
                        { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
                },
                Array.Empty<string>()
            }
        });
    });

    // ─── Rate Limiting ────────────────────────────────────────────────────────
    builder.Services.AddRateLimiter(opts =>
    {
        opts.OnRejected = async (ctx, ct) =>
        {
            ctx.HttpContext.Response.StatusCode = 429;
            await ctx.HttpContext.Response.WriteAsJsonAsync(
                new { success = false, message = "Too many requests." }, ct);
        };
        opts.AddPolicy("otp", httpCtx =>
            RateLimitPartition.GetFixedWindowLimiter(
                httpCtx.Connection.RemoteIpAddress?.ToString() ?? "anon",
                _ => new FixedWindowRateLimiterOptions
                {
                    Window = TimeSpan.FromMinutes(1),
                    PermitLimit = 5,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0
                }));
        opts.AddPolicy("api", httpCtx =>
            RateLimitPartition.GetFixedWindowLimiter(
                httpCtx.Connection.RemoteIpAddress?.ToString() ?? "anon",
                _ => new FixedWindowRateLimiterOptions
                {
                    Window = TimeSpan.FromMinutes(1),
                    PermitLimit = 120,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0
                }));
    });

    // ─── SignalR ──────────────────────────────────────────────────────────────
    builder.Services.AddScoped<EventWOS.Application.Interfaces.INotificationPusher, 
        EventWOS.Api.Hubs.SignalRNotificationPusher>();

    builder.Services.AddSignalR(opts =>
    {
        opts.EnableDetailedErrors = builder.Environment.IsDevelopment();
        opts.KeepAliveInterval = TimeSpan.FromSeconds(15);
    });

    // ─── CORS ─────────────────────────────────────────────────────────────────
    // AllowedOrigins can be overridden via CORS_ALLOWED_ORIGINS env var (comma-separated)
    var corsEnvOverride = Environment.GetEnvironmentVariable("CORS_ALLOWED_ORIGINS");
    var allowedOrigins = !string.IsNullOrWhiteSpace(corsEnvOverride)
        ? corsEnvOverride.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        : builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
    Log.Information("CORS allowed origins: {Origins}", string.Join(", ", allowedOrigins));

    builder.Services.AddCors(opts => opts.AddPolicy("BlazorPolicy", policy =>
        policy.WithOrigins(allowedOrigins)
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials()));

    // ─── Health Checks ────────────────────────────────────────────────────────
    var healthBuilder = builder.Services.AddHealthChecks();
    if (!string.IsNullOrWhiteSpace(pgConn))
    {
        try
        {
            healthBuilder.AddNpgSql(pgConn, name: "postgres");
            Log.Information("Postgres health check registered.");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Postgres health check registration skipped.");
        }
    }
    // NOTE: Skipping Redis health check — AspNetCore.HealthChecks.Redis opens a 
    // connection at registration time which can block/fail in containerized envs.

    // ════════════════════════════════════════════════════════════════════════
    Log.Information("Building application host...");
    var app = builder.Build();
    Log.Information("Application host built successfully.");
    // ════════════════════════════════════════════════════════════════════════

    // ─── Migrate + Seed ───────────────────────────────────────────────────────
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();


        // ─── Startup migration gate ─────────────────────────────────────────────
        // EF migrations no longer auto-apply on every deploy. A code-only deploy
        // (no schema change) now boots without touching the database at all - the
        // drift-rebuild + MigrateAsync block below only runs when explicitly armed.
        // Enable for exactly one deploy via Railway variable RUN_MIGRATIONS_ON_STARTUP=true
        // (or config key Database:RunMigrationsOnStartup), then disable it again.
        //
        // >>> Adding a new table/column? A migration alone will NOT reach prod <<<
        // because of this gate. You ALSO need a matching block in
        // emergencySchemaPatchSql below (it runs on every boot, gate or no gate).
        // See docs/DatabaseMigrations.md — this exact mistake already caused one
        // outage (indian_states, 2026-08-22).
        var gateArmedByConfig = string.Equals(
            Environment.GetEnvironmentVariable("RUN_MIGRATIONS_ON_STARTUP")
                ?? app.Configuration["Database:RunMigrationsOnStartup"],
            "true", StringComparison.OrdinalIgnoreCase);

        // ─── Auto-arm on a virgin database ──────────────────────────────────
        // The gate exists to stop unattended schema changes on a database that
        // holds live data. A database with ZERO applied migrations holds no live
        // data by definition, so the gate protects nothing there -- it just
        // leaves the app serving 500s until a human notices and flips a Railway
        // variable. That is exactly what happened on 2026-08-23: the database was
        // reset, every migration stayed pending, and login was down until the
        // variable was set by hand.
        //
        // So: nothing applied yet => migrate without being asked. Any database
        // with history is still gated and needs the variable, unchanged.
        var appliedBefore = (await db.Database.GetAppliedMigrationsAsync()).Count();
        var autoArmedForEmptyDb = appliedBefore == 0 && !gateArmedByConfig;
        if (autoArmedForEmptyDb)
            Log.Warning("Database has no applied migrations -> treating it as a fresh install and "
                      + "auto-applying the full migration set. The startup gate is bypassed on purpose "
                      + "here: an unmigrated database has no data to protect.");

        var runMigrationsOnStartup = gateArmedByConfig || autoArmedForEmptyDb;

        if (!runMigrationsOnStartup)
        {
            var pendingCheck = (await db.Database.GetPendingMigrationsAsync()).ToList();
            BuildInfo.MigrationGateArmed = false;
            BuildInfo.MigrationsPending  = pendingCheck.Count;
            BuildInfo.MigrationsApplied  = (await db.Database.GetAppliedMigrationsAsync()).Count();
            if (pendingCheck.Count > 0)
            {
                Log.Warning("SKIPPING EF migrations on startup (RUN_MIGRATIONS_ON_STARTUP is not 'true'). " +
                    "{Count} migration(s) pending, starting with {First}. Set RUN_MIGRATIONS_ON_STARTUP=true " +
                    "for one deploy to apply them, then unset it.",
                    pendingCheck.Count, pendingCheck[0]);
            }
            else
            {
                Log.Information("EF migrations up to date ({Count} applied). Startup auto-migrate is disabled.",
                    BuildInfo.MigrationsApplied);
            }
        }
        else
        {
            // ─── Schema / migration-history drift guard (self-healing) ────────────
            // Failure mode this fixes: the database was wiped (tables DROPPED) but the
            // __EFMigrationsHistory table survived. MigrateAsync() then sees every
            // migration as already applied, does nothing, cheerfully logs
            // "Migrations complete." - and the app runs against an EMPTY schema, so
            // every request dies with: 42P01: relation "users" does not exist
            // (which is exactly what the login screen was showing).
            //
            // Detection: the core `users` table is missing while migration history
            // claims migrations were applied.
            // Recovery: reset the public schema so the full migration set re-applies
            // from scratch below. Safe by construction - if `users` is missing the
            // database is already unusable and holds no reachable data (every table
            // is rooted in users), and the seeder recreates the admin account.
            var rebuildOnDrift = !string.Equals(
                app.Configuration["Database:RebuildSchemaOnDrift"], "false",
                StringComparison.OrdinalIgnoreCase);
            try
            {
                var driftConn = db.Database.GetDbConnection();
                if (driftConn.State != System.Data.ConnectionState.Open)
                    await driftConn.OpenAsync();

                bool usersTableExists;
                bool historyTableExists;
                await using (var checkCmd = driftConn.CreateCommand())
                {
                    checkCmd.CommandText =
                        @"SELECT to_regclass('public.users') IS NOT NULL,
                                 to_regclass('public.""__EFMigrationsHistory""') IS NOT NULL";
                    await using var driftReader = await checkCmd.ExecuteReaderAsync();
                    await driftReader.ReadAsync();
                    usersTableExists   = driftReader.GetBoolean(0);
                    historyTableExists = driftReader.GetBoolean(1);
                }

                long historyRows = 0;
                if (historyTableExists)
                {
                    await using var countCmd = driftConn.CreateCommand();
                    countCmd.CommandText = @"SELECT count(*) FROM ""__EFMigrationsHistory""";
                    historyRows = Convert.ToInt64(await countCmd.ExecuteScalarAsync());
                }

                Log.Information("Schema check -> users table present: {UsersExists} | migration history rows: {HistoryRows}",
                    usersTableExists, historyRows);

                if (!usersTableExists && historyRows > 0)
                {
                    if (!rebuildOnDrift)
                    {
                        Log.Error("SCHEMA DRIFT DETECTED: migration history has {HistoryRows} rows but the `users` table does not exist. "
                                + "Auto-rebuild is disabled (Database:RebuildSchemaOnDrift=false), so the app will start against an unusable schema.",
                            historyRows);
                    }
                    else
                    {
                        Log.Warning("SCHEMA DRIFT DETECTED: migration history claims {HistoryRows} applied migrations but the `users` table is "
                                  + "missing -> the database was wiped without clearing __EFMigrationsHistory. Resetting the public schema so "
                                  + "every migration re-applies from scratch.", historyRows);

                        await using (var resetCmd = driftConn.CreateCommand())
                        {
                            resetCmd.CommandText = @"
    DROP SCHEMA public CASCADE;
    CREATE SCHEMA public;
    GRANT ALL ON SCHEMA public TO CURRENT_USER;
    GRANT ALL ON SCHEMA public TO public;";
                            resetCmd.CommandTimeout = 120;
                            await resetCmd.ExecuteNonQueryAsync();
                        }

                        // gen_random_uuid() is built in on PostgreSQL 13+; create pgcrypto
                        // as a fallback for older servers. Failure is not fatal (it needs
                        // elevated privileges on some hosts and isn't needed on PG13+).
                        try
                        {
                            await using var extCmd = driftConn.CreateCommand();
                            extCmd.CommandText = "CREATE EXTENSION IF NOT EXISTS pgcrypto;";
                            await extCmd.ExecuteNonQueryAsync();
                        }
                        catch (Exception extEx)
                        {
                            Log.Warning("pgcrypto extension not created ({Message}) - fine on PostgreSQL 13+ where gen_random_uuid() is built in.",
                                extEx.Message);
                        }

                        Log.Warning("Public schema reset complete. EF migrations will now rebuild the full schema from scratch.");
                    }
                }
            }
            catch (Exception driftEx)
            {
                Log.Error("Schema drift check FAILED (non-fatal, continuing to migrations) -> {ExType}: {Message}",
                    driftEx.GetType().Name, driftEx.Message.Replace('\n', ' '));
            }

            Log.Information("Running EF Core migrations...");

            // Migration visibility check. EF only counts a migration class if it carries
            // BOTH [Migration("id")] AND [DbContext(typeof(AppDbContext))] - a missing
            // [DbContext] attribute silently drops it (that bug made all 21 invisible and
            // left MigrateAsync applying nothing on every boot).
            var allMigrations = db.Database.GetMigrations().ToList();
            var pendingMigrations = (await db.Database.GetPendingMigrationsAsync()).ToList();
            Log.Information("Migration discovery -> discovered: {Total} | pending: {Pending} | first pending: {First}",
                allMigrations.Count, pendingMigrations.Count,
                pendingMigrations.Count == 0 ? "(none)" : pendingMigrations[0]);

            if (allMigrations.Count == 0)
            {
                Log.Error("No migrations discovered. Check that every migration class has "
                        + "[Migration(\"id\")] AND [DbContext(typeof(AppDbContext))].");
            }

            // Non-fatal: a single bad migration must not put the container in a crash loop.
            // The emergency patch and seeder below are already non-fatal, and they can often
            // repair whatever a partial migration left behind.
            try
            {
                await db.Database.MigrateAsync();
                var appliedAfter = (await db.Database.GetAppliedMigrationsAsync()).ToList();
                BuildInfo.MigrationGateArmed = true;
                BuildInfo.MigrationsApplied  = appliedAfter.Count;
                BuildInfo.MigrationsPending  = 0;
                Log.Information("Migrations complete. Applied now: {Count} | latest: {Latest}",
                    appliedAfter.Count,
                    appliedAfter.Count == 0 ? "(none)" : appliedAfter[^1]);
            }
            catch (Exception migEx)
            {
                Log.Error("MIGRATIONS FAILED (non-fatal, startup continues) -> {ExType}: {Message}",
                    migEx.GetType().Name, migEx.Message.Replace('\n', ' '));
            }
        }

        // ── Emergency schema patch ─────────────────────────────────────────
        // Runs AFTER MigrateAsync so base tables always exist first (safe on a
        // brand-new/empty database too). Fully idempotent — safe on every startup.
        // Uses '' (doubled single-quote) for SQL string literals inside C# @"..." verbatim strings.
        // NON-FATAL by design: executed on a raw Npgsql connection (so EF Core
        // never dumps the whole script into the logs on failure, which used to
        // flood Railway's 500 logs/sec cap and hide the real error) and wrapped
        // in try/catch so a patch failure can never brick startup. The schema of
        // record is EF migrations, which already ran above.
        // ─── Is the emergency patch still needed on this database? ─────────
        // The patch and the migrations do NOT produce identical schemas -- the
        // patch creates several tables WITHOUT their foreign keys (verified by
        // schema-diffing two live databases). On a fully migrated database the
        // patch therefore adds nothing and can only re-introduce divergence, so
        // it is skipped: EF migrations are the schema of record.
        //
        // It still runs on a database that is behind (pending > 0), which is the
        // legacy delivery path this project has relied on while the gate is shut.
        var schemaIsMigrationManaged =
            BuildInfo.MigrationsApplied > 0 && BuildInfo.MigrationsPending == 0;

        if (schemaIsMigrationManaged)
        {
            BuildInfo.SchemaPatchStatus = "skipped";
            Log.Information("Skipping emergency schema patch -- database is fully migrated "
                          + "({Applied} migrations applied, none pending), so EF migrations own the schema.",
                BuildInfo.MigrationsApplied);
        }
        else
        {
        Log.Information("Applying emergency schema patch...");
        // Section bodies only -- no surrounding DO/BEGIN/END. The runner below
        // wraps and executes each ""=== name ==="" section as its own block.
        string emergencySchemaPatchSql = @"
    -- ═══ users ═══════════════════════════════════════════════════════════════
    ALTER TABLE users ADD COLUMN IF NOT EXISTS manager_id UUID;
    ALTER TABLE users ADD COLUMN IF NOT EXISTS device_id VARCHAR(255);
    ALTER TABLE users ADD COLUMN IF NOT EXISTS last_known_ip VARCHAR(45);
    ALTER TABLE users ADD COLUMN IF NOT EXISTS last_login_at TIMESTAMP;
    ALTER TABLE users ADD COLUMN IF NOT EXISTS failed_otp_attempts INT NOT NULL DEFAULT 0;
    ALTER TABLE users ADD COLUMN IF NOT EXISTS locked_until TIMESTAMP;
    ALTER TABLE users ADD COLUMN IF NOT EXISTS business_name VARCHAR(200);
    ALTER TABLE users ADD COLUMN IF NOT EXISTS referral_code VARCHAR(20);
    ALTER TABLE users ADD COLUMN IF NOT EXISTS rating NUMERIC(3,2);
    ALTER TABLE users ADD COLUMN IF NOT EXISTS events_completed INT NOT NULL DEFAULT 0;
    ALTER TABLE users ADD COLUMN IF NOT EXISTS vendor_id UUID;
    ALTER TABLE users ADD COLUMN IF NOT EXISTS discipline_score NUMERIC(5,2) NOT NULL DEFAULT 100.0;
    ALTER TABLE users ADD COLUMN IF NOT EXISTS events_attended INT NOT NULL DEFAULT 0;
    ALTER TABLE users ADD COLUMN IF NOT EXISTS created_by UUID;
    ALTER TABLE users ADD COLUMN IF NOT EXISTS updated_at TIMESTAMP;
    ALTER TABLE users ADD COLUMN IF NOT EXISTS updated_by UUID;
    ALTER TABLE users ADD COLUMN IF NOT EXISTS deleted_at TIMESTAMP;
    ALTER TABLE users ADD COLUMN IF NOT EXISTS deleted_by UUID;
    -- Direct-add invite tracking (Admin/Vendor adds a Crew/Vendor/Manager
    -- directly, skipping the approval queue) — see UpdateProfileCommand /
    -- CreateVendorCommand / CreateCrewCommand / CreateManagerCommand.
    ALTER TABLE users ADD COLUMN IF NOT EXISTS invited_by_user_id UUID;
    ALTER TABLE users ADD COLUMN IF NOT EXISTS profile_completed_at TIMESTAMP;
    CREATE UNIQUE INDEX IF NOT EXISTS ix_users_referral_code ON users(referral_code) WHERE referral_code IS NOT NULL;
    CREATE INDEX IF NOT EXISTS ix_users_vendor_id ON users(vendor_id);
    -- Email uniqueness (case-insensitive) across Vendor/Crew/Manager creation —
    -- previously only mobile was checked. Normalize existing rows first so
    -- legacy casing differences don't collide the new unique index.
    UPDATE users SET email = LOWER(TRIM(email)) WHERE email IS NOT NULL;
    DROP INDEX IF EXISTS ix_users_email;
    CREATE UNIQUE INDEX IF NOT EXISTS ix_users_email ON users(email) WHERE email IS NOT NULL;

    -- ═══ otp_requests ════════════════════════════════════════════════════════
    -- Case A: both hashed_otp (old) and otp_hash (blank, added by prev patch) exist
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='otp_requests' AND column_name='hashed_otp')
       AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='otp_requests' AND column_name='otp_hash') THEN
        ALTER TABLE otp_requests ALTER COLUMN otp_hash DROP NOT NULL;
        UPDATE otp_requests SET otp_hash = hashed_otp WHERE otp_hash IS NULL OR otp_hash = '';
        ALTER TABLE otp_requests DROP COLUMN hashed_otp;
        ALTER TABLE otp_requests ALTER COLUMN otp_hash SET NOT NULL; END IF;
    -- Case B: only hashed_otp exists (rename it)
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='otp_requests' AND column_name='hashed_otp')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='otp_requests' AND column_name='otp_hash') THEN
        ALTER TABLE otp_requests RENAME COLUMN hashed_otp TO otp_hash; END IF;
    ALTER TABLE otp_requests ADD COLUMN IF NOT EXISTS otp_hash VARCHAR(255) NOT NULL DEFAULT '';
    ALTER TABLE otp_requests ADD COLUMN IF NOT EXISTS user_agent VARCHAR(500);
    ALTER TABLE otp_requests ADD COLUMN IF NOT EXISTS ip_address VARCHAR(45);
    ALTER TABLE otp_requests ADD COLUMN IF NOT EXISTS attempts INT NOT NULL DEFAULT 0;
    ALTER TABLE otp_requests ADD COLUMN IF NOT EXISTS verified_at TIMESTAMP;
    ALTER TABLE otp_requests ADD COLUMN IF NOT EXISTS created_by UUID;
    ALTER TABLE otp_requests ADD COLUMN IF NOT EXISTS updated_at TIMESTAMP;
    ALTER TABLE otp_requests ADD COLUMN IF NOT EXISTS updated_by UUID;
    ALTER TABLE otp_requests ADD COLUMN IF NOT EXISTS deleted_at TIMESTAMP;
    ALTER TABLE otp_requests ADD COLUMN IF NOT EXISTS deleted_by UUID;

    -- ═══ refresh_tokens ══════════════════════════════════════════════════════
    -- CREATE, not just ALTER. This table is auth-critical and was previously
    -- only ever created by EF migrations -- which the RUN_MIGRATIONS_ON_STARTUP
    -- gate above means do NOT run on a normal deploy. On any database where it
    -- is absent, the ALTERs below abort the whole patch block (see the
    -- user_sessions note underneath).
    CREATE TABLE IF NOT EXISTS refresh_tokens (
        id          UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
        user_id     UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
        token_hash  VARCHAR(255) NOT NULL,
        device_id   VARCHAR(255),
        expires_at  TIMESTAMP NOT NULL,
        revoked_at  TIMESTAMP,
        replaced_by VARCHAR(255),
        ip_address  VARCHAR(45),
        created_at  TIMESTAMP NOT NULL DEFAULT now(),
        created_by  UUID, updated_at TIMESTAMP, updated_by UUID,
        is_deleted  BOOL NOT NULL DEFAULT false, deleted_at TIMESTAMP, deleted_by UUID
    );
    CREATE INDEX IF NOT EXISTS ix_refresh_tokens_user_id ON refresh_tokens(user_id);

    ALTER TABLE refresh_tokens ADD COLUMN IF NOT EXISTS device_id VARCHAR(255);
    ALTER TABLE refresh_tokens ADD COLUMN IF NOT EXISTS ip_address VARCHAR(45);
    ALTER TABLE refresh_tokens ADD COLUMN IF NOT EXISTS is_revoked BOOL NOT NULL DEFAULT false;
    ALTER TABLE refresh_tokens ADD COLUMN IF NOT EXISTS revoked_at TIMESTAMP;
    ALTER TABLE refresh_tokens ADD COLUMN IF NOT EXISTS replaced_by_token_hash VARCHAR(500);
    ALTER TABLE refresh_tokens ADD COLUMN IF NOT EXISTS revoke_reason VARCHAR(100);

    -- ═══ user_sessions ═══════════════════════════════════════════════════════
    -- OUTAGE 2026-08-23: login died with 42P01 relation ""user_sessions"" does
    -- not exist. The table only ever existed via EF migrations, which the
    -- startup gate skips, so after the database was rebuilt it was simply gone.
    -- Worse: this whole patch is ONE DO block, so the ALTERs below were the
    -- first statement to throw and Postgres discarded EVERY remaining statement
    -- in the block -- roughly a thousand lines of later patching (including the
    -- event_announcements tables) silently never ran, on every single boot.
    CREATE TABLE IF NOT EXISTS user_sessions (
        id                 UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
        user_id            UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
        session_id         UUID NOT NULL DEFAULT gen_random_uuid(),
        device_id          VARCHAR(255) NOT NULL DEFAULT '',
        device_name        VARCHAR(100) NOT NULL DEFAULT '',
        ip_address         VARCHAR(45)  NOT NULL DEFAULT '',
        user_agent         VARCHAR(500) NOT NULL DEFAULT '',
        is_active          BOOL NOT NULL DEFAULT true,
        last_activity_at   TIMESTAMP NOT NULL DEFAULT now(),
        terminated_at      TIMESTAMP,
        termination_reason VARCHAR(100),
        created_at         TIMESTAMP NOT NULL DEFAULT now(),
        created_by         UUID,
        updated_at         TIMESTAMP,
        updated_by         UUID,
        is_deleted         BOOL NOT NULL DEFAULT false,
        deleted_at         TIMESTAMP,
        deleted_by         UUID
    );
    CREATE UNIQUE INDEX IF NOT EXISTS ix_user_sessions_session_id ON user_sessions(session_id);
    CREATE INDEX IF NOT EXISTS ix_user_sessions_user_active ON user_sessions(user_id, is_active);

    ALTER TABLE user_sessions ADD COLUMN IF NOT EXISTS device_id VARCHAR(255);
    ALTER TABLE user_sessions ADD COLUMN IF NOT EXISTS device_name VARCHAR(200);
    ALTER TABLE user_sessions ADD COLUMN IF NOT EXISTS ip_address VARCHAR(45);
    ALTER TABLE user_sessions ADD COLUMN IF NOT EXISTS user_agent VARCHAR(500);
    ALTER TABLE user_sessions ADD COLUMN IF NOT EXISTS last_activity_at TIMESTAMP;
    ALTER TABLE user_sessions ADD COLUMN IF NOT EXISTS terminated_at TIMESTAMP;
    ALTER TABLE user_sessions ADD COLUMN IF NOT EXISTS termination_reason VARCHAR(100);

    -- ═══ vendor_crew_mappings ════════════════════════════════════════════════
    ALTER TABLE vendor_crew_mappings ADD COLUMN IF NOT EXISTS approved_by_manager_id UUID;
    ALTER TABLE vendor_crew_mappings ADD COLUMN IF NOT EXISTS is_active BOOL NOT NULL DEFAULT true;
    ALTER TABLE vendor_crew_mappings ADD COLUMN IF NOT EXISTS removed_at TIMESTAMP;
    ALTER TABLE vendor_crew_mappings ADD COLUMN IF NOT EXISTS notes VARCHAR(500);
    ALTER TABLE vendor_crew_mappings ADD COLUMN IF NOT EXISTS created_by UUID;
    ALTER TABLE vendor_crew_mappings ADD COLUMN IF NOT EXISTS updated_at TIMESTAMP;
    ALTER TABLE vendor_crew_mappings ADD COLUMN IF NOT EXISTS updated_by UUID;
    ALTER TABLE vendor_crew_mappings ADD COLUMN IF NOT EXISTS deleted_at TIMESTAMP;
    ALTER TABLE vendor_crew_mappings ADD COLUMN IF NOT EXISTS deleted_by UUID;
    -- ═══ events (Phase 2) ══════════════════════════════════════════════════════
    -- These tables are created fresh by migration 20260529; this block is a safety net.
    IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name='events') THEN
        CREATE TABLE events (
            id                  UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            title               VARCHAR(200) NOT NULL,
            description         VARCHAR(2000),
            venue               VARCHAR(300) NOT NULL,
            address             VARCHAR(500),
            start_at            TIMESTAMP   NOT NULL,
            end_at              TIMESTAMP   NOT NULL,
            status              INT         NOT NULL DEFAULT 0,
            max_crew            INT         NOT NULL DEFAULT 0,
            created_by_user_id  UUID        NOT NULL REFERENCES users(id),
            notes               VARCHAR(1000),
            created_at          TIMESTAMP   NOT NULL DEFAULT NOW(),
            created_by          UUID,
            updated_at          TIMESTAMP,
            updated_by          UUID,
            is_deleted          BOOLEAN     NOT NULL DEFAULT false,
            deleted_at          TIMESTAMP,
            deleted_by          UUID
        ); END IF;

    IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name='event_assignments') THEN
        CREATE TABLE event_assignments (
            id                  UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            event_id            UUID        NOT NULL REFERENCES events(id) ON DELETE CASCADE,
            crew_id             UUID        NOT NULL REFERENCES users(id),
            vendor_id           UUID        NOT NULL REFERENCES users(id),
            assigned_by_user_id UUID        NOT NULL REFERENCES users(id),
            status              INT         NOT NULL DEFAULT 0,
            notes               VARCHAR(1000),
            confirmed_at        TIMESTAMP,
            declined_at         TIMESTAMP,
            created_at          TIMESTAMP   NOT NULL DEFAULT NOW(),
            created_by          UUID,
            updated_at          TIMESTAMP,
            updated_by          UUID,
            is_deleted          BOOLEAN     NOT NULL DEFAULT false,
            deleted_at          TIMESTAMP,
            deleted_by          UUID
        ); END IF;

    IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name='attendance_records') THEN
        CREATE TABLE attendance_records (
            id                   UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
            assignment_id        UUID        NOT NULL REFERENCES event_assignments(id) ON DELETE CASCADE,
            event_id             UUID        NOT NULL REFERENCES events(id),
            crew_id              UUID        NOT NULL REFERENCES users(id),
            action               INT         NOT NULL,
            recorded_at          TIMESTAMP   NOT NULL DEFAULT NOW(),
            location             VARCHAR(500),
            recorded_by_user_id  VARCHAR(100),
            created_at           TIMESTAMP   NOT NULL DEFAULT NOW(),
            created_by           UUID,
            updated_at           TIMESTAMP,
            updated_by           UUID,
            is_deleted           BOOLEAN     NOT NULL DEFAULT false,
            deleted_at           TIMESTAMP,
            deleted_by           UUID
        ); END IF;

    -- ═══ payroll_batches ════════════════════════════════════════════════════
    IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'payroll_batches') THEN
        CREATE TABLE payroll_batches (
            id                  UUID          NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
            vendor_id           UUID          NOT NULL REFERENCES users(id)   ON DELETE RESTRICT,
            event_id            UUID          NOT NULL REFERENCES events(id)  ON DELETE RESTRICT,
            batch_ref           VARCHAR(100)  NOT NULL,
            status              VARCHAR(50)   NOT NULL DEFAULT 'Draft',
            total_amount        NUMERIC(14,2) NOT NULL DEFAULT 0,
            notes               TEXT,
            submitted_at        TIMESTAMPTZ,
            approved_at         TIMESTAMPTZ,
            disbursed_at        TIMESTAMPTZ,
            approved_by_user_id UUID,
            created_at          TIMESTAMPTZ   NOT NULL DEFAULT NOW(),
            created_by          UUID,
            updated_at          TIMESTAMPTZ,
            updated_by          UUID,
            is_deleted          BOOLEAN       NOT NULL DEFAULT false,
            deleted_at          TIMESTAMPTZ,
            deleted_by          UUID
        );
        CREATE UNIQUE INDEX ix_payroll_batches_batch_ref ON payroll_batches(batch_ref);
        CREATE INDEX ix_payroll_batches_vendor_id ON payroll_batches(vendor_id);
        CREATE INDEX ix_payroll_batches_event_id  ON payroll_batches(event_id);
        CREATE INDEX ix_payroll_batches_status    ON payroll_batches(status);
    ELSE
        -- Rename wrongly-named columns if they exist
        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='payroll_batches' AND column_name='created_date') THEN
            ALTER TABLE payroll_batches RENAME COLUMN created_date TO created_at; END IF;
        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='payroll_batches' AND column_name='updated_date') THEN
            ALTER TABLE payroll_batches RENAME COLUMN updated_date TO updated_at; END IF;
        -- Add missing columns
        ALTER TABLE payroll_batches ADD COLUMN IF NOT EXISTS created_at TIMESTAMPTZ NOT NULL DEFAULT NOW();
        ALTER TABLE payroll_batches ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ;
        ALTER TABLE payroll_batches ADD COLUMN IF NOT EXISTS updated_by UUID;
        ALTER TABLE payroll_batches ADD COLUMN IF NOT EXISTS deleted_at TIMESTAMPTZ;
        ALTER TABLE payroll_batches ADD COLUMN IF NOT EXISTS deleted_by UUID;
        -- Ensure updated_at is nullable (EF only sets it on Update, not Insert)
        ALTER TABLE payroll_batches ALTER COLUMN updated_at DROP NOT NULL;
        ALTER TABLE payroll_batches ALTER COLUMN updated_at DROP DEFAULT;
        -- Fix created_by column type: varchar -> uuid
        IF EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_name='payroll_batches' AND column_name='created_by'
              AND data_type='character varying'
        ) THEN
            ALTER TABLE payroll_batches
                ALTER COLUMN created_by TYPE UUID USING NULLIF(created_by, '')::UUID;
        END IF;
    END IF;

    -- ═══ crew_payments ═══════════════════════════════════════════════════════
    IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'crew_payments') THEN
        CREATE TABLE crew_payments (
            id               UUID          NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
            event_id         UUID          NOT NULL REFERENCES events(id)             ON DELETE RESTRICT,
            assignment_id    UUID          NOT NULL REFERENCES event_assignments(id)  ON DELETE RESTRICT,
            crew_id          UUID          NOT NULL REFERENCES users(id)              ON DELETE RESTRICT,
            vendor_id        UUID          NOT NULL REFERENCES users(id)              ON DELETE RESTRICT,
            agreed_amount    NUMERIC(12,2) NOT NULL,
            paid_amount      NUMERIC(12,2),
            status           VARCHAR(50)   NOT NULL DEFAULT 'Pending',
            method           VARCHAR(50),
            transaction_ref  VARCHAR(200),
            notes            TEXT,
            paid_at          TIMESTAMPTZ,
            payroll_batch_id UUID REFERENCES payroll_batches(id) ON DELETE SET NULL,
            created_at       TIMESTAMPTZ   NOT NULL DEFAULT NOW(),
            created_by       UUID,
            updated_at       TIMESTAMPTZ,
            updated_by       UUID,
            is_deleted       BOOLEAN       NOT NULL DEFAULT false,
            deleted_at       TIMESTAMPTZ,
            deleted_by       UUID
        );
        CREATE INDEX ix_crew_payments_event_id         ON crew_payments(event_id);
        CREATE INDEX ix_crew_payments_crew_id          ON crew_payments(crew_id);
        CREATE INDEX ix_crew_payments_vendor_id        ON crew_payments(vendor_id);
        CREATE INDEX ix_crew_payments_status           ON crew_payments(status);
        CREATE INDEX ix_crew_payments_payroll_batch_id ON crew_payments(payroll_batch_id);
    ELSE
        -- Rename wrongly-named columns if they exist
        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='crew_payments' AND column_name='created_date') THEN
            ALTER TABLE crew_payments RENAME COLUMN created_date TO created_at; END IF;
        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='crew_payments' AND column_name='updated_date') THEN
            ALTER TABLE crew_payments RENAME COLUMN updated_date TO updated_at; END IF;
        -- Add missing columns
        ALTER TABLE crew_payments ADD COLUMN IF NOT EXISTS created_at TIMESTAMPTZ NOT NULL DEFAULT NOW();
        ALTER TABLE crew_payments ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ;
        ALTER TABLE crew_payments ADD COLUMN IF NOT EXISTS updated_by UUID;
        ALTER TABLE crew_payments ADD COLUMN IF NOT EXISTS deleted_at TIMESTAMPTZ;
        ALTER TABLE crew_payments ADD COLUMN IF NOT EXISTS deleted_by UUID;
        -- Ensure updated_at is nullable (EF only sets it on Update, not Insert)
        ALTER TABLE crew_payments ALTER COLUMN updated_at DROP NOT NULL;
        ALTER TABLE crew_payments ALTER COLUMN updated_at DROP DEFAULT;
        -- Fix created_by / created_by column type: varchar -> uuid
        IF EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_name='crew_payments' AND column_name='created_by'
              AND data_type='character varying'
        ) THEN
            ALTER TABLE crew_payments
                ALTER COLUMN created_by TYPE UUID USING NULLIF(created_by, '')::UUID;
        END IF;
    END IF;

    -- ═══ event_assignments — 2-step approval columns ══════════════════════════
    ALTER TABLE event_assignments ADD COLUMN IF NOT EXISTS crew_responded_at TIMESTAMPTZ;
    ALTER TABLE event_assignments ADD COLUMN IF NOT EXISTS vendor_reviewed_at TIMESTAMPTZ;
    ALTER TABLE event_assignments ADD COLUMN IF NOT EXISTS manager_reviewed_at TIMESTAMPTZ;
    ALTER TABLE event_assignments ADD COLUMN IF NOT EXISTS rejection_reason TEXT;
    ALTER TABLE event_assignments ADD COLUMN IF NOT EXISTS rejected_by_user_id UUID;

    -- status index for manager queue
    IF NOT EXISTS (SELECT 1 FROM pg_indexes WHERE tablename='event_assignments' AND indexname='ix_event_assignments_status') THEN
        CREATE INDEX ix_event_assignments_status ON event_assignments(status); END IF;

    -- ═══ crew rating fields ══════════════════════════════════════════════════
    ALTER TABLE users ADD COLUMN IF NOT EXISTS crew_rating NUMERIC(4,2);
    ALTER TABLE users ADD COLUMN IF NOT EXISTS crew_rating_count INT NOT NULL DEFAULT 0;

    -- ═══ per-assignment vendor rating ════════════════════════════════════════
    ALTER TABLE event_assignments ADD COLUMN IF NOT EXISTS vendor_rating NUMERIC(3,1);
    ALTER TABLE event_assignments ADD COLUMN IF NOT EXISTS rated_at TIMESTAMPTZ;

    -- ═══ 3-mode assignments: vendor_id AND crew_id both nullable ═════════════
    IF EXISTS (SELECT 1 FROM information_schema.columns
                WHERE table_name='event_assignments' AND column_name='vendor_id' AND is_nullable='NO') THEN
        ALTER TABLE event_assignments ALTER COLUMN vendor_id DROP NOT NULL;
        RAISE NOTICE 'event_assignments.vendor_id is now nullable';
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns
                WHERE table_name='event_assignments' AND column_name='crew_id' AND is_nullable='NO') THEN
        ALTER TABLE event_assignments ALTER COLUMN crew_id DROP NOT NULL;
        RAISE NOTICE 'event_assignments.crew_id is now nullable';
    END IF;


    -- ═══ crew_payments — acknowledgment columns (2026-06-03) ═════════════════
    ALTER TABLE crew_payments ADD COLUMN IF NOT EXISTS crew_acknowledgment TEXT NOT NULL DEFAULT 'None';
    ALTER TABLE crew_payments ADD COLUMN IF NOT EXISTS acknowledged_at TIMESTAMPTZ;
    ALTER TABLE crew_payments ADD COLUMN IF NOT EXISTS acknowledgment_note VARCHAR(500);

    -- ═══ crew_payments / payroll_batches — vendor_id nullable (2026-06-03) ═══
    IF EXISTS (SELECT 1 FROM information_schema.columns
               WHERE table_name='crew_payments' AND column_name='vendor_id' AND is_nullable='NO') THEN
        ALTER TABLE crew_payments ALTER COLUMN vendor_id DROP NOT NULL;
        RAISE NOTICE 'crew_payments.vendor_id is now nullable';
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns
               WHERE table_name='payroll_batches' AND column_name='vendor_id' AND is_nullable='NO') THEN
        ALTER TABLE payroll_batches ALTER COLUMN vendor_id DROP NOT NULL;
        RAISE NOTICE 'payroll_batches.vendor_id is now nullable';
    END IF;

    -- ═══ One-time data fix (2026-06-04) ══════════════════════════════════════
    -- Old vendor-batch logic split the vendor total evenly across crew, exposing
    -- per-crew amounts the vendor hadn't actually decided yet. Zero those rows
    -- back out so the vendor can set the real cut on the Vendor Payments page.
    --
    -- Safe & idempotent — only touches rows that still match the buggy signature
    -- (approved, vendor-mediated, has agreed amount, not paid). Real paid rows
    -- and the batch-level totals are left untouched.
    IF EXISTS (
        SELECT 1 FROM crew_payments
        WHERE status         = 'Approved'         -- enum stored as varchar
          AND vendor_id      IS NOT NULL
          AND agreed_amount  > 0
          AND paid_amount    IS NULL
          AND paid_at        IS NULL
    ) THEN
        UPDATE crew_payments
        SET agreed_amount = 0
        WHERE status         = 'Approved'
          AND vendor_id      IS NOT NULL
          AND agreed_amount  > 0
          AND paid_amount    IS NULL
          AND paid_at        IS NULL;
        RAISE NOTICE 'Zeroed stale vendor-split agreed_amount on unpaid vendor rows';
    END IF;

    -- ═══ crew_groups + crew_group_members ════════════════════════════════════
    -- Safety net: if the formal migration didn't apply for any reason, ensure
    -- the Crew Groups tables exist so the vendor UI doesn't 500 on first use.
    IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'crew_groups') THEN
        CREATE TABLE crew_groups (
            id            UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
            vendor_id     UUID NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
            name          VARCHAR(120) NOT NULL,
            description   VARCHAR(500),
            created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
            created_by    UUID,
            updated_at    TIMESTAMPTZ,
            updated_by    UUID,
            is_deleted    BOOLEAN NOT NULL DEFAULT false,
            deleted_at    TIMESTAMPTZ,
            deleted_by    UUID
        );
        CREATE INDEX ix_crew_groups_vendor_id   ON crew_groups(vendor_id);
        CREATE INDEX ix_crew_groups_vendor_name ON crew_groups(vendor_id, name);
        RAISE NOTICE 'Created crew_groups table';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'crew_group_members') THEN
        CREATE TABLE crew_group_members (
            id             UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
            crew_group_id  UUID NOT NULL REFERENCES crew_groups(id) ON DELETE CASCADE,
            crew_id        UUID NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
            added_at       TIMESTAMPTZ NOT NULL,
            created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
            created_by     UUID,
            updated_at     TIMESTAMPTZ,
            updated_by     UUID,
            is_deleted     BOOLEAN NOT NULL DEFAULT false,
            deleted_at     TIMESTAMPTZ,
            deleted_by     UUID
        );
        CREATE INDEX ix_cgm_crew_group_id ON crew_group_members(crew_group_id);
        CREATE INDEX ix_cgm_crew_id       ON crew_group_members(crew_id);
        CREATE UNIQUE INDEX ux_cgm_group_crew_active
            ON crew_group_members(crew_group_id, crew_id)
            WHERE is_deleted = false;
        RAISE NOTICE 'Created crew_group_members table';
    END IF;

    -- ═══ event_assignments: attendance audit columns ═════════════════════════
    -- Belt-and-braces for the 20260606_AddAttendanceNote migration. Idempotent.
    ALTER TABLE event_assignments
        ADD COLUMN IF NOT EXISTS attendance_note            VARCHAR(500),
        ADD COLUMN IF NOT EXISTS attendance_note_at         TIMESTAMPTZ,
        ADD COLUMN IF NOT EXISTS attendance_note_by_user_id UUID;

    -- ═══ users: self-registration + password auth columns ═══════════════════
    -- Belt-and-braces for 20260608_AddSelfRegistration. Idempotent.
    -- Mirrors the formal migration so a partial / never-applied migration
    -- still results in a healthy schema on next API boot.
    ALTER TABLE users
        ADD COLUMN IF NOT EXISTS username                  VARCHAR(50),
        ADD COLUMN IF NOT EXISTS password_hash             VARCHAR(255),
        ADD COLUMN IF NOT EXISTS require_password_reset    BOOLEAN NOT NULL DEFAULT FALSE,
        ADD COLUMN IF NOT EXISTS failed_login_attempts     INT     NOT NULL DEFAULT 0,
        ADD COLUMN IF NOT EXISTS last_password_change_at   TIMESTAMPTZ,
        ADD COLUMN IF NOT EXISTS rejected_at               TIMESTAMPTZ,
        ADD COLUMN IF NOT EXISTS rejection_reason          VARCHAR(500),
        ADD COLUMN IF NOT EXISTS rejected_by_user_id       UUID,
        ADD COLUMN IF NOT EXISTS approved_at               TIMESTAMPTZ,
        ADD COLUMN IF NOT EXISTS approved_by_user_id       UUID,
        ADD COLUMN IF NOT EXISTS contact_person_name       VARCHAR(150),
        ADD COLUMN IF NOT EXISTS gst_number                VARCHAR(50),
        ADD COLUMN IF NOT EXISTS address                   VARCHAR(500),
        ADD COLUMN IF NOT EXISTS city                      VARCHAR(100),
        ADD COLUMN IF NOT EXISTS state                     VARCHAR(100),
        ADD COLUMN IF NOT EXISTS website                   VARCHAR(255),
        ADD COLUMN IF NOT EXISTS bio                       VARCHAR(2000),
        ADD COLUMN IF NOT EXISTS skills                    VARCHAR(500),
        ADD COLUMN IF NOT EXISTS experience_years          INT,
        ADD COLUMN IF NOT EXISTS referral_code_used        VARCHAR(20),
        ADD COLUMN IF NOT EXISTS date_of_birth              DATE,
        ADD COLUMN IF NOT EXISTS invite_message_template   VARCHAR(500);

    -- Backfill: grandfather existing accounts. Username = lowercase mobile.
    -- They'll be forced through the OTP-driven password-setup flow on next login.
    UPDATE users
       SET username = LOWER(mobile),
           require_password_reset = TRUE
     WHERE username IS NULL;

    CREATE UNIQUE INDEX IF NOT EXISTS ix_users_username
        ON users (username)
        WHERE username IS NOT NULL;
    CREATE INDEX IF NOT EXISTS ix_users_rejected_at
        ON users (rejected_at)
        WHERE rejected_at IS NOT NULL;

    -- ═══ scope_of_work catalog ═══════════════════════════════════════════════
    -- Belt-and-braces for 20260609_AddScopeOfWork. Idempotent. Phase A of the
    -- Scope-of-Work feature (admin-managed global list of work categories).
    IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'scope_of_work') THEN
        CREATE TABLE scope_of_work (
            id                  UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
            name                VARCHAR(80) NOT NULL,
            description         VARCHAR(500),
            created_by_user_id  UUID NOT NULL,
            created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
            created_by          UUID,
            updated_at          TIMESTAMPTZ,
            updated_by          UUID,
            is_deleted          BOOLEAN NOT NULL DEFAULT false,
            deleted_at          TIMESTAMPTZ,
            deleted_by          UUID
        );
        CREATE INDEX ix_scope_of_work_name ON scope_of_work (name);
        CREATE UNIQUE INDEX ux_scope_of_work_name_active
            ON scope_of_work (LOWER(name))
            WHERE is_deleted = false;
        RAISE NOTICE 'Created scope_of_work table';
    END IF;

    -- ═══ event_shifts (Phase B) ══════════════════════════════════════════════
    -- Belt-and-braces for 20260609_AddEventShifts. Idempotent — table CREATE,
    -- column ADD, indexes, ""General"" scope seed and backfill all reproduced
    -- here for the same reason every other table is: a partial migration
    -- leaves a healthy schema on next API boot.
    IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'event_shifts') THEN
        CREATE TABLE event_shifts (
            id                   UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
            event_id             UUID NOT NULL REFERENCES events(id)         ON DELETE CASCADE,
            scope_of_work_id     UUID NOT NULL REFERENCES scope_of_work(id)  ON DELETE RESTRICT,
            crew_count           INTEGER NOT NULL CHECK (crew_count >= 1),
            start_at             TIMESTAMPTZ NOT NULL,
            end_at               TIMESTAMPTZ,
            created_by_user_id   UUID NOT NULL,
            created_at           TIMESTAMPTZ NOT NULL DEFAULT now(),
            created_by           UUID,
            updated_at           TIMESTAMPTZ,
            updated_by           UUID,
            is_deleted           BOOLEAN NOT NULL DEFAULT false,
            deleted_at           TIMESTAMPTZ,
            deleted_by           UUID,
            CONSTRAINT ck_event_shifts_end_after_start
                CHECK (end_at IS NULL OR end_at > start_at)
        );
        CREATE INDEX ix_event_shifts_event_id        ON event_shifts (event_id);
        CREATE INDEX ix_event_shifts_scope_of_work_id ON event_shifts (scope_of_work_id);
        RAISE NOTICE 'Created event_shifts table';
    END IF;

    -- event_assignments.shift_id — nullable, then backfilled, then NOT NULL.
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
         WHERE table_name = 'event_assignments' AND column_name = 'shift_id') THEN
        ALTER TABLE event_assignments ADD COLUMN shift_id UUID;
        CREATE INDEX ix_event_assignments_shift_id ON event_assignments (shift_id);
        RAISE NOTICE 'Added event_assignments.shift_id (nullable)';
    END IF;

    -- Seed ""General"" scope + synthetic shifts. Block-scoped so the
    -- variables don't bleed.
    DECLARE
        v_general_id UUID;
        v_admin_id   UUID;
        v_orphans    INT;
    BEGIN
        SELECT id INTO v_general_id
          FROM scope_of_work
         WHERE LOWER(name) = 'general' AND is_deleted = false
         LIMIT 1;

        SELECT id INTO v_admin_id FROM users ORDER BY created_at ASC LIMIT 1;

        IF v_general_id IS NULL AND v_admin_id IS NOT NULL THEN
            INSERT INTO scope_of_work
                (id, name, description, created_by_user_id, created_at, is_deleted)
            VALUES
                (gen_random_uuid(), 'General',
                 'Default scope of work backfilled from pre-shift events. ' ||
                 'Edit the shift to assign a more specific category.',
                 v_admin_id, now(), false)
            RETURNING id INTO v_general_id;
            RAISE NOTICE 'Seeded ""General"" scope-of-work row';
        END IF;

        IF v_general_id IS NOT NULL THEN
            WITH events_needing_shift AS (
                SELECT e.id, e.start_at, e.end_at, GREATEST(e.max_crew, 1) AS cc,
                       COALESCE(e.created_by_user_id, v_admin_id) AS creator
                  FROM events e
                  LEFT JOIN event_shifts s
                        ON s.event_id = e.id AND s.is_deleted = false
                 WHERE s.id IS NULL
            ),
            inserted_shifts AS (
                INSERT INTO event_shifts
                    (id, event_id, scope_of_work_id, crew_count,
                     start_at, end_at, created_by_user_id, created_at, is_deleted)
                SELECT gen_random_uuid(), id, v_general_id, cc,
                       start_at, end_at, creator, now(), false
                  FROM events_needing_shift
                RETURNING id, event_id
            )
            UPDATE event_assignments a
               SET shift_id = ish.id
              FROM inserted_shifts ish
             WHERE a.event_id = ish.event_id
               AND a.shift_id IS NULL;
        END IF;

        SELECT COUNT(*) INTO v_orphans
          FROM event_assignments
         WHERE shift_id IS NULL AND is_deleted = false;

        IF v_orphans = 0 THEN
            BEGIN
                ALTER TABLE event_assignments ALTER COLUMN shift_id SET NOT NULL;
            EXCEPTION WHEN OTHERS THEN
                -- Already NOT NULL — fine.
                NULL;
            END;
        ELSE
            RAISE NOTICE 'Skipping shift_id NOT NULL — % orphans remain.', v_orphans;
        END IF;
    END;

    -- ═══ vendor_shift_allocations (Phase C) ════════════════════════════════════
    -- Quota table that gates how many crew a vendor can invite onto a given
    -- shift. No backfill — legacy events have NO vendor allocations and the
    -- assignment handlers fall back to the unallocated-vendor path for those
    -- (rows pre-Phase-C had no concept of vendor↔shift quotas anyway).
    IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'vendor_shift_allocations') THEN
        CREATE TABLE vendor_shift_allocations (
            id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
            shift_id           uuid NOT NULL REFERENCES event_shifts(id) ON DELETE CASCADE,
            vendor_id          uuid NOT NULL REFERENCES users(id)        ON DELETE RESTRICT,
            quota              integer NOT NULL CHECK (quota >= 1),
            created_by_user_id uuid NOT NULL,
            created_at         timestamptz NOT NULL DEFAULT now(),
            created_by         uuid,
            updated_at         timestamptz,
            updated_by         uuid,
            is_deleted         boolean NOT NULL DEFAULT false,
            deleted_at         timestamptz,
            deleted_by         uuid
        );
        CREATE UNIQUE INDEX ux_vendor_shift_allocations_shift_vendor_active
            ON vendor_shift_allocations (shift_id, vendor_id)
            WHERE is_deleted = false;
        CREATE INDEX ix_vendor_shift_allocations_vendor_id
            ON vendor_shift_allocations (vendor_id);
        RAISE NOTICE 'Created vendor_shift_allocations table';
    END IF;

    -- ═══ pending_checkins (Phase E — QR-verified check-in handshake) ═════════
    -- Crew mints a code with a 10-min TTL, vendor scans → server flips it to
    -- Consumed and writes the real attendance_records row in one transaction.
    -- No FK on assignment_id/crew_id (matches this project's convention of
    -- keeping soft-delete tables free of hard FKs so records survive vendor
    -- rejig without cascading nightmares). All three indexes are used by
    -- the app: code lookups (verify path), (assignment_id, status) for the
    -- already-live check and regenerate-cancels-prior, expires_at for
    -- future sweepers.
    IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'pending_checkins') THEN
        CREATE TABLE pending_checkins (
            id                    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
            assignment_id         uuid NOT NULL,
            crew_id               uuid NOT NULL,
            event_id              uuid NOT NULL,
            shift_id              uuid,
            code                  varchar(32) NOT NULL,
            expires_at            timestamptz NOT NULL,
            status                integer NOT NULL DEFAULT 0,
            consumed_by_vendor_id uuid,
            consumed_at           timestamptz,
            created_at            timestamptz NOT NULL DEFAULT now(),
            created_by            uuid,
            updated_at            timestamptz,
            updated_by            uuid,
            is_deleted            boolean NOT NULL DEFAULT false,
            deleted_at            timestamptz,
            deleted_by            uuid
        );
        CREATE INDEX ix_pending_checkins_code
            ON pending_checkins (code)
            WHERE is_deleted = false;
        CREATE INDEX ix_pending_checkins_assignment_status
            ON pending_checkins (assignment_id, status)
            WHERE is_deleted = false;
        CREATE INDEX ix_pending_checkins_expires
            ON pending_checkins (expires_at)
            WHERE is_deleted = false;
        RAISE NOTICE 'Created pending_checkins table';
    END IF;

    -- ═══ pending_checkins.crew_location (Phase G — crew-side location) ═════
    -- Product policy: attendance records must carry the CREW's coords at
    -- the moment they hit Check In, not the vendor's scanning phone.
    -- We now capture the fix on the crew device up front and store it
    -- on the pending row so the eventual verify-transaction can copy
    -- it into attendance_records instead of trusting the vendor payload.
    -- Idempotent: only adds the column if missing. Existing rows (which
    -- were minted before this field existed) get an empty string so the
    -- NOT NULL constraint holds — they'll never be redeemed in practice
    -- because their TTL was 10 min from creation. The constraint is
    -- deliberately NOT NULL because the domain contract is 'required';
    -- allowing NULL would let a future bug bypass the ctor guard.
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'pending_checkins' AND column_name = 'crew_location'
    ) THEN
        ALTER TABLE pending_checkins
            ADD COLUMN crew_location varchar(40) NOT NULL DEFAULT '';
        -- Drop the default after backfill so future INSERTs must supply it
        -- (matches the domain contract for a required field; the empty-string
        -- default is only there to satisfy NOT NULL on existing rows).
        ALTER TABLE pending_checkins ALTER COLUMN crew_location DROP DEFAULT;
        RAISE NOTICE 'Added crew_location column to pending_checkins';
    END IF;

    -- ═══ attendance_records — location split (Phase F) ══════════════════════
    -- Rationale: the single location column held one of:
    --   * lat,lng           — raw fix, no address label
    --   * lat,lng|Address   — coord + address (transient BigDataCloud era)
    --   * unavailable:<c>   — GPS refused/failed
    --   * NULL /            — no fix attempted (legacy rows)
    --
    -- Product decision: split into two typed columns:
    --   * location_address (VARCHAR 200)  — human-readable, e.g. Airoli, Navi Mumbai
    --   * location_coords  (VARCHAR 30)   — lat,lng for the map link
    --
    -- The old location column is KEPT (never dropped) so that any tool
    -- that queried it historically still works during transition. Only
    -- the domain model unmaps it — reads/writes from EF now flow to the
    -- two new columns. A separate one-shot backfill (see below) copies
    -- any legacy values into the split columns.
    ALTER TABLE attendance_records ADD COLUMN IF NOT EXISTS location_address VARCHAR(200);

    ALTER TABLE attendance_records ADD COLUMN IF NOT EXISTS location_coords VARCHAR(30);

    -- One-shot backfill from the legacy location column into
    -- location_coords / location_address. Only touches rows whose new
    -- columns are BOTH still null AND whose legacy column is non-empty
    -- and non-unavailable — so the patch is safe to re-run every
    -- startup (idempotent).
    IF EXISTS (SELECT 1 FROM information_schema.columns
               WHERE table_name='attendance_records' AND column_name='location') THEN
        -- (a) lat,lng|Address — split on the pipe.
        UPDATE attendance_records
           SET location_coords  = split_part(location, '|', 1),
               location_address = NULLIF(split_part(location, '|', 2), '')
         WHERE location_address IS NULL
           AND location_coords IS NULL
           AND location IS NOT NULL
           AND position('|' IN location) > 0;

        -- (b) pure lat,lng (matches num,num with optional decimals) —
        -- copy into coords, leave address NULL for a later geocode.
        UPDATE attendance_records
           SET location_coords = location
         WHERE location_address IS NULL
           AND location_coords IS NULL
           AND location ~ '^-?[0-9]+(\.[0-9]+)?,-?[0-9]+(\.[0-9]+)?$';
    END IF;

    -- Sample size behind users.rating. An average without its count invites
    -- trusting one glowing review as much as twenty.
    ALTER TABLE users ADD COLUMN IF NOT EXISTS rating_count INT NOT NULL DEFAULT 0;

    -- ═══ ratings ═════════════════════════════════════════════════════════
    -- Single source of truth for vendor + crew reputation, scored on two axes
    -- (performance, cooperation) and scoped to ONE event.
    --
    -- Replaces two lossy mechanisms that could not survive a correction:
    --   * users.rating was OVERWRITTEN per vendor, so rating a vendor for their
    --     second event destroyed the first. No history, no count, no average.
    --   * users.crew_rating folded each star into a running mean, discarding the
    --     individual scores -- so nothing could be revised or recomputed. That is
    --     the same incremental-cache pattern behind the max_crew drift bug fixed
    --     earlier in this file.
    -- Those two columns remain, but purely as CACHES recomputed from this table
    -- by full aggregation at the bottom of this block.
    IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'ratings') THEN
        CREATE TABLE ratings (
            id                      UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
            event_id                UUID NOT NULL,
            subject_user_id         UUID NOT NULL,
            -- 1 = Vendor, 2 = Crew. Stored, not inferred from users.role: a role
            -- can change, and a promotion must not re-file old ratings.
            subject_type            INT  NOT NULL,
            rater_user_id           UUID NOT NULL,
            performance             INT  NOT NULL,
            cooperation             INT  NOT NULL,
            comment                 VARCHAR(1000),
            -- Provenance for crew ratings; never part of uniqueness.
            assignment_id           UUID,
            rated_at                TIMESTAMPTZ NOT NULL DEFAULT now(),
            revised_at              TIMESTAMPTZ,
            -- Marks rows imported from the old single-star vendor_rating, where
            -- both axes hold the same number because the split never existed.
            is_legacy_single_score  BOOLEAN NOT NULL DEFAULT false,
            created_at              TIMESTAMPTZ NOT NULL DEFAULT now(),
            created_by              UUID,
            updated_at              TIMESTAMPTZ,
            updated_by              UUID,
            is_deleted              BOOLEAN NOT NULL DEFAULT false,
            deleted_at              TIMESTAMPTZ,
            deleted_by              UUID,
            CONSTRAINT ck_ratings_performance CHECK (performance BETWEEN 1 AND 5),
            CONSTRAINT ck_ratings_cooperation CHECK (cooperation BETWEEN 1 AND 5),
            CONSTRAINT ck_ratings_subject_type CHECK (subject_type IN (1, 2)),
            -- Self-rating would quietly inflate a real average, so the database
            -- refuses it rather than trusting every caller to remember.
            CONSTRAINT ck_ratings_no_self_rating CHECK (subject_user_id <> rater_user_id)
        );
    END IF;

    CREATE INDEX IF NOT EXISTS ix_ratings_event_id      ON ratings (event_id);
    CREATE INDEX IF NOT EXISTS ix_ratings_rater_user_id ON ratings (rater_user_id);
    -- Covers the only hot read: ""average for this person in this capacity"",
    -- which every dashboard and user list performs.
    CREATE INDEX IF NOT EXISTS ix_ratings_subject_user_id_subject_type
        ON ratings (subject_user_id, subject_type);

    -- ONE live rating per person per event. Partial on is_deleted so withdrawing
    -- a rating frees the slot instead of blocking it forever. This index is what
    -- makes ""re-rating is a revision"" true under concurrency -- two simultaneous
    -- checkouts would otherwise both pass a ""already rated?"" read and each insert.
    CREATE UNIQUE INDEX IF NOT EXISTS ux_ratings_event_subject_live
        ON ratings (event_id, subject_user_id, subject_type) WHERE is_deleted = false;

    -- ═══ ratings backfill from event_assignments.vendor_rating ════════════
    -- Those stars are real feedback vendors already gave; dropping them would
    -- reset every crew member's reputation to zero on deploy. Imported flagged
    -- as legacy because the old column never separated the two axes.
    --
    -- DISTINCT ON collapses a crew member rated on several shifts of the SAME
    -- event down to their most recent star, because an event must count once --
    -- the per-shift model was letting a three-shift crew member outvote a
    -- one-shift colleague three to one.
    --
    -- ON CONFLICT makes re-runs harmless, so this is safe on every boot.
    IF EXISTS (SELECT 1 FROM information_schema.columns
               WHERE table_name = 'event_assignments' AND column_name = 'vendor_rating') THEN
        INSERT INTO ratings (
            event_id, subject_user_id, subject_type, rater_user_id,
            performance, cooperation, assignment_id, rated_at, is_legacy_single_score)
        SELECT DISTINCT ON (ea.event_id, ea.crew_id)
            ea.event_id,
            ea.crew_id,
            2,                                  -- RatingSubjectType.Crew
            ea.vendor_id,
            GREATEST(1, LEAST(5, ROUND(ea.vendor_rating)::INT)),
            GREATEST(1, LEAST(5, ROUND(ea.vendor_rating)::INT)),
            ea.id,
            COALESCE(ea.rated_at, ea.updated_at, ea.created_at, now()),
            true
        FROM event_assignments ea
        WHERE ea.vendor_rating IS NOT NULL
          AND ea.crew_id       IS NOT NULL
          AND ea.vendor_id     IS NOT NULL
          AND ea.crew_id      <> ea.vendor_id
          AND COALESCE(ea.is_deleted, false) = false
        ORDER BY ea.event_id, ea.crew_id,
                 COALESCE(ea.rated_at, ea.updated_at, ea.created_at) DESC
        ON CONFLICT (event_id, subject_user_id, subject_type) WHERE is_deleted = false DO NOTHING;
    END IF;

    -- ═══ Reputation cache recompute ══════════════════════════════════════
    -- users.crew_rating / crew_rating_count / rating are DERIVED. Recomputed
    -- here by full aggregation rather than nudged, so they are correct by
    -- construction and self-heal on the next boot if anything ever drifts.
    -- The IS DISTINCT FROM guards make this a no-op once settled.
    UPDATE users u
       SET crew_rating       = agg.avg_score,
           crew_rating_count = agg.cnt
      FROM (SELECT subject_user_id,
                   ROUND(AVG((performance + cooperation) / 2.0), 2) AS avg_score,
                   COUNT(*)                                         AS cnt
              FROM ratings
             WHERE subject_type = 2 AND is_deleted = false
          GROUP BY subject_user_id) agg
     WHERE u.id = agg.subject_user_id
       AND (u.crew_rating       IS DISTINCT FROM agg.avg_score
         OR u.crew_rating_count IS DISTINCT FROM agg.cnt);

    UPDATE users u
       SET rating       = agg.avg_score,
           rating_count = agg.cnt
      FROM (SELECT subject_user_id,
                   ROUND(AVG((performance + cooperation) / 2.0), 2) AS avg_score,
                   COUNT(*)                                         AS cnt
              FROM ratings
             WHERE subject_type = 1 AND is_deleted = false
          GROUP BY subject_user_id) agg
     WHERE u.id = agg.subject_user_id
       AND (u.rating       IS DISTINCT FROM agg.avg_score
         OR u.rating_count IS DISTINCT FROM agg.cnt);

    -- ═══ Attendance location accuracy ════════════════════════════════════
    -- Mirrors migration 20260822224500_AddAttendanceLocationAccuracy.
    --
    -- Coordinates hide their own quality: a 10 m GPS fix and a 2 km
    -- cell-tower estimate are both six decimal places and both draw an
    -- equally confident pin. Recording the browser's accuracy figure next
    -- to the fix is what lets an auditor tell ""stood at the gate"" from
    -- ""was somewhere in the district"" - and it is what makes a geofence
    -- rejection defensible after the fact.
    --
    -- Deliberately NOT backfilled. The information was never captured for
    -- existing rows, and a guessed value would be worse than an honest
    -- NULL, so NULL here means ""unknown"", never ""accurate"".
    --
    -- pending_checkins gets the same column so the QR flow can carry the
    -- CREW device's accuracy across the handshake — the vendor's scanning
    -- phone supplies no position, so it must supply no accuracy either.
    ALTER TABLE attendance_records
        ADD COLUMN IF NOT EXISTS location_accuracy_meters INT NULL;

    -- Guarded because this whole patch is a single DO block: an ALTER against a
    -- missing table raises, and the raise would abandon every statement below it.
    -- attendance_records is created earlier in this script; pending_checkins is
    -- not, so it must be proven present before being altered.
    IF EXISTS (SELECT 1 FROM information_schema.tables
               WHERE table_name = 'pending_checkins') THEN
        ALTER TABLE pending_checkins
            ADD COLUMN IF NOT EXISTS crew_location_accuracy_meters INT NULL;
    END IF;

    -- Reject negatives and absurd values. 100 km is far past any real fix,
    -- so anything beyond it is a bug or a hostile client, not a bad GPS day.
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.constraint_column_usage
        WHERE  constraint_name = 'ck_attendance_records_accuracy_sane'
    ) THEN
        ALTER TABLE attendance_records
            ADD CONSTRAINT ck_attendance_records_accuracy_sane
            CHECK (location_accuracy_meters IS NULL
                   OR location_accuracy_meters BETWEEN 0 AND 100000);
    END IF;

    IF EXISTS (SELECT 1 FROM information_schema.tables
               WHERE table_name = 'pending_checkins')
       AND NOT EXISTS (
        SELECT 1 FROM information_schema.constraint_column_usage
        WHERE  constraint_name = 'ck_pending_checkins_accuracy_sane'
    ) THEN
        ALTER TABLE pending_checkins
            ADD CONSTRAINT ck_pending_checkins_accuracy_sane
            CHECK (crew_location_accuracy_meters IS NULL
                   OR crew_location_accuracy_meters BETWEEN 0 AND 100000);
    END IF;

    -- ═══ MaxCrew drift backfill ═══════════════════════════════════════════
    -- Historical bug in UpdateEventShiftCommand / AddEventShiftCommand:
    -- they recomputed events.max_crew via a SumAsync() that translated to
    -- server-side SELECT SUM(). That SUM only sees COMMITTED rows, not the
    -- change-tracker's in-memory mutation of the shift about to be saved,
    -- so every resize baked the STALE (pre-change) total into max_crew.
    -- Symptom in the UI: an event card showed 13/21 while its active
    -- shifts actually totalled 22 (KASHISH Pride: Box Office=5 + F&B=17).
    --
    -- Now that the handlers use in-memory Sum on the tracked collection,
    -- new resizes will store the correct total — but existing rows that
    -- drifted are still wrong. One-shot backfill: for every event whose
    -- max_crew doesn't match SUM(shift.crew_count) over its active
    -- (not-soft-deleted) shifts, correct it.
    --
    -- Idempotent: on subsequent boots there's nothing to fix so the
    -- UPDATE affects zero rows.
    IF EXISTS (SELECT 1 FROM information_schema.tables
                WHERE table_name = 'events')
       AND EXISTS (SELECT 1 FROM information_schema.tables
                WHERE table_name = 'event_shifts') THEN
        WITH shift_totals AS (
            SELECT event_id, COALESCE(SUM(crew_count), 0) AS total
              FROM event_shifts
             WHERE is_deleted = FALSE
             GROUP BY event_id
        )
        UPDATE events e
           SET max_crew = st.total
          FROM shift_totals st
         WHERE e.id = st.event_id
           AND e.max_crew IS DISTINCT FROM st.total;
    END IF;

    -- ═══ venues catalog + events.venue_id ═══════════════════════════════════
    -- Belt-and-braces for 20260821211500_AddVenues. Idempotent. Settings
    -- module: admin-managed venue catalog with structured address + lat/lng,
    -- so an Event can reuse a saved venue's location (Event.VenueId) instead
    -- of every event needing its own coordinates entered by hand.
    IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'venues') THEN
        CREATE TABLE venues (
            id                   UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
            name                 VARCHAR(120) NOT NULL,
            address_line1        VARCHAR(200) NOT NULL,
            address_line2        VARCHAR(200),
            city                 VARCHAR(200) NOT NULL,
            state                VARCHAR(100),
            postal_code          VARCHAR(20),
            country              VARCHAR(100),
            latitude             DOUBLE PRECISION,
            longitude            DOUBLE PRECISION,
            notes                VARCHAR(1000),
            created_by_user_id   UUID NOT NULL,
            created_at           TIMESTAMPTZ NOT NULL DEFAULT now(),
            created_by           UUID,
            updated_at           TIMESTAMPTZ,
            updated_by           UUID,
            is_deleted           BOOLEAN NOT NULL DEFAULT false,
            deleted_at           TIMESTAMPTZ,
            deleted_by           UUID
        );
        CREATE INDEX ix_venues_name ON venues (name);
        CREATE UNIQUE INDEX ux_venues_name_active
            ON venues (LOWER(name))
            WHERE is_deleted = false;
        RAISE NOTICE 'Created venues table';
    END IF;

    ALTER TABLE events ADD COLUMN IF NOT EXISTS venue_id UUID NULL;

    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE constraint_name = 'fk_events_venue_id'
    ) THEN
        ALTER TABLE events
            ADD CONSTRAINT fk_events_venue_id
            FOREIGN KEY (venue_id) REFERENCES venues(id) ON DELETE SET NULL;
    END IF;

    CREATE INDEX IF NOT EXISTS ix_events_venue_id ON events (venue_id);

    -- ═══ Location & Geofencing ══════════════════════════════════════════════
    -- Belt-and-braces for 20260822213000_AddVenueGeofencing. Idempotent.
    --
    -- venues.short_address: compact 'locality, city, state' label captured from
    -- provider search — display_name is far too long for a table row.
    ALTER TABLE venues ADD COLUMN IF NOT EXISTS short_address VARCHAR(200);

    -- Coordinates as numeric(9,6): fixed-precision decimal data, 6 dp ~ 11 cm.
    -- Guarded so the ALTER only runs once (it is not IF NOT EXISTS-able) —
    -- rewriting the column on every boot would be a pointless table lock.
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE  table_name = 'venues' AND column_name = 'latitude'
          AND  data_type <> 'numeric'
    ) THEN
        ALTER TABLE venues
            ALTER COLUMN latitude  TYPE NUMERIC(9,6) USING ROUND(latitude::numeric,  6),
            ALTER COLUMN longitude TYPE NUMERIC(9,6) USING ROUND(longitude::numeric, 6);
        RAISE NOTICE 'venues.latitude/longitude converted to numeric(9,6)';
    END IF;

    -- Attendance geofence config lives on the EVENT, not the venue: two events
    -- at the same venue routinely need different radii.
    ALTER TABLE events ADD COLUMN IF NOT EXISTS geo_fence_enabled       BOOLEAN NOT NULL DEFAULT FALSE;
    ALTER TABLE events ADD COLUMN IF NOT EXISTS geo_fence_radius_meters INT;

    IF NOT EXISTS (
        SELECT 1 FROM information_schema.constraint_column_usage
        WHERE  constraint_name = 'ck_events_geo_fence_radius'
    ) THEN
        ALTER TABLE events ADD CONSTRAINT ck_events_geo_fence_radius
            CHECK (
                (geo_fence_enabled = FALSE AND geo_fence_radius_meters IS NULL)
                OR (geo_fence_enabled = TRUE
                    AND geo_fence_radius_meters IS NOT NULL
                    AND geo_fence_radius_meters BETWEEN 20 AND 5000
                    AND venue_id IS NOT NULL)
            );
        RAISE NOTICE 'Added ck_events_geo_fence_radius';
    END IF;

    -- ═══ terms_and_conditions + terms_acceptances ═══════════════════════════
    -- Belt-and-braces for 20260821214500_AddTermsAndConditions. Idempotent.
    -- Settings module: versioned Terms & Conditions per audience (Vendor/
    -- Crew) plus an append-only acceptance audit trail.
    IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'terms_and_conditions') THEN
        CREATE TABLE terms_and_conditions (
            id           UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
            audience     VARCHAR(20) NOT NULL,
            version      INT NOT NULL,
            content      TEXT NOT NULL,
            created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
            created_by   UUID,
            updated_at   TIMESTAMPTZ,
            updated_by   UUID,
            is_deleted   BOOLEAN NOT NULL DEFAULT false,
            deleted_at   TIMESTAMPTZ,
            deleted_by   UUID
        );
        CREATE UNIQUE INDEX ux_terms_audience_version ON terms_and_conditions (audience, version);
        RAISE NOTICE 'Created terms_and_conditions table';
    END IF;

    -- Belt-and-braces for 20260821223000_WidenTermsContentColumn. Content is
    -- now rich-text HTML from a WYSIWYG editor, which needs more room than
    -- the original VARCHAR(20000). No-op once already TEXT.
    ALTER TABLE terms_and_conditions ALTER COLUMN content TYPE TEXT;

    IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'terms_acceptances') THEN
        CREATE TABLE terms_acceptances (
            id           UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
            user_id      UUID NOT NULL,
            audience     VARCHAR(20) NOT NULL,
            version      INT NOT NULL,
            created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
            created_by   UUID,
            updated_at   TIMESTAMPTZ,
            updated_by   UUID,
            is_deleted   BOOLEAN NOT NULL DEFAULT false,
            deleted_at   TIMESTAMPTZ,
            deleted_by   UUID
        );
        CREATE INDEX ix_terms_acceptances_user_audience_version ON terms_acceptances (user_id, audience, version);
        RAISE NOTICE 'Created terms_acceptances table';
    END IF;

    -- ═══ indian_states ══════════════════════════════════════════════════════
    -- Belt-and-braces for 20260821224500_AddIndianStatesTable. Idempotent.
    -- Reference data (28 states + 8 union territories) backing every ""State""
    -- dropdown in the app — Venue catalog, vendor/crew self-registration,
    -- profile editing, and the Event venue picker. Rows are inserted by
    -- DatabaseSeeder, which runs right after this patch.
    IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'indian_states') THEN
        CREATE TABLE indian_states (
            id                   UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
            name                 VARCHAR(100) NOT NULL,
            is_union_territory   BOOLEAN NOT NULL DEFAULT false,
            sort_order           INTEGER NOT NULL DEFAULT 0,
            created_at           TIMESTAMPTZ NOT NULL DEFAULT now(),
            created_by           UUID,
            updated_at           TIMESTAMPTZ,
            updated_by           UUID,
            is_deleted           BOOLEAN NOT NULL DEFAULT false,
            deleted_at           TIMESTAMPTZ,
            deleted_by           UUID
        );
        CREATE UNIQUE INDEX ux_indian_states_name ON indian_states (name);
        RAISE NOTICE 'Created indian_states table';
    END IF;

    -- ═══ event notifications (announcements) ════════════════════════════════
    -- Mirrors migration 20260822200000_AddEventAnnouncements. Duplicated here
    -- because the startup migration gate means a migration alone never reaches
    -- prod (see the comment on the gate above) — this block runs every boot.
    -- Admin/Manager broadcasts a rich-text message to an event's vendors
    -- and/or crew; attachments stay in object storage and are joined in.
    CREATE TABLE IF NOT EXISTS event_announcements (
        id                   UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
        event_id             UUID NOT NULL,
        audience             VARCHAR(20) NOT NULL,
        subject              VARCHAR(200) NOT NULL,
        body_html            TEXT NOT NULL,
        recipient_count      INT NOT NULL DEFAULT 0,
        whatsapp_sent_count  INT NOT NULL DEFAULT 0,
        created_at           TIMESTAMPTZ NOT NULL DEFAULT now(),
        created_by           UUID,
        updated_at           TIMESTAMPTZ,
        updated_by           UUID,
        is_deleted           BOOLEAN NOT NULL DEFAULT false,
        deleted_at           TIMESTAMPTZ,
        deleted_by           UUID
    );
    CREATE INDEX IF NOT EXISTS ix_event_announcements_event_created
        ON event_announcements (event_id, created_at);

    CREATE TABLE IF NOT EXISTS event_announcement_attachments (
        id                UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
        announcement_id   UUID NOT NULL,
        file_document_id  UUID NOT NULL,
        created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
        created_by        UUID,
        updated_at        TIMESTAMPTZ,
        updated_by        UUID,
        is_deleted        BOOLEAN NOT NULL DEFAULT false,
        deleted_at        TIMESTAMPTZ,
        deleted_by        UUID
    );
    CREATE INDEX IF NOT EXISTS ix_announcement_attachments_announcement
        ON event_announcement_attachments (announcement_id);
    CREATE UNIQUE INDEX IF NOT EXISTS ux_announcement_attachments_pair
        ON event_announcement_attachments (announcement_id, file_document_id);

    -- Read receipts. Absence of a row = unread, so nothing needs backfilling
    -- for a user who joins an event after a notification was sent.
    CREATE TABLE IF NOT EXISTS event_announcement_reads (
        id                UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
        announcement_id   UUID NOT NULL,
        user_id           UUID NOT NULL,
        read_at           TIMESTAMPTZ NOT NULL DEFAULT now(),
        created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
        created_by        UUID,
        updated_at        TIMESTAMPTZ,
        updated_by        UUID,
        is_deleted        BOOLEAN NOT NULL DEFAULT false,
        deleted_at        TIMESTAMPTZ,
        deleted_by        UUID
    );
    CREATE UNIQUE INDEX IF NOT EXISTS ux_announcement_reads_pair
        ON event_announcement_reads (announcement_id, user_id);

    -- ═══ notification platform ══════
    -- Mirrors migration 20260823190000_AddNotificationPlatform. Same reason as
    -- the block above: the startup migration gate means a migration alone never
    -- reaches prod, so the tables are created here on every boot.
    --
    -- Postgres is the source of truth for notification state. Business handlers
    -- write a notification plus an outbox row in the same transaction as the
    -- business data; a background worker calls AiSensy/SES/FCM afterwards.
    CREATE TABLE IF NOT EXISTS notifications (
        id                 UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
        recipient_user_id  UUID NOT NULL,
        event_id           UUID,
        actor_user_id      UUID,
        template_code      VARCHAR(60) NOT NULL,
        priority           VARCHAR(10) NOT NULL,
        status             VARCHAR(15) NOT NULL,
        data               JSONB NOT NULL DEFAULT '{}'::jsonb,
        idempotency_key    VARCHAR(200) NOT NULL,
        correlation_id     VARCHAR(100),
        read_at            TIMESTAMPTZ,
        created_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
        created_by         UUID,
        updated_at         TIMESTAMPTZ,
        updated_by         UUID,
        is_deleted         BOOLEAN NOT NULL DEFAULT false,
        deleted_at         TIMESTAMPTZ,
        deleted_by         UUID
    );

    -- The guard that actually prevents duplicate messages. Application-level
    -- checks race; a unique index does not.
    CREATE UNIQUE INDEX IF NOT EXISTS ux_notifications_idempotency_key
        ON notifications (idempotency_key);
    CREATE INDEX IF NOT EXISTS ix_notifications_recipient_created
        ON notifications (recipient_user_id, created_at);
    CREATE INDEX IF NOT EXISTS ix_notifications_event
        ON notifications (event_id);

    CREATE TABLE IF NOT EXISTS notification_deliveries (
        id                          UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
        notification_id             UUID NOT NULL,
        channel                     VARCHAR(15) NOT NULL,
        destination                 VARCHAR(320),
        provider                    VARCHAR(40) NOT NULL,
        template_version            INT NOT NULL DEFAULT 1,
        priority                    VARCHAR(10) NOT NULL,
        status                      VARCHAR(15) NOT NULL,
        provider_message_id         VARCHAR(200),
        provider_response_reference VARCHAR(200),
        attempt_count               INT NOT NULL DEFAULT 0,
        last_attempt_at             TIMESTAMPTZ,
        next_attempt_at             TIMESTAMPTZ,
        accepted_at                 TIMESTAMPTZ,
        delivered_at                TIMESTAMPTZ,
        read_at                     TIMESTAMPTZ,
        failed_at                   TIMESTAMPTZ,
        failure_reason              VARCHAR(500),
        locked_by                   VARCHAR(100),
        locked_at                   TIMESTAMPTZ,
        created_at                  TIMESTAMPTZ NOT NULL DEFAULT now(),
        created_by                  UUID,
        updated_at                  TIMESTAMPTZ,
        updated_by                  UUID,
        is_deleted                  BOOLEAN NOT NULL DEFAULT false,
        deleted_at                  TIMESTAMPTZ,
        deleted_by                  UUID
    );

    -- The FK is declared here as well as in the migration, on purpose: the
    -- patch has historically created tables WITHOUT their foreign keys, which
    -- let a patch-built database tolerate ordering bugs that a migrated
    -- database rejects (that mismatch caused the 2026-08-23 registration
    -- outage). Both paths should produce the same schema.
    DO $inner$
    BEGIN
        ALTER TABLE notification_deliveries
            ADD CONSTRAINT fk_notification_deliveries_notification
            FOREIGN KEY (notification_id) REFERENCES notifications (id) ON DELETE CASCADE;
    EXCEPTION WHEN duplicate_object THEN NULL;
    END $inner$;

    -- The worker claim query: due + pending, best priority first.
    CREATE INDEX IF NOT EXISTS ix_notification_deliveries_claim
        ON notification_deliveries (status, priority, next_attempt_at);
    -- Webhook correlation: providers identify a message only by their own id.
    CREATE INDEX IF NOT EXISTS ix_notification_deliveries_provider_message
        ON notification_deliveries (provider_message_id);
    CREATE INDEX IF NOT EXISTS ix_notification_deliveries_notification
        ON notification_deliveries (notification_id);
    -- One delivery per channel per notification: a replayed outbox row cannot
    -- produce a second WhatsApp message.
    CREATE UNIQUE INDEX IF NOT EXISTS ux_notification_deliveries_notification_channel
        ON notification_deliveries (notification_id, channel);

    CREATE TABLE IF NOT EXISTS notification_templates (
        id                    UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
        code                  VARCHAR(60) NOT NULL,
        channel               VARCHAR(15) NOT NULL,
        language              VARCHAR(10) NOT NULL DEFAULT 'en',
        subject               VARCHAR(300),
        body                  TEXT NOT NULL,
        provider_template_id  VARCHAR(200),
        version               INT NOT NULL DEFAULT 1,
        is_active             BOOLEAN NOT NULL DEFAULT true,
        created_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
        created_by            UUID,
        updated_at            TIMESTAMPTZ,
        updated_by            UUID,
        is_deleted            BOOLEAN NOT NULL DEFAULT false,
        deleted_at            TIMESTAMPTZ,
        deleted_by            UUID
    );

    CREATE UNIQUE INDEX IF NOT EXISTS ux_notification_templates_code_channel_lang
        ON notification_templates (code, channel, language);

    CREATE TABLE IF NOT EXISTS outbox_messages (
        id              UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
        aggregate_type  VARCHAR(60) NOT NULL,
        aggregate_id    UUID,
        message_type    VARCHAR(60) NOT NULL,
        payload         JSONB NOT NULL,
        status          VARCHAR(15) NOT NULL,
        attempt_count   INT NOT NULL DEFAULT 0,
        available_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
        locked_at       TIMESTAMPTZ,
        locked_by       VARCHAR(100),
        processed_at    TIMESTAMPTZ,
        last_error      VARCHAR(1000),
        correlation_id  VARCHAR(100),
        created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
        created_by      UUID,
        updated_at      TIMESTAMPTZ,
        updated_by      UUID,
        is_deleted      BOOLEAN NOT NULL DEFAULT false,
        deleted_at      TIMESTAMPTZ,
        deleted_by      UUID
    );

    CREATE INDEX IF NOT EXISTS ix_outbox_messages_status_available
        ON outbox_messages (status, available_at);
    CREATE INDEX IF NOT EXISTS ix_outbox_messages_status_locked
        ON outbox_messages (status, locked_at);

";
        // Split into sections and run each as its OWN block. Postgres aborts an
        // entire DO block on the first error, so while this was one giant block a
        // single bad statement silently discarded every statement after it -- that
        // is precisely how the whole back half of this patch (event_announcements
        // included) stopped reaching the database, unnoticed, on every boot. See
        // the user_sessions comment above for the outage that exposed it.
        // Now one bad section costs only that section, and the log names it.
        var patchMarker   = "\u2550\u2550\u2550";   // the rule character in the section headers
        var patchSections = new List<(string Name, System.Text.StringBuilder Sql)>();
        foreach (var patchLine in emergencySchemaPatchSql.Split('\n'))
        {
            if (patchLine.TrimStart().StartsWith("--", StringComparison.Ordinal)
                && patchLine.Contains(patchMarker, StringComparison.Ordinal))
            {
                var sectionName = patchLine.Trim().TrimStart('-').Trim().Trim('\u2550').Trim();
                patchSections.Add((
                    string.IsNullOrWhiteSpace(sectionName) ? $"section {patchSections.Count + 1}" : sectionName,
                    new System.Text.StringBuilder()));
            }
            else if (patchSections.Count > 0)
            {
                // StringBuilder is a reference, so appending through the tuple copy
                // still writes to the section's own buffer.
                patchSections[^1].Sql.AppendLine(patchLine);
            }
        }

        var patchApplied = 0;
        var patchFailed  = new List<string>();
        try
        {
            // Raw ADO.NET command on the underlying connection: EF Core does not
            // intercept (and therefore cannot log) commands it did not create.
            var patchConn = db.Database.GetDbConnection();
            if (patchConn.State != System.Data.ConnectionState.Open)
                await patchConn.OpenAsync();

            foreach (var (sectionName, sectionSql) in patchSections)
            {
                var sectionBody = sectionSql.ToString();
                if (string.IsNullOrWhiteSpace(sectionBody)) continue;

                try
                {
                    await using var patchCmd = patchConn.CreateCommand();
                    patchCmd.CommandText    = "DO $$\nBEGIN\n" + sectionBody + "\nEND $$;";
                    patchCmd.CommandTimeout = 180;
                    await patchCmd.ExecuteNonQueryAsync();
                    patchApplied++;
                }
                catch (Exception sectionEx)
                {
                    // Reflection keeps this free of a compile-time Npgsql dependency
                    // while still surfacing PostgresException.Where / .Detail, which
                    // name the exact statement that failed.
                    patchFailed.Add(sectionName);
                    var st = sectionEx.GetType();
                    Log.Error("Schema patch section FAILED (non-fatal) -> [{Section}] {ExType}: {Message} | Where={Where} | Detail={Detail}",
                        sectionName, st.Name, sectionEx.Message.Replace('\n', ' '),
                        (st.GetProperty("Where")?.GetValue(sectionEx) as string)?.Replace('\n', ' ') ?? "(none)",
                        (st.GetProperty("Detail")?.GetValue(sectionEx) as string)?.Replace('\n', ' ') ?? "(none)");
                }
            }

            // One summary line either way -- Railway caps logs at 500/sec, so
            // successful sections stay quiet and only the total is reported.
            BuildInfo.SchemaPatchApplied        = patchApplied;
            BuildInfo.SchemaPatchTotal          = patchSections.Count;
            BuildInfo.SchemaPatchFailedSections = patchFailed;
            BuildInfo.SchemaPatchStatus         = patchFailed.Count == 0 ? "complete" : "partial";

            if (patchFailed.Count == 0)
                Log.Information("Emergency schema patch complete ({Applied}/{Total} sections).",
                    patchApplied, patchSections.Count);
            else
                Log.Error("Emergency schema patch PARTIALLY applied ({Applied}/{Total} sections). Failed: {Failed}",
                    patchApplied, patchSections.Count, string.Join(", ", patchFailed));
        }
        catch (Exception patchEx)
        {
            // Connection-level failure: nothing was applied at all.
            BuildInfo.SchemaPatchStatus = "skipped";
            BuildInfo.SchemaPatchTotal  = patchSections.Count;
            var t2 = patchEx.GetType();
            Log.Error("Emergency schema patch SKIPPED entirely (non-fatal, startup continues) -> {ExType}: {Message} | Inner={Inner}",
                t2.Name,
                patchEx.Message.Replace('\n', ' '),
                patchEx.InnerException?.Message?.Replace('\n', ' ') ?? "(none)");
        }
        }   // end: emergency patch only runs when the database is behind

        // NON-FATAL: a seeding hiccup must not put the container into a
        // crash-restart loop (Railway then gives up and the whole deploy fails).
        // Boot anyway and log a concise, greppable error instead.
        Log.Information("Running seeder...");
        try
        {
            var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
            await seeder.SeedAsync();
            Log.Information("Seeding complete.");
        }
        catch (Exception seedEx)
        {
            var st = seedEx.GetType();
            var seedWhere = st.GetProperty("Where")?.GetValue(seedEx) as string;

            Log.Error("Seeding FAILED (non-fatal, startup continues) -> {ExType}: {Message} | Where={Where} | Inner={Inner}",
                st.Name,
                seedEx.Message.Replace('\n', ' '),
                string.IsNullOrWhiteSpace(seedWhere) ? "(none)" : seedWhere.Replace('\n', ' '),
                seedEx.InnerException?.Message?.Replace('\n', ' ') ?? "(none)");
        }

        // ─── One-time data repair: restore vendor anchor placeholders ─────────
        // History: earlier code deleted a vendor's placeholder row
        // (CrewId=null) once they staffed their first crew. If all that crew
        // was later rejected/declined, the vendor ended up with zero active
        // rows and the event vanished from their My Events.
        //
        // This restores the anchor for any (event, vendor) pair where the
        // vendor was historically attached but has no current placeholder
        // and the event is still active. Idempotent: skips pairs that already
        // have a placeholder row.
        try
        {
            var activeEventStatuses = new[]
            {
                EventWOS.Domain.Enums.EventStatus.Draft,
                EventWOS.Domain.Enums.EventStatus.Published,
                EventWOS.Domain.Enums.EventStatus.InProgress
            };

            // (eventId, vendorId) pairs that have EVER had a vendor attribution
            var historicalPairs = await db.EventAssignments
                .Where(a => a.VendorId != null)
                .Select(a => new { a.EventId, VendorId = a.VendorId!.Value })
                .Distinct()
                .ToListAsync();

            // (eventId, vendorId) pairs that already have a CrewId==null placeholder
            var existingPlaceholders = await db.EventAssignments
                .Where(a => a.CrewId == null && a.VendorId != null)
                .Select(a => new { a.EventId, VendorId = a.VendorId!.Value })
                .ToListAsync();
            var existingSet = new HashSet<(Guid, Guid)>(
                existingPlaceholders.Select(p => (p.EventId, p.VendorId)));

            // Only active events qualify
            var activeEventIds = await db.Events
                .Where(e => activeEventStatuses.Contains(e.Status))
                .Select(e => e.Id)
                .ToListAsync();
            var activeSet = new HashSet<Guid>(activeEventIds);

            var toRestore = historicalPairs
                .Where(p => activeSet.Contains(p.EventId)
                         && !existingSet.Contains((p.EventId, p.VendorId)))
                .ToList();

            if (toRestore.Count > 0)
            {
                foreach (var p in toRestore)
                {
                    db.EventAssignments.Add(new EventWOS.Domain.Entities.EventAssignment(
                        eventId:          p.EventId,
                        crewId:           null,
                        vendorId:         p.VendorId,
                        assignedByUserId: p.VendorId));
                }
                await db.SaveChangesAsync();
                Log.Information("Anchor repair: restored {Count} vendor placeholder row(s).", toRestore.Count);
            }
            else
            {
                Log.Information("Anchor repair: no placeholders needed restoration.");
            }
        }
        catch (Exception repairEx)
        {
            // Repair must never crash startup.
            Log.Warning(repairEx, "Anchor repair encountered an error and was skipped.");
        }

        // ─── One-time data repair: orphan vendor-routed payments ──────────────
        // History: before the auto-batch fix, a manager creating an ad-hoc
        // CrewPayment via "+ New Payment" for a vendor-routed crew would
        // leave the row with VendorId set but PayrollBatchId = null. The
        // vendor's payments page then showed the row stuck on "Awaiting
        // organiser disbursement" forever — no batch ever existed to
        // disburse. Spotted on "The MIX" for Sam Martin (Saly's crew).
        //
        // Repair rules:
        //   • AgreedAmount <= 0 → soft-delete the row. It was created as a
        //     placeholder with no real value; the manager will recreate it
        //     properly via the new auto-batched flow.
        //   • AgreedAmount > 0  → attach to an existing Draft batch for the
        //     same (vendor, event), or spin up a new one. Same fold-up
        //     behavior as CreateCrewPaymentHandler going forward.
        //
        // Idempotent: once a row has a PayrollBatchId, it's invisible to
        // this query on subsequent runs.
        try
        {
            var orphans = await db.CrewPayments
                .Where(p => p.VendorId != null
                         && p.PayrollBatchId == null
                         && p.Status != EventWOS.Domain.Enums.PaymentStatus.Rejected)
                .ToListAsync();

            if (orphans.Count == 0)
            {
                Log.Information("Orphan-payment repair: nothing to fix.");
            }
            else
            {
                int softDeleted = 0;
                int attachedExisting = 0;
                int attachedNew = 0;

                // Cache draft batches per (vendor, event) so multiple orphans
                // on the same pair fold into one batch.
                var draftCache = new Dictionary<(Guid VendorId, Guid EventId), EventWOS.Domain.Entities.PayrollBatch>();

                foreach (var pmt in orphans)
                {
                    // Case 1: junk row with no amount → soft-delete.
                    if (pmt.AgreedAmount <= 0m)
                    {
                        pmt.IsDeleted = true;
                        pmt.DeletedAt = DateTime.UtcNow;
                        softDeleted++;
                        continue;
                    }

                    // Case 2: real amount → fold into a draft batch.
                    var vid = pmt.VendorId!.Value;
                    var key = (vid, pmt.EventId);

                    if (!draftCache.TryGetValue(key, out var batch))
                    {
                        batch = await db.PayrollBatches
                            .Where(b => b.VendorId == vid
                                     && b.EventId  == pmt.EventId
                                     && b.Status   == EventWOS.Domain.Enums.PayrollStatus.Draft)
                            .OrderByDescending(b => b.CreatedAt)
                            .FirstOrDefaultAsync();

                        if (batch is null)
                        {
                            var batchRef = $"PAY-{pmt.EventId.ToString()[..8].ToUpper()}-{DateTime.UtcNow:yyyyMMddHHmm}-R";
                            batch = new EventWOS.Domain.Entities.PayrollBatch(
                                vid, pmt.EventId, batchRef, "Auto-recovered from orphan payment");
                            await db.PayrollBatches.AddAsync(batch);
                            await db.SaveChangesAsync(); // need batch.Id
                            attachedNew++;
                        }
                        else
                        {
                            attachedExisting++;
                        }
                        draftCache[key] = batch;
                    }
                    else
                    {
                        attachedExisting++;
                    }

                    pmt.AttachToPayroll(batch.Id);
                }

                await db.SaveChangesAsync();

                // Now recalc totals on every touched batch.
                foreach (var batch in draftCache.Values)
                {
                    var total = await db.CrewPayments
                        .Where(p => p.PayrollBatchId == batch.Id
                                 && p.Status != EventWOS.Domain.Enums.PaymentStatus.Rejected)
                        .SumAsync(p => p.AgreedAmount);
                    batch.SetTotal(total);
                }
                await db.SaveChangesAsync();

                Log.Information(
                    "Orphan-payment repair: soft-deleted {Deleted}, attached to existing batches {ExistingBatch}, attached to new batches {NewBatch}.",
                    softDeleted, attachedExisting, attachedNew);
            }
        }
        catch (Exception orphanEx)
        {
            Log.Warning(orphanEx, "Orphan-payment repair encountered an error and was skipped.");
        }
    }

    // ─── Middleware pipeline ──────────────────────────────────────────────────
    app.UseMiddleware<GlobalExceptionMiddleware>();
    app.UseSerilogRequestLogging();

    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "EventWOS v1");
        c.RoutePrefix = "swagger";
    });

    app.UseRateLimiter();
    app.UseCors("BlazorPolicy");
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapControllers();
    app.MapHub<NotificationHub>("/hubs/notifications");
    app.MapHealthChecks("/health");

    // Simple ping endpoint — no auth, no DB — for Railway health probing
    app.MapGet("/ping", () => Results.Ok(new { status = "alive", time = DateTime.UtcNow }));

    // Build + boot-schema visibility — no auth, no DB, so it can be checked from
    // anywhere. Answers the two questions that cost us the 2026-08-23 login
    // outage: which commit is this container actually running, and did the boot
    // schema patch apply cleanly? A 404 here means the container is older than
    // this endpoint and is definitely not running current code.
    app.MapGet("/version", () => Results.Ok(new
    {
        sha           = BuildInfo.ShortSha,
        fullSha       = BuildInfo.CommitSha,
        bootedAt      = BuildInfo.BootedAtUtc,
        uptimeSeconds = (long)(DateTime.UtcNow - BuildInfo.BootedAtUtc).TotalSeconds,
        migrations = new
        {
            gateArmed = BuildInfo.MigrationGateArmed,
            applied   = BuildInfo.MigrationsApplied,
            pending   = BuildInfo.MigrationsPending
        },
        schemaPatch = new
        {
            status         = BuildInfo.SchemaPatchStatus,
            applied        = BuildInfo.SchemaPatchApplied,
            total          = BuildInfo.SchemaPatchTotal,
            failedSections = BuildInfo.SchemaPatchFailedSections
        }
    }));

    Log.Information("All middleware configured. Starting Kestrel on {Url}...",
        Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "default");

    app.Run();

    Log.Information("Application shut down cleanly.");
}
catch (Exception ex)
{
    Log.Fatal(ex, "EventWOS API failed to start.");
    Console.Error.WriteLine($"[FATAL STARTUP ERROR] {ex.GetType().Name}: {ex.Message}");
    Console.Error.WriteLine(ex.ToString());
    throw; // re-throw so Railway marks the deploy as failed
}
finally
{
    Log.CloseAndFlush();
}
