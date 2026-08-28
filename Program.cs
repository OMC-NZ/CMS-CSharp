using System.Text.RegularExpressions;
using System.Text.Json;
using CMS_CSharp.Contracts.Channels;
using CMS_CSharp.Contracts.Devices;
using CMS_CSharp.Contracts.Gifts;
using CMS_CSharp.Data.Repositories;
using CMS_CSharp.Features.Claims;
using CMS_CSharp.Features.Promotions;
using CMS_CSharp.Features.Promotions.DuplicateDetection;
using CMS_CSharp.Services.Email;
using CMS_CSharp.Services.Storage;
using MySqlConnector;

var builder = WebApplication.CreateBuilder(args);
const string DevelopmentCorsPolicy = "DevelopmentCors";

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddHealthChecks();
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
builder.Services.AddScoped<EligiblePromotionLookupService>();
builder.Services.AddScoped<ClaimCreationService>();

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

app.MapGet("/", (IHostEnvironment environment) => Results.Ok(new
{
    name = "OMC CMS API",
    status = "running",
    environment = environment.EnvironmentName,
    utcTime = DateTimeOffset.UtcNow
}))
.WithName("GetApiStatus");

app.MapHealthChecks("/health");

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
.WithName("GetDatabaseConfigurationStatus");

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
        var devices = new List<DeviceSearchResult>();
        var mysqlConnectionString = NormalizeMySqlConnectionString(connectionString);

        await using var connection = new MySqlConnection(mysqlConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT
                market_name,
                model
            FROM Devices
            WHERE market_name LIKE CONCAT('%', @marketName, '%') ESCAPE '='
              AND category IN (11, 21)
              AND redemption_status = 0
            ORDER BY market_name, model;
            """;
        command.Parameters.Add(
            new MySqlParameter("@marketName", MySqlDbType.VarChar)
            {
                Value = EscapeLikePattern(market_name.Trim())
            });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            devices.Add(new DeviceSearchResult(
                reader.GetString("market_name"),
                reader.GetString("model")));
        }

        return Results.Ok(devices);
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
.WithName("SearchDevicesByMarketName");

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
.WithName("SearchChannelsByName");

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
.WithName("GetChannels");

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
.WithName("SearchGiftsByName");

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
.WithName("CreatePromotion");

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
.WithName("GetEligiblePromotionsByImei");

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
.WithName("CreateClaim");

app.Run();

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

    return normalized;
}

static string EscapeLikePattern(string value) => value
    .Replace("=", "==", StringComparison.Ordinal)
    .Replace("%", "=%", StringComparison.Ordinal)
    .Replace("_", "=_", StringComparison.Ordinal);
