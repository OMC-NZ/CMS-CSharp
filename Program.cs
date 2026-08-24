using System.Text.RegularExpressions;
using CMS_CSharp.Contracts.Devices;
using MySqlConnector;

var builder = WebApplication.CreateBuilder(args);
const string DevelopmentCorsPolicy = "DevelopmentCors";

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddHealthChecks();

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
    var provider = configuration["Database:Provider"];
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
        if (!string.Equals(provider, "MySql", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Json(new
            {
                connected = false,
                error = $"Unsupported database provider: {provider}"
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

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
            provider,
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

app.Run();

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
