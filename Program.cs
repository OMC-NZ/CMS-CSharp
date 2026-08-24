using System.Text.RegularExpressions;
using MySqlConnector;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddHealthChecks();

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

if (!app.Environment.IsDevelopment())
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
