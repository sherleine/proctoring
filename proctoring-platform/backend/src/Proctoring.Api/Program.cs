using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Proctoring.Api.Configuration;
using Proctoring.Api.Endpoints;
using Proctoring.Api.Errors;
using Proctoring.Api.Background;
using Proctoring.Api.Middleware;
using Proctoring.Application.Events;
using Proctoring.Contracts.Errors;
using Proctoring.Infrastructure;
using Proctoring.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------- configuration
// Local development reads the repository's .env (real environment variables take precedence).
string? dotEnvFile = null;
if (builder.Environment.IsDevelopment())
    dotEnvFile = DotEnv.Load(builder.Configuration, Directory.GetCurrentDirectory());

var (settings, tokenSecret, redisSettings) = builder.Services.AddValidatedSettings(builder.Configuration, builder.Environment); // throws on invalid values

// ASPNETCORE_URLS from .env (real environment variables are applied by the host itself).
if (builder.Configuration["ASPNETCORE_URLS"] is { Length: > 0 } urls && string.IsNullOrEmpty(builder.Configuration["urls"]))
    builder.WebHost.UseUrls(urls);

// ---------------------------------------------------------------- logging
builder.Logging.ClearProviders();
// The framework logs every exception that reaches the exception handler as an error with a stack trace,
// including expected ones (401/404/409). AppExceptionHandler logs unexpected exceptions itself, so the
// framework's copy is switched off to keep logs free of noise (and of anything a stack trace might carry).
builder.Logging.AddFilter("Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware", LogLevel.None);
if (settings.LogFormat == "json")
{
    // One JSON object per line, including scopes (CorrelationId, TraceId) - easy to search and ship.
    builder.Logging.AddJsonConsole(o =>
    {
        o.IncludeScopes = true;
        o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
        o.UseUtcTimestamp = true;
    });
}
else
{
    builder.Logging.AddSimpleConsole(o => o.IncludeScopes = true);
}

// ---------------------------------------------------------------- services
builder.Services.AddSingleton(TimeProvider.System);
// Relative paths resolve against the content root (the API project folder; /app in Docker), which is
// also where `dotnet ef database update` runs, so the CLI and the app always use the same file.
var contentRoot = builder.Environment.ContentRootPath;
var databasePath = Path.GetFullPath(settings.DatabasePath, contentRoot);
var infrastructure = new InfrastructureSettings(
    databasePath,
    settings.ContractsSchemaDirectory,
    tokenSecret,
    AudioStorePath: Path.GetFullPath(settings.AudioStorePath, contentRoot),
    QueueProvider: settings.QueueProvider,
    QueueDirectory: Path.GetFullPath(settings.QueueLocalDirectory, contentRoot),
    Redis: redisSettings);
builder.Services.AddInfrastructure(infrastructure);
builder.Services.AddHostedService<EventMaintenanceHostedService>();
builder.Services.AddExceptionHandler<AppExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 50_000_000);
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(settings.CorsOrigins)
    .WithMethods("GET", "POST")
    .WithHeaders("Authorization", "Content-Type", ApiHeaders.IdempotencyKey, ApiHeaders.CorrelationId, "Last-Event-ID")
    .WithExposedHeaders(ApiHeaders.CorrelationId, ApiHeaders.IdempotentReplayed, "Location")));

var app = builder.Build();

if (dotEnvFile is not null) app.Logger.LogInformation("Loaded settings from {DotEnvFile}", dotEnvFile);
app.Logger.LogInformation("Audio store: {AudioStorePath}", infrastructure.AudioStorePath);
var queue = app.Services.GetRequiredService<IAudioEventQueue>();
if (DependencyInjection.NormalizeQueueProvider(infrastructure.QueueProvider) == DependencyInjection.LocalDirectoryQueue)
    app.Logger.LogWarning("Event queue is the development placeholder ({Queue}): messages are written to {QueueDirectory} and nothing consumes them yet",
        queue.Name, infrastructure.QueueDirectory);
else if (redisSettings is not null)
    app.Logger.LogInformation("Event queue: Redis Streams at {Redis}; events -> {EventsStream}, results <- {ResultsStream} (consumer {ConsumerState})",
        redisSettings.ToString(), redisSettings.EventsStream, redisSettings.ResultsStream, redisSettings.ResultsConsumerEnabled ? "enabled" : "disabled");
if (tokenSecret.IsEphemeral)
    app.Logger.LogWarning("SESSION_TOKEN_SECRET is not set: using a random per-process secret. Idempotent session-create retries will not survive a restart.");

// ---------------------------------------------------------------- database
if (settings.MigrateOnStart)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ProctoringDbContext>();
    db.Database.Migrate();
    app.Logger.LogInformation("Database migrations applied to {DatabasePath}", databasePath);
}

// ---------------------------------------------------------------- pipeline
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages(async ctx =>
{
    // Unmatched routes etc. also get problem+json instead of an empty body.
    var http = ctx.HttpContext;
    if (http.Response.HasStarted || http.Response.ContentLength > 0) return;
    var status = http.Response.StatusCode;
    var result = status == StatusCodes.Status404NotFound
        ? Problems.Create(http, status, ProblemTypes.NotFound, "Not found", "No such resource")
        : Problems.Create(http, status, ProblemTypes.BadRequest, ReasonPhrase(status));
    await result.ExecuteAsync(http);
});
app.UseCors();

app.MapOpenApi(); // OpenAPI document at /openapi/v1.json
app.MapHealthEndpoints();
app.MapSessionEndpoints();
app.MapEventEndpoints();
app.MapUpdateEndpoints();

app.Run();

static string ReasonPhrase(int status) => Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(status);

/// <summary>Entry point marker so integration tests can host the app (WebApplicationFactory&lt;Program&gt;).</summary>
public partial class Program;
