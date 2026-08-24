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

app.Run();
