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

                    var isActive = await db.UserSessions
                        .AsNoTracking()
                        .AnyAsync(us => us.SessionId == sessionId && us.IsActive,
                                  ctx.HttpContext.RequestAborted);

                    if (!isActive)
                    {
                        ctx.Fail("session_revoked");
                    }
                }
            };
        });
    }

    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy("perm:attendance:read",   policy => policy.Requirements.Add(new PermissionRequirement("attendance:read")));
        options.AddPolicy("perm:attendance:write",  policy => policy.Requirements.Add(new PermissionRequirement("attendance:write")));
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
        options.AddPolicy("perm:sessions:read",     policy => policy.Requirements.Add(new PermissionRequirement("sessions:read")));
        options.AddPolicy("perm:sessions:revoke",   policy => policy.Requirements.Add(new PermissionRequirement("sessions:revoke")));
        options.AddPolicy("perm:users:delete",      policy => policy.Requirements.Add(new PermissionRequirement("users:delete")));
        options.AddPolicy("perm:users:read",        policy => policy.Requirements.Add(new PermissionRequirement("users:read")));
        options.AddPolicy("perm:users:status",      policy => policy.Requirements.Add(new PermissionRequirement("users:status")));
        options.AddPolicy("perm:users:write",       policy => policy.Requirements.Add(new PermissionRequirement("users:write")));
        options.AddPolicy("perm:vendors:read",      policy => policy.Requirements.Add(new PermissionRequirement("vendors:read")));
        options.AddPolicy("perm:vendors:write",     policy => policy.Requirements.Add(new PermissionRequirement("vendors:write")));
    });
    builder.Services.AddSingleton<IAuthorizationHandler, PermissionHandler>();

    // ─── Application Services ─────────────────────────────────────────────────
    builder.Services.Configure<OtpOptions>(builder.Configuration.GetSection(OtpOptions.SectionName));
    builder.Services.AddScoped<IOtpService, OtpService>();
    builder.Services.AddScoped<IPermissionService, PermissionService>();
    builder.Services.AddScoped<IAuditLogger, AuditLogger>();
    builder.Services.AddScoped<ISmsProvider, StubSmsProvider>();
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

END $$;
");
        Log.Information("Emergency schema patch complete.");

        await db.Database.MigrateAsync();
        Log.Information("Migrations complete. Running seeder...");
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
