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
            npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName);
            npgsql.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null);
        }));

    builder.Services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
    builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
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
                    if (ctx.Exception is Microsoft.IdentityModel.Tokens.SecurityTokenExpiredException)
                        ctx.HttpContext.Items["auth_fail_reason"] = "expired";
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

    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy("perm:attendance:read",   policy => policy.Requirements.Add(new PermissionRequirement("attendance:read")));
        options.AddPolicy("perm:attendance:write",  policy => policy.Requirements.Add(new PermissionRequirement("attendance:write")));
        options.AddPolicy("perm:attendance:verify", policy => policy.Requirements.Add(new PermissionRequirement("attendance:verify")));
        options.AddPolicy("perm:audit:read",        policy => policy.Requirements.Add(new PermissionRequirement("audit:read")));
        options.AddPolicy("perm:crew:approve",      policy => policy.Requirements.Add(new PermissionRequirement("crew:approve")));
        options.AddPolicy("perm:crew:invite",       policy => policy.Requirements.Add(new PermissionRequirement("crew:invite")));
        options.AddPolicy("perm:crew:read",         policy => policy.Requirements.Add(new PermissionRequirement("crew:read")));
        options.AddPolicy("perm:crew:write",        policy => policy.Requirements.Add(new PermissionRequirement("crew:write")));
        options.AddPolicy("perm:events:read",       policy => policy.Requirements.Add(new PermissionRequirement("events:read")));
        options.AddPolicy("perm:events:write",      policy => policy.Requirements.Add(new PermissionRequirement("events:write")));
        options.AddPolicy("perm:payments:read",     policy => policy.Requirements.Add(new PermissionRequirement("payments:read")));
        options.AddPolicy("perm:payments:write",    policy => policy.Requirements.Add(new PermissionRequirement("payments:write")));
        options.AddPolicy("perm:payments:self",     policy => policy.Requirements.Add(new PermissionRequirement("payments:self")));
        options.AddPolicy("perm:payments:disburse", policy => policy.Requirements.Add(new PermissionRequirement("payments:disburse")));
        options.AddPolicy("perm:payments:acknowledge", policy => policy.Requirements.Add(new PermissionRequirement("payments:acknowledge")));
        options.AddPolicy("perm:permissions:read",  policy => policy.Requirements.Add(new PermissionRequirement("permissions:read")));
        options.AddPolicy("perm:permissions:write", policy => policy.Requirements.Add(new PermissionRequirement("permissions:write")));
        options.AddPolicy("perm:profile:read",      policy => policy.Requirements.Add(new PermissionRequirement("profile:read")));
        options.AddPolicy("perm:profile:write",     policy => policy.Requirements.Add(new PermissionRequirement("profile:write")));
        options.AddPolicy("perm:reports:read",      policy => policy.Requirements.Add(new PermissionRequirement("reports:read")));
        options.AddPolicy("perm:roles:read",        policy => policy.Requirements.Add(new PermissionRequirement("roles:read")));
        options.AddPolicy("perm:roles:write",       policy => policy.Requirements.Add(new PermissionRequirement("roles:write")));
        // Phase A of Scope-of-Work feature — controller uses [Permission(...)]
        // which builds policies named "perm:<perm-string>". These two were
        // missed in the original Phase A commit; without them the controller
        // returns "AuthorizationPolicy not found" on first request.
        options.AddPolicy("perm:scope_of_work:read",  policy => policy.Requirements.Add(new PermissionRequirement("scope_of_work:read")));
        options.AddPolicy("perm:scope_of_work:write", policy => policy.Requirements.Add(new PermissionRequirement("scope_of_work:write")));
        options.AddPolicy("perm:sessions:read",     policy => policy.Requirements.Add(new PermissionRequirement("sessions:read")));
        options.AddPolicy("perm:sessions:revoke",   policy => policy.Requirements.Add(new PermissionRequirement("sessions:revoke")));
        options.AddPolicy("perm:users:delete",      policy => policy.Requirements.Add(new PermissionRequirement("users:delete")));
        options.AddPolicy("perm:users:read",        policy => policy.Requirements.Add(new PermissionRequirement("users:read")));
        options.AddPolicy("perm:users:status",      policy => policy.Requirements.Add(new PermissionRequirement("users:status")));
        options.AddPolicy("perm:users:write",       policy => policy.Requirements.Add(new PermissionRequirement("users:write")));
        options.AddPolicy("perm:vendor_allocations:read",  policy => policy.Requirements.Add(new PermissionRequirement("vendor_allocations:read")));
        options.AddPolicy("perm:vendor_allocations:write", policy => policy.Requirements.Add(new PermissionRequirement("vendor_allocations:write")));
        options.AddPolicy("perm:vendors:read",      policy => policy.Requirements.Add(new PermissionRequirement("vendors:read")));
        options.AddPolicy("perm:vendors:write",     policy => policy.Requirements.Add(new PermissionRequirement("vendors:write")));
    });
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
        Log.Information("Running EF Core migrations...");
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // ── Emergency schema patch ─────────────────────────────────────────
        // Runs BEFORE MigrateAsync. Fully idempotent — safe on every startup.
        // Uses '' (doubled single-quote) for SQL string literals inside C# @"..." verbatim strings.
        Log.Information("Applying emergency schema patch...");
        await db.Database.ExecuteSqlRawAsync(@"
DO $$
BEGIN
    -- ═══ users ═══════════════════════════════════════════════════════════════
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='manager_id') THEN
        ALTER TABLE users ADD COLUMN manager_id UUID; END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='device_id') THEN
        ALTER TABLE users ADD COLUMN device_id VARCHAR(255); END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='last_known_ip') THEN
        ALTER TABLE users ADD COLUMN last_known_ip VARCHAR(45); END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='last_login_at') THEN
        ALTER TABLE users ADD COLUMN last_login_at TIMESTAMP; END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='failed_otp_attempts') THEN
        ALTER TABLE users ADD COLUMN failed_otp_attempts INT NOT NULL DEFAULT 0; END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='locked_until') THEN
        ALTER TABLE users ADD COLUMN locked_until TIMESTAMP; END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='business_name') THEN
        ALTER TABLE users ADD COLUMN business_name VARCHAR(200); END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='referral_code') THEN
        ALTER TABLE users ADD COLUMN referral_code VARCHAR(20); END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='rating') THEN
        ALTER TABLE users ADD COLUMN rating NUMERIC(3,2); END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='events_completed') THEN
        ALTER TABLE users ADD COLUMN events_completed INT NOT NULL DEFAULT 0; END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='vendor_id') THEN
        ALTER TABLE users ADD COLUMN vendor_id UUID; END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='discipline_score') THEN
        ALTER TABLE users ADD COLUMN discipline_score NUMERIC(5,2) NOT NULL DEFAULT 100.0; END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='events_attended') THEN
        ALTER TABLE users ADD COLUMN events_attended INT NOT NULL DEFAULT 0; END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='created_by') THEN
        ALTER TABLE users ADD COLUMN created_by UUID; END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='updated_at') THEN
        ALTER TABLE users ADD COLUMN updated_at TIMESTAMP; END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='updated_by') THEN
        ALTER TABLE users ADD COLUMN updated_by UUID; END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='deleted_at') THEN
        ALTER TABLE users ADD COLUMN deleted_at TIMESTAMP; END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='deleted_by') THEN
        ALTER TABLE users ADD COLUMN deleted_by UUID; END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_indexes WHERE indexname='ix_users_referral_code') THEN
        CREATE UNIQUE INDEX ix_users_referral_code ON users(referral_code) WHERE referral_code IS NOT NULL; END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_indexes WHERE indexname='ix_users_vendor_id') THEN
        CREATE INDEX ix_users_vendor_id ON users(vendor_id); END IF;

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
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='otp_requests' AND column_name='otp_hash') THEN
        ALTER TABLE otp_requests ADD COLUMN otp_hash VARCHAR(255) NOT NULL DEFAULT ''; END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='otp_requests' AND column_name='user_agent') THEN
        ALTER TABLE otp_requests ADD COLUMN user_agent VARCHAR(500); END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='otp_requests' AND column_name='ip_address') THEN
        ALTER TABLE otp_requests ADD COLUMN ip_address VARCHAR(45); END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='otp_requests' AND column_name='attempts') THEN
        ALTER TABLE otp_requests ADD COLUMN attempts INT NOT NULL DEFAULT 0; END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='otp_requests' AND column_name='verified_at') THEN
        ALTER TABLE otp_requests ADD COLUMN verified_at TIMESTAMP; END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='otp_requests' AND column_name='created_by') THEN
        ALTER TABLE otp_requests ADD COLUMN created_by UUID; END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='otp_requests' AND column_name='updated_at') THEN
        ALTER TABLE otp_requests ADD COLUMN updated_at TIMESTAMP; END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='otp_requests' AND column_name='updated_by') THEN
        ALTER TABLE otp_requests ADD COLUMN updated_by UUID; END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='otp_requests' AND column_name='deleted_at') THEN
        ALTER TABLE otp_requests ADD COLUMN deleted_at TIMESTAMP; END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='otp_requests' AND column_name='deleted_by') THEN
        ALTER TABLE otp_requests ADD COLUMN deleted_by UUID; END IF;

    -- ═══ refresh_tokens ══════════════════════════════════════════════════════
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='refresh_tokens' AND column_name='device_id') THEN
        ALTER TABLE refresh_tokens ADD COLUMN device_id VARCHAR(255); END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='refresh_tokens' AND column_name='ip_address') THEN
        ALTER TABLE refresh_tokens ADD COLUMN ip_address VARCHAR(45); END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='refresh_tokens' AND column_name='is_revoked') THEN
        ALTER TABLE refresh_tokens ADD COLUMN is_revoked BOOL NOT NULL DEFAULT false; END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='refresh_tokens' AND column_name='revoked_at') THEN
        ALTER TABLE refresh_tokens ADD COLUMN revoked_at TIMESTAMP; END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='refresh_tokens' AND column_name='replaced_by_token_hash') THEN
        ALTER TABLE refresh_tokens ADD COLUMN replaced_by_token_hash VARCHAR(500); END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='refresh_tokens' AND column_name='revoke_reason') THEN
        ALTER TABLE refresh_tokens ADD COLUMN revoke_reason VARCHAR(100); END IF;

    -- ═══ user_sessions ═══════════════════════════════════════════════════════
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='user_sessions' AND column_name='device_id') THEN
        ALTER TABLE user_sessions ADD COLUMN device_id VARCHAR(255); END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='user_sessions' AND column_name='device_name') THEN
        ALTER TABLE user_sessions ADD COLUMN device_name VARCHAR(200); END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='user_sessions' AND column_name='ip_address') THEN
        ALTER TABLE user_sessions ADD COLUMN ip_address VARCHAR(45); END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='user_sessions' AND column_name='user_agent') THEN
        ALTER TABLE user_sessions ADD COLUMN user_agent VARCHAR(500); END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='user_sessions' AND column_name='last_activity_at') THEN
        ALTER TABLE user_sessions ADD COLUMN last_activity_at TIMESTAMP; END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='user_sessions' AND column_name='terminated_at') THEN
        ALTER TABLE user_sessions ADD COLUMN terminated_at TIMESTAMP; END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='user_sessions' AND column_name='termination_reason') THEN
        ALTER TABLE user_sessions ADD COLUMN termination_reason VARCHAR(100); END IF;

    -- ═══ vendor_crew_mappings ════════════════════════════════════════════════
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='vendor_crew_mappings' AND column_name='approved_by_manager_id') THEN
        ALTER TABLE vendor_crew_mappings ADD COLUMN approved_by_manager_id UUID; END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='vendor_crew_mappings' AND column_name='is_active') THEN
        ALTER TABLE vendor_crew_mappings ADD COLUMN is_active BOOL NOT NULL DEFAULT true; END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='vendor_crew_mappings' AND column_name='removed_at') THEN
        ALTER TABLE vendor_crew_mappings ADD COLUMN removed_at TIMESTAMP; END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='vendor_crew_mappings' AND column_name='notes') THEN
        ALTER TABLE vendor_crew_mappings ADD COLUMN notes VARCHAR(500); END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='vendor_crew_mappings' AND column_name='created_by') THEN
        ALTER TABLE vendor_crew_mappings ADD COLUMN created_by UUID; END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='vendor_crew_mappings' AND column_name='updated_at') THEN
        ALTER TABLE vendor_crew_mappings ADD COLUMN updated_at TIMESTAMP; END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='vendor_crew_mappings' AND column_name='updated_by') THEN
        ALTER TABLE vendor_crew_mappings ADD COLUMN updated_by UUID; END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='vendor_crew_mappings' AND column_name='deleted_at') THEN
        ALTER TABLE vendor_crew_mappings ADD COLUMN deleted_at TIMESTAMP; END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='vendor_crew_mappings' AND column_name='deleted_by') THEN
        ALTER TABLE vendor_crew_mappings ADD COLUMN deleted_by UUID; END IF;
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
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='payroll_batches' AND column_name='created_at') THEN
            ALTER TABLE payroll_batches ADD COLUMN created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(); END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='payroll_batches' AND column_name='updated_at') THEN
            ALTER TABLE payroll_batches ADD COLUMN updated_at TIMESTAMPTZ; END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='payroll_batches' AND column_name='updated_by') THEN
            ALTER TABLE payroll_batches ADD COLUMN updated_by UUID; END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='payroll_batches' AND column_name='deleted_at') THEN
            ALTER TABLE payroll_batches ADD COLUMN deleted_at TIMESTAMPTZ; END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='payroll_batches' AND column_name='deleted_by') THEN
            ALTER TABLE payroll_batches ADD COLUMN deleted_by UUID; END IF;
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
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='crew_payments' AND column_name='created_at') THEN
            ALTER TABLE crew_payments ADD COLUMN created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(); END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='crew_payments' AND column_name='updated_at') THEN
            ALTER TABLE crew_payments ADD COLUMN updated_at TIMESTAMPTZ; END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='crew_payments' AND column_name='updated_by') THEN
            ALTER TABLE crew_payments ADD COLUMN updated_by UUID; END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='crew_payments' AND column_name='deleted_at') THEN
            ALTER TABLE crew_payments ADD COLUMN deleted_at TIMESTAMPTZ; END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='crew_payments' AND column_name='deleted_by') THEN
            ALTER TABLE crew_payments ADD COLUMN deleted_by UUID; END IF;
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
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='event_assignments' AND column_name='crew_responded_at') THEN
        ALTER TABLE event_assignments ADD COLUMN crew_responded_at TIMESTAMPTZ; END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='event_assignments' AND column_name='vendor_reviewed_at') THEN
        ALTER TABLE event_assignments ADD COLUMN vendor_reviewed_at TIMESTAMPTZ; END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='event_assignments' AND column_name='manager_reviewed_at') THEN
        ALTER TABLE event_assignments ADD COLUMN manager_reviewed_at TIMESTAMPTZ; END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='event_assignments' AND column_name='rejection_reason') THEN
        ALTER TABLE event_assignments ADD COLUMN rejection_reason TEXT; END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='event_assignments' AND column_name='rejected_by_user_id') THEN
        ALTER TABLE event_assignments ADD COLUMN rejected_by_user_id UUID; END IF;

    -- status index for manager queue
    IF NOT EXISTS (SELECT 1 FROM pg_indexes WHERE tablename='event_assignments' AND indexname='ix_event_assignments_status') THEN
        CREATE INDEX ix_event_assignments_status ON event_assignments(status); END IF;

    -- ═══ crew rating fields ══════════════════════════════════════════════════
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='crew_rating') THEN
        ALTER TABLE users ADD COLUMN crew_rating NUMERIC(4,2); END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='crew_rating_count') THEN
        ALTER TABLE users ADD COLUMN crew_rating_count INT NOT NULL DEFAULT 0; END IF;

    -- ═══ per-assignment vendor rating ════════════════════════════════════════
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='event_assignments' AND column_name='vendor_rating') THEN
        ALTER TABLE event_assignments ADD COLUMN vendor_rating NUMERIC(3,1); END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='event_assignments' AND column_name='rated_at') THEN
        ALTER TABLE event_assignments ADD COLUMN rated_at TIMESTAMPTZ; END IF;

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
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_name='crew_payments' AND column_name='crew_acknowledgment') THEN
        ALTER TABLE crew_payments ADD COLUMN crew_acknowledgment TEXT NOT NULL DEFAULT 'None';
        RAISE NOTICE 'crew_payments.crew_acknowledgment added';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_name='crew_payments' AND column_name='acknowledged_at') THEN
        ALTER TABLE crew_payments ADD COLUMN acknowledged_at TIMESTAMPTZ;
        RAISE NOTICE 'crew_payments.acknowledged_at added';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_name='crew_payments' AND column_name='acknowledgment_note') THEN
        ALTER TABLE crew_payments ADD COLUMN acknowledgment_note VARCHAR(500);
        RAISE NOTICE 'crew_payments.acknowledgment_note added';
    END IF;

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
        ADD COLUMN IF NOT EXISTS referral_code_used        VARCHAR(20);

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
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_name='attendance_records' AND column_name='location_address') THEN
        ALTER TABLE attendance_records ADD COLUMN location_address VARCHAR(200);
        RAISE NOTICE 'Added attendance_records.location_address';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_name='attendance_records' AND column_name='location_coords') THEN
        ALTER TABLE attendance_records ADD COLUMN location_coords VARCHAR(30);
        RAISE NOTICE 'Added attendance_records.location_coords';
    END IF;

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

END $$;
");
        Log.Information("Emergency schema patch complete.");

        await db.Database.MigrateAsync();
        Log.Information("Migrations complete. Running seeder...");

        // ─── Optional: SEED_MODE=CleanReset ───────────────────────────────────
        // One-shot destructive wipe. Truncates all operational tables (events,
        // shifts, allocations, assignments, attendance, payments, payroll,
        // users, sessions, OTPs, audit logs, crew groups, vendor↔crew maps,
        // scopes of work, manager & user role permissions). Preserves the
        // essential RBAC catalog: roles, permissions, role_permissions.
        //
        // After a successful wipe, writes a marker row into role_permissions'
        // adjacent tracking so it self-disables on next boot even if the env
        // var is left on. Actual self-guard: a dedicated meta table.
        //
        // To use:
        //   1. Set SEED_MODE=CleanReset in Railway → API service → Variables
        //   2. Deploy (or restart the service)
        //   3. Watch logs for "CleanReset complete"
        //   4. Remove the env var (marker will also block re-runs)
        var seedMode = Environment.GetEnvironmentVariable("SEED_MODE");
        if (string.Equals(seedMode, "CleanReset", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning("SEED_MODE=CleanReset detected. Checking self-guard marker...");

            // Ensure marker table exists
            await db.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS clean_reset_history (
                    id SERIAL PRIMARY KEY,
                    executed_at TIMESTAMP NOT NULL DEFAULT NOW(),
                    note TEXT
                );");

            long alreadyRanCount;
            var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM clean_reset_history;";
                var scalar = await cmd.ExecuteScalarAsync();
                alreadyRanCount = scalar is null ? 0 : Convert.ToInt64(scalar);
            }

            if (alreadyRanCount > 0)
            {
                Log.Warning("CleanReset SKIPPED — marker exists ({Count} prior run(s)). " +
                            "Delete rows from clean_reset_history to allow another wipe.",
                            alreadyRanCount);
            }
            else
            {
                Log.Warning("CleanReset EXECUTING — this will wipe operational data.");

                // Order matters only where FKs would block; TRUNCATE ... CASCADE
                // and RESTART IDENTITY handle it. We list every operational
                // table explicitly so this is auditable and doesn't rely on
                // schema introspection.
                await db.Database.ExecuteSqlRawAsync(@"
                    TRUNCATE TABLE
                        crew_payments,
                        payroll_batches,
                        attendance_records,
                        pending_check_ins,
                        event_assignments,
                        vendor_shift_allocations,
                        event_shifts,
                        events,
                        crew_group_members,
                        crew_groups,
                        vendor_crew_mappings,
                        scopes_of_work,
                        manager_permissions,
                        user_role_permissions,
                        refresh_tokens,
                        user_sessions,
                        otp_requests,
                        audit_logs,
                        users
                    RESTART IDENTITY CASCADE;");

                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO clean_reset_history (note) VALUES ('SEED_MODE=CleanReset one-shot wipe');");

                Log.Warning("CleanReset complete. Seeder will now recreate admin +911234567890.");
            }
        }

        var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
        await seeder.SeedAsync();
        Log.Information("Seeding complete.");

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
