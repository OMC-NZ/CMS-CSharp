using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using CMS_CSharp.Contracts.Channels;
using CMS_CSharp.Contracts.Devices;
using CMS_CSharp.Contracts.Gifts;
using CMS_CSharp.Data.Repositories;
using CMS_CSharp.Features.Auth;
using CMS_CSharp.Features.Claims;
using CMS_CSharp.Features.Devices;
using CMS_CSharp.Features.Promotions;
using CMS_CSharp.Features.Promotions.DuplicateDetection;
using CMS_CSharp.Services.Email;
using CMS_CSharp.Services.Storage;
using CMS_CSharp.Validation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using MySqlConnector;

var builder = WebApplication.CreateBuilder(args);
const string DevelopmentCorsPolicy = "DevelopmentCors";

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddHealthChecks();
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var jwtSecret = builder.Configuration["JWT_SECRET"];
        var jwtIssuer = builder.Configuration["JWT_ISSUER"];
        var jwtAudience = builder.Configuration["JWT_AUDIENCE"];
        if (string.IsNullOrWhiteSpace(jwtSecret) || Encoding.UTF8.GetByteCount(jwtSecret) < 32)
        {
            throw new InvalidOperationException(
                "JWT_SECRET must be configured with at least 32 bytes.");
        }
        if (string.IsNullOrWhiteSpace(jwtIssuer) || string.IsNullOrWhiteSpace(jwtAudience))
        {
            throw new InvalidOperationException(
                "JWT_ISSUER and JWT_AUDIENCE must be configured.");
        }

        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256]
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var principal = context.Principal;
                var sessionId = principal?.FindFirstValue("sid");
                if (principal is null ||
                    !int.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var accountId) ||
                    !int.TryParse(principal.FindFirstValue("user_id"), out var userId) ||
                    !int.TryParse(principal.FindFirstValue("security_version"), out var securityVersion) ||
                    string.IsNullOrWhiteSpace(sessionId))
                {
                    context.Fail("Required authentication claims are missing.");
                    return;
                }

                var validator = context.HttpContext.RequestServices
                    .GetRequiredService<SessionValidationService>();
                var access = await validator.ValidateAsync(
                    accountId,
                    userId,
                    sessionId,
                    securityVersion,
                    context.HttpContext.RequestAborted);
                if (access is null)
                {
                    context.Fail("The login session is no longer valid.");
                    return;
                }

                if (principal.Identity is ClaimsIdentity identity)
                {
                    foreach (var claim in identity.FindAll("roles").ToArray())
                    {
                        identity.RemoveClaim(claim);
                    }
                    foreach (var claim in identity.FindAll("permissions").ToArray())
                    {
                        identity.RemoveClaim(claim);
                    }
                    foreach (var role in access.Roles)
                    {
                        identity.AddClaim(new Claim("roles", role));
                    }
                    foreach (var permission in access.Permissions)
                    {
                        identity.AddClaim(new Claim("permissions", permission));
                    }
                }
            },
            OnChallenge = async context =>
            {
                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new
                {
                    success = false,
                    message = "Authentication is required."
                });
            },
            OnForbidden = async context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new
                {
                    success = false,
                    message = "You do not have permission to perform this action."
                });
            }
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();

    foreach (var permissionCode in PermissionCodes.All)
    {
        options.AddPolicy(permissionCode, policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.RequireAssertion(context => context.User
                .FindAll("permissions")
                .Any(claim => string.Equals(
                    claim.Value,
                    permissionCode,
                    StringComparison.OrdinalIgnoreCase)));
        });
    }
});
builder.Services.AddSingleton<IR2StorageService, R2StorageService>();
builder.Services.AddScoped<IClaimConfirmationEmailService, ClaimConfirmationEmailService>();
builder.Services.AddSingleton<ClaimConfirmationEmailQueue>();
builder.Services.AddSingleton<IClaimConfirmationEmailQueue>(serviceProvider =>
    serviceProvider.GetRequiredService<ClaimConfirmationEmailQueue>());
builder.Services.AddHostedService(serviceProvider =>
    serviceProvider.GetRequiredService<ClaimConfirmationEmailQueue>());
builder.Services.AddScoped<IReferenceDataRepository, ReferenceDataRepository>();
builder.Services.AddScoped<PromotionConflictDetector>();
builder.Services.AddScoped<PromotionCreationService>();
builder.Services.AddScoped<PromotionListService>();
builder.Services.AddScoped<PromotionDetailsService>();
builder.Services.AddScoped<EligiblePromotionLookupService>();
builder.Services.AddScoped<ClaimCreationService>();
builder.Services.AddScoped<ClaimListService>();
builder.Services.AddScoped<ClaimDetailsService>();
builder.Services.AddScoped<ClaimFulfilmentService>();
builder.Services.AddScoped<ClaimUpdateService>();
builder.Services.AddScoped<ClaimDeletionService>();
builder.Services.AddScoped<DeviceImportService>();
builder.Services.AddScoped<DeviceLookupService>();
builder.Services.AddScoped<DeviceUpdateService>();
builder.Services.AddScoped<AccountCreationService>();
builder.Services.AddScoped<RoleLookupService>();
builder.Services.AddScoped<LoginService>();
builder.Services.AddScoped<SessionValidationService>();
builder.Services.AddScoped<AccountListService>();

if (builder.Environment.IsDevelopment())
{
    var allowedOrigins = builder.Configuration
        .GetSection("Cors:AllowedOrigins")
        .Get<string[]>() ?? [];

    builder.Services.AddCors(options =>
    {
        options.AddPolicy(DevelopmentCorsPolicy, policy =>
        {
            policy
                .WithOrigins(allowedOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials();
        });
    });
}

var app = builder.Build();

app.Use(async (context, next) =>
{
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();

    try
    {
        await next(context);
    }
    finally
    {
        stopwatch.Stop();

        app.Logger.LogInformation(
            "HTTP {Method} {Path}{QueryString} responded {StatusCode} in {ElapsedMilliseconds} ms from {RemoteIpAddress}",
            context.Request.Method,
            context.Request.Path,
            context.Request.QueryString,
            context.Response.StatusCode,
            stopwatch.Elapsed.TotalMilliseconds,
            context.Connection.RemoteIpAddress);
    }
});

if (app.Environment.IsDevelopment())
{
    app.UseCors(DevelopmentCorsPolicy);
}
else
{
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", (IHostEnvironment environment) => Results.Ok(new
{
    name = "OMC CMS API",
    status = "running",
    environment = environment.EnvironmentName,
    utcTime = DateTimeOffset.UtcNow
}))
.WithName("GetApiStatus")
.AllowAnonymous();

app.MapHealthChecks("/health")
    .AllowAnonymous();

app.MapPost("/api/auth/login", async (
    LoginCommand command,
    HttpContext httpContext,
    LoginService loginService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await loginService.LoginAsync(
            command,
            httpContext.Connection.RemoteIpAddress?.ToString(),
            httpContext.Request.Headers.UserAgent.ToString(),
            cancellationToken));
    }
    catch (Exception exception) when (
        exception is LoginValidationException or InputValidationException)
    {
        return Results.BadRequest(new { success = false, message = exception.Message });
    }
    catch (LoginUnauthorizedException exception)
    {
        return Results.Json(
            new { success = false, message = exception.Message },
            statusCode: StatusCodes.Status401Unauthorized);
    }
    catch (LoginForbiddenException exception)
    {
        return Results.Json(
            new { success = false, message = exception.Message },
            statusCode: StatusCodes.Status403Forbidden);
    }
    catch (Exception exception) when (
        exception is MySqlException or InvalidOperationException or ArgumentException)
    {
        app.Logger.LogError(exception, "Login failed.");
        return Results.Json(new
        {
            success = false,
            message = app.Environment.IsDevelopment()
                ? exception.Message
                : "Login service is unavailable."
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("Login")
.AllowAnonymous();

app.MapGet("/api/roles", async (
    RoleLookupService roleLookupService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await roleLookupService.GetActiveAsync(cancellationToken));
    }
    catch (Exception exception) when (
        exception is MySqlException or InvalidOperationException or ArgumentException)
    {
        app.Logger.LogError(exception, "Role lookup failed.");
        return Results.Json(new
        {
            error = app.Environment.IsDevelopment()
                ? exception.Message
                : "Role lookup failed."
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("GetActiveRoles")
.RequireAuthorization(PermissionCodes.RolesView);

app.MapPost("/api/accounts", async (
    CreateAccountCommand command,
    AccountCreationService accountCreationService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await accountCreationService.CreateAsync(command, cancellationToken);
        return Results.Created($"/api/accounts/{result.AccountId}", result);
    }
    catch (AccountValidationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (InputValidationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (AccountConflictException exception)
    {
        return Results.Conflict(new { error = exception.Message });
    }
    catch (Exception exception) when (
        exception is MySqlException or InvalidOperationException or ArgumentException)
    {
        app.Logger.LogError(exception, "Account creation failed.");
        return Results.Json(new
        {
            error = app.Environment.IsDevelopment()
                ? exception.Message
                : "Account creation failed."
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("CreateAccount")
.RequireAuthorization(PermissionCodes.AccountsCreate);

app.MapGet("/api/accounts", async (
    AccountListService accountListService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await accountListService.GetAllAsync(cancellationToken));
    }
    catch (Exception exception) when (
        exception is MySqlException or InvalidOperationException or ArgumentException)
    {
        app.Logger.LogError(exception, "Account list lookup failed.");
        return Results.Json(new
        {
            error = app.Environment.IsDevelopment()
                ? exception.Message
                : "Account list lookup failed."
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("GetAccounts")
.RequireAuthorization(PermissionCodes.AccountsView);

app.MapGet("/api/accounts/search", async (
    string[]? email,
    AccountListService accountListService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await accountListService.FindByEmailsAsync(
            email,
            cancellationToken));
    }
    catch (Exception exception) when (
        exception is AccountValidationException or InputValidationException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (Exception exception) when (
        exception is MySqlException or InvalidOperationException or ArgumentException)
    {
        app.Logger.LogError(exception, "Account email lookup failed.");
        return Results.Json(new
        {
            error = app.Environment.IsDevelopment()
                ? exception.Message
                : "Account email lookup failed."
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("SearchAccountsByEmail")
.RequireAuthorization(PermissionCodes.AccountsView);

app.MapGet("/api/accounts/{accountId:int}", async (
    int accountId,
    AccountListService accountListService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var account = await accountListService.FindByAccountIdAsync(
            accountId,
            cancellationToken);
        return account is null
            ? Results.NotFound(new { error = $"Account '{accountId}' was not found." })
            : Results.Ok(account);
    }
    catch (AccountValidationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (Exception exception) when (
        exception is MySqlException or InvalidOperationException or ArgumentException)
    {
        app.Logger.LogError(exception, "Account ID lookup failed.");
        return Results.Json(new
        {
            error = app.Environment.IsDevelopment()
                ? exception.Message
                : "Account ID lookup failed."
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("GetAccountById")
.RequireAuthorization(PermissionCodes.AccountsView);

app.MapGet("/database/status", async (IConfiguration configuration, CancellationToken cancellationToken) =>
{
    var connectionString = configuration.GetConnectionString("DefaultConnection");

    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Json(new
        {
            connected = false,
            error = "ConnectionStrings:DefaultConnection is not configured."
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    try
    {
        var mysqlConnectionString = NormalizeMySqlConnectionString(connectionString);
        await using var connection = new MySqlConnection(mysqlConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                DATABASE() AS DatabaseName,
                @@hostname AS ServerName,
                VERSION() AS ProductVersion;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        return Results.Ok(new
        {
            connected = true,
            provider = "MySql",
            database = reader.GetString(0),
            server = reader.IsDBNull(1) ? null : reader.GetString(1),
            version = reader.IsDBNull(2) ? null : reader.GetString(2)
        });
    }
    catch (Exception exception) when (exception is MySqlException or InvalidOperationException or ArgumentException)
    {
        app.Logger.LogWarning(
            "Database connection failed: {ErrorMessage}",
            exception.Message);

        return Results.Json(new
        {
            connected = false,
            error = app.Environment.IsDevelopment()
                ? exception.Message
                : "Database connection failed."
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("GetDatabaseConfigurationStatus")
.RequireAuthorization(PermissionCodes.AccountsView);

app.MapGet("/api/devices/search", async (
    string market_name,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(market_name))
    {
        return Results.BadRequest(new
        {
            error = "The market_name query parameter is required."
        });
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Json(new
        {
            error = "Database connection is not configured."
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    try
    {
        var devices = new Dictionary<(string MarketName, string Model),
            HashSet<(string ChannelName, string ChannelCode)>>();
        var mysqlConnectionString = NormalizeMySqlConnectionString(connectionString);

        await using var connection = new MySqlConnection(mysqlConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT
                d.market_name,
                d.model,
                c.name AS channel_name,
                c.code AS channel_code
            FROM Devices d
            INNER JOIN Channels c ON c.code = d.channel_code
            WHERE d.market_name LIKE CONCAT('%', @marketName, '%') ESCAPE '='
              AND d.category = 11
              AND d.redemption_status = 0
              AND LOWER(TRIM(c.category)) IN ('retailer', 'carrier')
            ORDER BY d.market_name, d.model, c.name, c.code;
            """;
        command.Parameters.Add(
            new MySqlParameter("@marketName", MySqlDbType.VarChar)
            {
                Value = EscapeLikePattern(market_name.Trim())
            });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = (
                reader.GetString("market_name"),
                reader.GetString("model"));
            if (!devices.TryGetValue(key, out var channels))
            {
                channels = [];
                devices[key] = channels;
            }

            channels.Add((
                reader.GetString("channel_name"),
                reader.GetString("channel_code")));
        }

        var result = devices.Select(device => new DeviceSearchResult(
            device.Key.MarketName,
            device.Key.Model,
            device.Value
                .OrderBy(channel => channel.ChannelName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(channel => channel.ChannelCode, StringComparer.OrdinalIgnoreCase)
                .Select(channel => new DeviceSearchChannelResult(
                    channel.ChannelName,
                    channel.ChannelCode))
                .ToArray()))
            .ToArray();

        return Results.Ok(result);
    }
    catch (Exception exception) when (exception is MySqlException or InvalidOperationException or ArgumentException)
    {
        app.Logger.LogWarning(
            "Device search failed: {ErrorMessage}",
            exception.Message);

        return Results.Json(new
        {
            error = app.Environment.IsDevelopment()
                ? exception.Message
                : "Device search failed."
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("SearchDevicesByMarketName")
.RequireAuthorization(PermissionCodes.DevicesView);

app.MapPost("/api/devices/import", async (
    DeviceImportRequest request,
    DeviceImportService deviceImportService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await deviceImportService.ImportAsync(request, cancellationToken));
    }
    catch (DeviceImportValidationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (Exception exception) when (
        exception is MySqlException or InvalidOperationException or ArgumentException)
    {
        app.Logger.LogError(exception, "Device Excel import failed.");
        return Results.Json(new
        {
            error = app.Environment.IsDevelopment()
                ? exception.Message
                : "Device import failed."
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("ImportDevices")
.RequireAuthorization(PermissionCodes.DevicesCreate);

app.MapGet("/api/devices/search/imei", async (
    string[]? imei,
    DeviceLookupService deviceLookupService,
    CancellationToken cancellationToken) =>
    await ExecuteDeviceLookupAsync(
        app,
        () => deviceLookupService.FindByImeiAsync(imei, cancellationToken),
        "Device IMEI search failed."))
.WithName("SearchDevicesByImei")
.RequireAuthorization(PermissionCodes.DevicesView);

app.MapGet("/api/devices/search/model", async (
    string[]? model,
    DeviceLookupService deviceLookupService,
    CancellationToken cancellationToken) =>
    await ExecuteDeviceLookupAsync(
        app,
        () => deviceLookupService.FindByModelAsync(model, cancellationToken),
        "Device model search failed."))
.WithName("SearchDevicesByExactModel")
.RequireAuthorization(PermissionCodes.DevicesView);

app.MapGet("/api/devices/search/market-name", async (
    string[]? market_name,
    DeviceLookupService deviceLookupService,
    CancellationToken cancellationToken) =>
    await ExecuteDeviceLookupAsync(
        app,
        () => deviceLookupService.FindByMarketNameAsync(market_name, cancellationToken),
        "Device market name search failed."))
.WithName("SearchDevicesByMarketNameText")
.RequireAuthorization(PermissionCodes.DevicesView);

app.MapPatch("/api/devices/{imei}", async (
    string imei,
    DeviceUpdateRequest request,
    DeviceUpdateService deviceUpdateService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await deviceUpdateService.UpdateAsync(
            imei, request, cancellationToken);
        return result is null
            ? Results.NotFound(new
            {
                error = $"Device with IMEI '{imei.Trim()}' was not found."
            })
            : Results.Ok(result);
    }
    catch (DeviceUpdateValidationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (Exception exception) when (
        exception is MySqlException or InvalidOperationException or ArgumentException)
    {
        app.Logger.LogError(exception, "Device update failed.");
        return Results.Json(new
        {
            error = app.Environment.IsDevelopment()
                ? exception.Message
                : "Device update failed."
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("UpdateDeviceByImei")
.RequireAuthorization(PermissionCodes.DevicesEdit);

app.MapGet("/api/channels/search", async (
    string name,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(name))
    {
        return Results.BadRequest(new
        {
            error = "The name query parameter is required."
        });
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Json(new
        {
            error = "Database connection is not configured."
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    try
    {
        var channels = new List<ChannelSearchResult>();
        var mysqlConnectionString = NormalizeMySqlConnectionString(connectionString);

        await using var connection = new MySqlConnection(mysqlConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT
                name,
                code,
                category
            FROM Channels
            WHERE name LIKE CONCAT('%', @name, '%') ESCAPE '='
            ORDER BY name, code, category;
            """;
        command.Parameters.Add(
            new MySqlParameter("@name", MySqlDbType.VarChar)
            {
                Value = EscapeLikePattern(name.Trim())
            });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            channels.Add(new ChannelSearchResult(
                reader.GetString("name"),
                reader.GetString("code"),
                reader.GetString("category")));
        }

        return Results.Ok(channels);
    }
    catch (Exception exception) when (exception is MySqlException or InvalidOperationException or ArgumentException)
    {
        app.Logger.LogWarning(
            "Channel search failed: {ErrorMessage}",
            exception.Message);

        return Results.Json(new
        {
            error = app.Environment.IsDevelopment()
                ? exception.Message
                : "Channel search failed."
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("SearchChannelsByName")
.RequireAuthorization(PermissionCodes.PromotionsView);

app.MapGet("/api/channels", async (
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var connectionString = configuration.GetConnectionString("DefaultConnection");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Json(new
        {
            error = "Database connection is not configured."
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    try
    {
        var channels = new List<ChannelListResult>();
        var mysqlConnectionString = NormalizeMySqlConnectionString(connectionString);

        await using var connection = new MySqlConnection(mysqlConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                code,
                name,
                category
            FROM Channels
            ORDER BY code;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            channels.Add(new ChannelListResult(
                reader.GetString("code"),
                reader.GetString("name"),
                reader.GetString("category")));
        }

        return Results.Ok(channels);
    }
    catch (Exception exception) when (exception is MySqlException or InvalidOperationException or ArgumentException)
    {
        app.Logger.LogWarning(
            "Get channels failed: {ErrorMessage}",
            exception.Message);

        return Results.Json(new
        {
            error = app.Environment.IsDevelopment()
                ? exception.Message
                : "Getting channels failed."
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("GetChannels")
.RequireAuthorization(PermissionCodes.PromotionsView);

app.MapGet("/api/gifts/search", async (
    string name,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(name))
    {
        return Results.BadRequest(new
        {
            error = "The name query parameter is required."
        });
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Json(new
        {
            error = "Database connection is not configured."
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    try
    {
        var gifts = new List<GiftSearchResult>();
        var mysqlConnectionString = NormalizeMySqlConnectionString(connectionString);

        await using var connection = new MySqlConnection(mysqlConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT
                name,
                alias,
                color,
                status
            FROM Gifts
            WHERE name LIKE CONCAT('%', @name, '%') ESCAPE '='
            ORDER BY name, alias, color, status;
            """;
        command.Parameters.Add(
            new MySqlParameter("@name", MySqlDbType.VarChar)
            {
                Value = EscapeLikePattern(name.Trim())
            });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            gifts.Add(new GiftSearchResult(
                reader.GetString("name"),
                reader.GetString("alias"),
                reader.GetString("color"),
                reader.GetSByte("status")));
        }

        return Results.Ok(gifts);
    }
    catch (Exception exception) when (exception is MySqlException or InvalidOperationException or ArgumentException)
    {
        app.Logger.LogWarning(
            "Gift search failed: {ErrorMessage}",
            exception.Message);

        return Results.Json(new
        {
            error = app.Environment.IsDevelopment()
                ? exception.Message
                : "Gift search failed."
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("SearchGiftsByName")
.RequireAuthorization(PermissionCodes.PromotionsView);

app.MapGet("/api/promotions", async (
    PromotionListService promotionListService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await promotionListService.GetAsync(cancellationToken));
    }
    catch (Exception exception)
    {
        app.Logger.LogError(exception, "Promotion list lookup failed.");
        return Results.Json(new
        {
            error = app.Environment.IsDevelopment()
                ? exception.Message
                : "Promotion list lookup failed."
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("GetPromotions")
.RequireAuthorization(PermissionCodes.PromotionsView);

app.MapGet("/api/promotions/{promotionId:int}", async (
    int promotionId,
    PromotionDetailsService promotionDetailsService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await promotionDetailsService.FindAsync(promotionId, cancellationToken);
        return result is null
            ? Results.NotFound(new { error = $"Promotion '{promotionId}' was not found." })
            : Results.Ok(result);
    }
    catch (PromotionValidationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (Exception exception)
    {
        app.Logger.LogError(exception, "Promotion details lookup failed.");
        return Results.Json(new
        {
            error = app.Environment.IsDevelopment()
                ? exception.Message
                : "Promotion details lookup failed."
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("GetPromotionById")
.RequireAuthorization(PermissionCodes.PromotionsView);

app.MapPost("/api/promotions", async (
    HttpRequest httpRequest,
    PromotionCreationService promotionCreationService,
    CancellationToken cancellationToken) =>
{
    if (!httpRequest.HasFormContentType)
    {
        return Results.Json(new
        {
            error = "Content-Type must be multipart/form-data."
        }, statusCode: StatusCodes.Status415UnsupportedMediaType);
    }

    try
    {
        var form = await httpRequest.ReadFormAsync(cancellationToken);
        var banner = form.Files.GetFile("banner");
        if (banner is null)
        {
            throw new PromotionValidationException("The banner file is required.");
        }

        var command = new CreatePromotionCommand(
            form["name"].ToString(),
            form["description"].ToString(),
            DeserializeRequiredList<PromotionProductInput>(form, "products"),
            DeserializeRequiredList<PromotionChannelInput>(form, "channels"),
            DeserializeRequiredList<PromotionGiftInput>(form, "gifts"),
            form["terms"].FirstOrDefault(),
            form.Files.GetFile("terms"),
            banner);

        var result = await promotionCreationService.CreateAsync(
            command,
            cancellationToken);

        return Results.Created($"/api/promotions/{result.Id}", result);
    }
    catch (PromotionConflictException exception)
    {
        return Results.Conflict(new
        {
            error = exception.Message,
            existingPromotion = new
            {
                id = exception.Conflict.PromotionId,
                name = exception.Conflict.Name,
                slugUrl = exception.Conflict.SlugUrl
            },
            overlappingChannelCodes = exception.Conflict.OverlappingChannelCodes
        });
    }
    catch (Exception exception) when (
        exception is PromotionValidationException or JsonException)
    {
        return Results.BadRequest(new
        {
            error = exception.Message
        });
    }
    catch (Exception exception)
    {
        app.Logger.LogError(exception, "Promotion creation failed.");

        return Results.Json(new
        {
            error = app.Environment.IsDevelopment()
                ? exception.Message
                : "Promotion creation failed."
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("CreatePromotion")
.RequireAuthorization(PermissionCodes.PromotionsCreate);

app.MapGet("/api/promotions/eligible", async (
    string? imei,
    EligiblePromotionLookupService lookupService,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(imei))
    {
        return Results.BadRequest(new { error = "The imei query parameter is required." });
    }

    try
    {
        var result = await lookupService.FindByImeiAsync(imei, cancellationToken);
        return result is null
            ? Results.NotFound(new { error = $"Device IMEI '{imei.Trim()}' was not found." })
            : Results.Ok(result);
    }
    catch (PromotionValidationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (Exception exception)
    {
        app.Logger.LogError(exception, "Eligible promotion lookup failed.");
        return Results.Json(new
        {
            error = app.Environment.IsDevelopment()
                ? exception.Message
                : "Eligible promotion lookup failed."
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("GetEligiblePromotionsByImei")
.AllowAnonymous();

app.MapPost("/api/claims", async (
    HttpRequest httpRequest,
    ClaimCreationService claimCreationService,
    CancellationToken cancellationToken) =>
{
    if (!httpRequest.HasFormContentType)
    {
        return Results.Json(new
        {
            error = "Content-Type must be multipart/form-data."
        }, statusCode: StatusCodes.Status415UnsupportedMediaType);
    }

    try
    {
        var form = await httpRequest.ReadFormAsync(cancellationToken);
        var receipt = form.Files.GetFile("receipt");
        var screenshot = form.Files.GetFile("screenshot");
        if (receipt is null || screenshot is null)
        {
            throw new ClaimValidationException(
                "The receipt and screenshot files are required.");
        }

        if (!int.TryParse(form["promotionId"], out var promotionId))
        {
            throw new ClaimValidationException("promotionId must be a valid integer.");
        }

        var command = new CreateClaimCommand(
            promotionId,
            form["imei"].ToString(),
            form["purchaseDate"].ToString(),
            form["firstName"].ToString(),
            form["lastName"].ToString(),
            form["email"].ToString(),
            form["contact"].ToString(),
            form["street"].ToString(),
            form["suburb"].ToString(),
            form["city"].ToString(),
            form["postcode"].ToString(),
            form["instructions"].FirstOrDefault(),
            DeserializeRequiredList<string>(form, "giftAliases"),
            receipt,
            screenshot);

        var result = await claimCreationService.CreateAsync(command, cancellationToken);
        return Results.Created($"/api/claims/{result.Id}", result);
    }
    catch (Exception exception) when (
        exception is ClaimValidationException or JsonException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (Exception exception)
    {
        app.Logger.LogError(exception, "Claim creation failed.");
        return Results.Json(new
        {
            error = app.Environment.IsDevelopment()
                ? exception.Message
                : "Claim creation failed."
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("CreateClaim")
.AllowAnonymous();

app.MapGet("/api/claims", async (
    ClaimListService claimListService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await claimListService.GetAllAsync(cancellationToken));
    }
    catch (Exception exception) when (
        exception is MySqlException or InvalidOperationException or ArgumentException)
    {
        app.Logger.LogWarning(exception, "Claim list lookup failed.");
        return Results.Json(new
        {
            error = app.Environment.IsDevelopment()
                ? exception.Message
                : "Claim list lookup failed."
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("GetClaims")
.RequireAuthorization(PermissionCodes.ClaimsView);

app.MapGet("/api/claims/search", async (
    string? claim_id,
    ClaimListService claimListService,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(claim_id))
    {
        return Results.BadRequest(new { error = "The claim_id query parameter is required." });
    }

    try
    {
        return Results.Ok(await claimListService.SearchByClaimIdAsync(
            claim_id,
            cancellationToken));
    }
    catch (ClaimValidationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (Exception exception) when (
        exception is MySqlException or InvalidOperationException or ArgumentException)
    {
        app.Logger.LogWarning(exception, "Claim ID search failed.");
        return Results.Json(new
        {
            error = app.Environment.IsDevelopment()
                ? exception.Message
                : "Claim ID search failed."
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("SearchClaimsById")
.RequireAuthorization(PermissionCodes.ClaimsView);

app.MapGet("/api/claims/search/imei", async (
    string? imei,
    ClaimListService claimListService,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(imei))
    {
        return Results.BadRequest(new { error = "The imei query parameter is required." });
    }

    try
    {
        return Results.Ok(await claimListService.SearchByImeiAsync(imei, cancellationToken));
    }
    catch (ClaimValidationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (Exception exception) when (
        exception is MySqlException or InvalidOperationException or ArgumentException)
    {
        app.Logger.LogWarning(exception, "Claim IMEI search failed.");
        return Results.Json(new
        {
            error = app.Environment.IsDevelopment()
                ? exception.Message
                : "Claim IMEI search failed."
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("SearchClaimsByImei")
.RequireAuthorization(PermissionCodes.ClaimsView);

app.MapGet("/api/claims/search/email", async (
    string? email,
    ClaimListService claimListService,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(email))
    {
        return Results.BadRequest(new { error = "The email query parameter is required." });
    }

    try
    {
        return Results.Ok(await claimListService.SearchByEmailAsync(email, cancellationToken));
    }
    catch (ClaimValidationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (Exception exception) when (
        exception is MySqlException or InvalidOperationException or ArgumentException)
    {
        app.Logger.LogWarning(exception, "Claim email search failed.");
        return Results.Json(new
        {
            error = app.Environment.IsDevelopment()
                ? exception.Message
                : "Claim email search failed."
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("SearchClaimsByEmail")
.RequireAuthorization(PermissionCodes.ClaimsView);

app.MapGet("/api/claims/search/reference", async (
    string? reference,
    ClaimListService claimListService,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(reference))
    {
        return Results.BadRequest(new { error = "The reference query parameter is required." });
    }

    try
    {
        return Results.Ok(await claimListService.SearchByReferenceAsync(reference, cancellationToken));
    }
    catch (ClaimValidationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (Exception exception) when (
        exception is MySqlException or InvalidOperationException or ArgumentException)
    {
        app.Logger.LogWarning(exception, "Claim reference search failed.");
        return Results.Json(new
        {
            error = app.Environment.IsDevelopment()
                ? exception.Message
                : "Claim reference search failed."
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("SearchClaimsByReference")
.RequireAuthorization(PermissionCodes.ClaimsView);

app.MapGet("/api/claims/view/{claimId}", async (
    string claimId,
    ClaimDetailsService claimDetailsService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await claimDetailsService.FindAsync(claimId, cancellationToken);
        return result is null
            ? Results.NotFound(new { error = $"Claim '{claimId.Trim()}' was not found." })
            : Results.Ok(result);
    }
    catch (ClaimValidationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (Exception exception) when (
        exception is MySqlException or InvalidOperationException or ArgumentException)
    {
        app.Logger.LogWarning(exception, "Claim details lookup failed.");
        return Results.Json(new
        {
            error = app.Environment.IsDevelopment()
                ? exception.Message
                : "Claim details lookup failed."
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("ViewClaimById")
.RequireAuthorization(PermissionCodes.ClaimsView);

app.MapGet("/api/claims/fulfilment", async (
    string[]? claimIds,
    ClaimFulfilmentService claimFulfilmentService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var requestedIds = (claimIds ?? [])
            .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries))
            .ToArray();
        return Results.Ok(await claimFulfilmentService.FindManyAsync(
            requestedIds,
            cancellationToken));
    }
    catch (ClaimValidationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (Exception exception) when (
        exception is MySqlException or InvalidOperationException or ArgumentException)
    {
        app.Logger.LogWarning(exception, "Batch Claim fulfilment lookup failed.");
        return Results.Json(new
        {
            error = app.Environment.IsDevelopment()
                ? exception.Message
                : "Batch Claim fulfilment lookup failed."
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("GetClaimFulfilmentByIds")
.RequireAuthorization(PermissionCodes.ClaimsExport);

app.MapGet("/api/claims/fulfilment/{claimId}", async (
    string claimId,
    ClaimFulfilmentService claimFulfilmentService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var results = await claimFulfilmentService.FindAsync(claimId, cancellationToken);
        return results.Count == 0
            ? Results.NotFound(new { error = $"Claim '{claimId.Trim()}' was not found or has no Gift." })
            : Results.Ok(results);
    }
    catch (ClaimValidationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (Exception exception) when (
        exception is MySqlException or InvalidOperationException or ArgumentException)
    {
        app.Logger.LogWarning(exception, "Claim fulfilment lookup failed.");
        return Results.Json(new
        {
            error = app.Environment.IsDevelopment()
                ? exception.Message
                : "Claim fulfilment lookup failed."
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("GetClaimFulfilmentById")
.RequireAuthorization(PermissionCodes.ClaimsExport);

app.MapPatch("/api/claims/{claimId}", async (
    string claimId,
    HttpRequest httpRequest,
    ClaimUpdateService claimUpdateService,
    CancellationToken cancellationToken) =>
{
    if (!httpRequest.HasFormContentType)
    {
        return Results.Json(new
        {
            error = "Content-Type must be multipart/form-data."
        }, statusCode: StatusCodes.Status415UnsupportedMediaType);
    }

    try
    {
        var form = await httpRequest.ReadFormAsync(cancellationToken);
        var command = new UpdateClaimCommand(
            claimId,
            GetOptionalFormValue(form, "email"),
            GetOptionalFormValue(form, "contact"),
            GetOptionalFormValue(form, "street"),
            GetOptionalFormValue(form, "suburb"),
            GetOptionalFormValue(form, "city"),
            GetOptionalFormValue(form, "postcode"),
            form.ContainsKey("instructions") ? form["instructions"].ToString() : null,
            form.ContainsKey("instructions"),
            DeserializeOptionalList<string>(form, "giftAliases"),
            form.Files.GetFile("receipt"),
            form.Files.GetFile("imeiCopy"));

        var result = await claimUpdateService.UpdateAsync(command, cancellationToken);
        return result is null
            ? Results.NotFound(new { error = $"Claim '{claimId.Trim()}' was not found." })
            : Results.Ok(result);
    }
    catch (Exception exception) when (
        exception is ClaimValidationException or JsonException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (Exception exception)
    {
        app.Logger.LogError(exception, "Claim update failed.");
        return Results.Json(new
        {
            error = app.Environment.IsDevelopment()
                ? exception.Message
                : "Claim update failed."
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("UpdateClaimById")
.RequireAuthorization(PermissionCodes.ClaimsEdit);

app.MapDelete("/api/claims/{claimId}", async (
    string claimId,
    ClaimDeletionService claimDeletionService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await claimDeletionService.DeleteAsync(claimId, cancellationToken);
        return result is null
            ? Results.NotFound(new { error = $"Claim '{claimId.Trim()}' was not found." })
            : Results.Ok(result);
    }
    catch (ClaimValidationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (Exception exception) when (
        exception is MySqlException or InvalidOperationException or ArgumentException)
    {
        app.Logger.LogError(exception, "Claim deletion failed.");
        return Results.Json(new
        {
            error = app.Environment.IsDevelopment()
                ? exception.Message
                : "Claim deletion failed."
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("DeleteClaimById")
.RequireAuthorization(PermissionCodes.ClaimsDelete);

app.Run();

static async Task<IResult> ExecuteDeviceLookupAsync(
    WebApplication app,
    Func<Task<DeviceLookupResult>> lookup,
    string logMessage)
{
    try
    {
        return Results.Ok(await lookup());
    }
    catch (DeviceLookupValidationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (Exception exception) when (
        exception is MySqlException or InvalidOperationException or ArgumentException)
    {
        app.Logger.LogWarning(exception, "{Message}", logMessage);
        return Results.Json(new
        {
            error = app.Environment.IsDevelopment()
                ? exception.Message
                : logMessage
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}

static IReadOnlyList<T> DeserializeRequiredList<T>(
    IFormCollection form,
    string fieldName)
{
    var json = form[fieldName].ToString();
    if (string.IsNullOrWhiteSpace(json))
    {
        throw new PromotionValidationException(
            $"The {fieldName} form field is required.");
    }

    return JsonSerializer.Deserialize<List<T>>(
        json,
        new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new PromotionValidationException(
            $"The {fieldName} form field must be a JSON array.");
}

static IReadOnlyList<T>? DeserializeOptionalList<T>(
    IFormCollection form,
    string fieldName)
{
    if (!form.ContainsKey(fieldName))
    {
        return null;
    }

    var json = form[fieldName].ToString();
    if (string.IsNullOrWhiteSpace(json))
    {
        throw new ClaimValidationException(
            $"The {fieldName} form field must be a JSON array when provided.");
    }

    return JsonSerializer.Deserialize<List<T>>(
        json,
        new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new ClaimValidationException(
            $"The {fieldName} form field must be a JSON array.");
}

static string? GetOptionalFormValue(IFormCollection form, string fieldName) =>
    form.ContainsKey(fieldName) ? form[fieldName].ToString() : null;

static string NormalizeMySqlConnectionString(string connectionString)
{
    var normalized = Regex.Replace(
        connectionString,
        @"(?i)Server=([^;,]+),(\d+)",
        "Server=$1;Port=$2");

    normalized = normalized.Replace(
        "Encrypt=True;",
        "SslMode=Preferred;",
        StringComparison.OrdinalIgnoreCase);
    normalized = normalized.Replace(
        "Encrypt=False;",
        "SslMode=None;",
        StringComparison.OrdinalIgnoreCase);
    normalized = normalized.Replace(
        "TrustServerCertificate=True;",
        string.Empty,
        StringComparison.OrdinalIgnoreCase);
    normalized = normalized.Replace(
        "TrustServerCertificate=False;",
        string.Empty,
        StringComparison.OrdinalIgnoreCase);

    var builder = new MySqlConnectionStringBuilder(normalized)
    {
        TreatTinyAsBoolean = false
    };
    return builder.ConnectionString;
}

static string EscapeLikePattern(string value) => value
    .Replace("=", "==", StringComparison.Ordinal)
    .Replace("%", "=%", StringComparison.Ordinal)
    .Replace("_", "=_", StringComparison.Ordinal);
