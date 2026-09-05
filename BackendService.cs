using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace HumanToolCall;

internal sealed class BackendService : IAsyncDisposable
{
    private readonly BackendConfig _config;
    private readonly InteractionBroker _broker;
    private readonly string _browserToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WebApplication? _app;
    private volatile bool _isRunning;

    internal BackendService(BackendConfig config, InteractionBroker broker)
    {
        _config = config;
        _broker = broker;
    }

    internal bool IsRunning => _isRunning;
    internal string McpUrl => $"http://{_config.Host}:{_config.Port}{_config.McpPath}";
    internal string BrowserUrl => $"http://{_config.Host}:{_config.Port}/#token={Uri.EscapeDataString(_browserToken)}";

    internal async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isRunning)
            {
                return;
            }

            WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = Array.Empty<string>(),
                ApplicationName = typeof(BackendService).Assembly.FullName,
                ContentRootPath = AppContext.BaseDirectory
            });

            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.AddServerHeader = false;
                options.Listen(IPAddress.Loopback, _config.Port);
            });

            builder.Services.AddSingleton(_config);
            builder.Services.AddSingleton(_broker);
            builder.Services.AddMcpServer(options =>
                {
                    options.ServerInfo = new Implementation
                    {
                        Name = "human-tool-call",
                        Version = "0.1.0",
                        Title = "Human Tool Call",
                        Description = "User communication tools for an ongoing ChatGPT workflow."
                    };
                    options.ServerInstructions = UserCommunicationTools.ServerInstructions;
                })
                .WithHttpTransport(options => options.Stateless = true)
                .WithTools<UserCommunicationTools>();

            WebApplication app = builder.Build();

            app.Use(async (context, next) =>
            {
                string host = context.Request.Host.Host;
                if (!string.Equals(host, _config.Host, StringComparison.Ordinal) &&
                    !string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsync("Invalid Host header.", context.RequestAborted).ConfigureAwait(false);
                    return;
                }

                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.XContentTypeOptions = "nosniff";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                await next(context).ConfigureAwait(false);
            });

            app.MapGet("/", () => Results.Content(WebUi.Html, "text/html; charset=utf-8"));
            app.MapGet("/healthz", () => Results.Text("live", "text/plain"));

            app.MapGet("/api/state", (HttpContext context) =>
            {
                if (!AuthorizeBrowser(context))
                {
                    return Results.Unauthorized();
                }

                return Results.Json(_broker.Snapshot(), ConfigLoader.JsonOptions);
            });

            app.MapGet("/api/poll", async (HttpContext context) =>
            {
                if (!AuthorizeBrowser(context))
                {
                    return Results.Unauthorized();
                }

                long since = 0;
                if (long.TryParse(context.Request.Query["version"], out long parsed))
                {
                    since = parsed;
                }

                await _broker.WaitForChangeAsync(
                    since,
                    TimeSpan.FromSeconds(_config.BrowserLongPollSeconds),
                    context.RequestAborted).ConfigureAwait(false);

                return Results.Json(_broker.Snapshot(), ConfigLoader.JsonOptions);
            });

            app.MapPost("/api/interactions/{id}/answer", async (HttpContext context, string id) =>
            {
                if (!AuthorizeBrowser(context))
                {
                    return Results.Unauthorized();
                }

                InteractionAnswerRequest? request = await context.Request.ReadFromJsonAsync<InteractionAnswerRequest>(
                    ConfigLoader.JsonOptions,
                    context.RequestAborted).ConfigureAwait(false);

                if (request is null)
                {
                    return Results.BadRequest(new { error = "Missing request body." });
                }

                return _broker.SubmitAnswers(id, request.Answers)
                    ? Results.Json(new { status = "received" }, ConfigLoader.JsonOptions)
                    : Results.NotFound(new { error = "The interaction no longer exists or has already completed." });
            });

            app.MapPost("/api/interactions/{id}/cancel", (HttpContext context, string id) =>
            {
                if (!AuthorizeBrowser(context))
                {
                    return Results.Unauthorized();
                }

                return _broker.CancelInteraction(id)
                    ? Results.Json(new { status = "received" }, ConfigLoader.JsonOptions)
                    : Results.NotFound(new { error = "The interaction no longer exists or has already completed." });
            });

            app.MapMcp(_config.McpPath);

            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            _app = app;
            _isRunning = true;
        }
        catch
        {
            _isRunning = false;
            if (_app is not null)
            {
                await _app.DisposeAsync().ConfigureAwait(false);
                _app = null;
            }
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_isRunning || _app is null)
            {
                _isRunning = false;
                return;
            }

            _broker.CancelAll("The local Human Tool Call backend was stopped before the interaction completed.");

            WebApplication app = _app;
            _app = null;
            _isRunning = false;

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await app.StopAsync(timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                await app.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool AuthorizeBrowser(HttpContext context)
    {
        string provided = context.Request.Headers["X-Human-Tool-Call-Token"].ToString();
        if (provided.Length != _browserToken.Length)
        {
            return false;
        }

        byte[] actualBytes = Encoding.UTF8.GetBytes(provided);
        byte[] expectedBytes = Encoding.UTF8.GetBytes(_browserToken);
        try
        {
            return CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actualBytes);
            CryptographicOperations.ZeroMemory(expectedBytes);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
