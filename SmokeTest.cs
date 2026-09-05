using System.Net.Http.Json;
using ModelContextProtocol.Client;

namespace HumanToolCall;

internal static class SmokeTest
{
    internal static async Task RunAsync()
    {
        BackendConfig config = new()
        {
            Host = "127.0.0.1",
            Port = 64591,
            McpPath = "/mcp",
            InteractionTimeoutSeconds = 30,
            BrowserLongPollSeconds = 5,
            MaxPendingInteractions = 4,
            MaxQuestionsPerInteraction = 8,
            MaxRecentProgressReports = 4
        };

        InteractionBroker broker = new(config);
        await using BackendService backend = new(config, broker);
        await backend.StartAsync().ConfigureAwait(false);

        HttpClientTransport transport = new(new HttpClientTransportOptions
        {
            Endpoint = new Uri(backend.McpUrl),
            TransportMode = HttpTransportMode.StreamableHttp
        });

        await using McpClient client = await McpClient.CreateAsync(transport).ConfigureAwait(false);

        var tools = await client.ListToolsAsync().ConfigureAwait(false);
        string[] names = tools.Select(tool => tool.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        string[] expected = ["ask_user", "choose_path", "progress_report"];
        if (!names.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "Unexpected MCP tool set: " + string.Join(", ", names));
        }

        await client.CallToolAsync(
            "progress_report",
            new Dictionary<string, object?>
            {
                ["summary"] = "Smoke-test progress report",
                ["nextStep"] = "Verify blocking user interaction"
            }).ConfigureAwait(false);

        if (broker.Snapshot().Progress.Count != 1)
        {
            throw new InvalidOperationException("progress_report did not reach the shared interaction broker.");
        }

        Task askCall = client.CallToolAsync(
            "ask_user",
            new Dictionary<string, object?>
            {
                ["intro"] = "Smoke test",
                ["questions"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["id"] = "confirmation",
                        ["question"] = "Confirm the smoke-test interaction."
                    }
                }
            }).AsTask();

        PendingInteractionView ask = await WaitForPendingAsync(broker, "ask_user").ConfigureAwait(false);
        await SubmitThroughBrowserApiAsync(
            backend,
            ask.Id,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["confirmation"] = "confirmed"
            }).ConfigureAwait(false);
        await askCall.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        Task choiceCall = client.CallToolAsync(
            "choose_path",
            new Dictionary<string, object?>
            {
                ["intro"] = "Smoke test",
                ["decisions"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["id"] = "path",
                        ["question"] = "Choose a smoke-test path.",
                        ["options"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["id"] = "a",
                                ["label"] = "Path A",
                                ["description"] = "First test path."
                            },
                            new Dictionary<string, object?>
                            {
                                ["id"] = "b",
                                ["label"] = "Path B",
                                ["description"] = "Second test path."
                            }
                        },
                        ["recommendedOptionId"] = "a"
                    }
                }
            }).AsTask();

        PendingInteractionView choice = await WaitForPendingAsync(broker, "choose_path").ConfigureAwait(false);
        await SubmitThroughBrowserApiAsync(
            backend,
            choice.Id,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["path"] = "a"
            }).ConfigureAwait(false);
        await choiceCall.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        if (broker.PendingCount != 0)
        {
            throw new InvalidOperationException("Pending interactions remained after smoke-test answers were submitted.");
        }
    }

    private static async Task<PendingInteractionView> WaitForPendingAsync(InteractionBroker broker, string kind)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            PendingInteractionView? interaction = broker.Snapshot().Pending.FirstOrDefault(x => x.Kind == kind);
            if (interaction is not null)
            {
                return interaction;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        throw new TimeoutException($"MCP tool '{kind}' did not create a pending interaction.");
    }

    private static async Task SubmitThroughBrowserApiAsync(
        BackendService backend,
        string interactionId,
        Dictionary<string, string> answers)
    {
        Uri browserUri = new(backend.BrowserUrl);
        const string fragmentPrefix = "#token=";
        if (!browserUri.Fragment.StartsWith(fragmentPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The browser URL did not contain its bootstrap token.");
        }

        string token = Uri.UnescapeDataString(browserUri.Fragment[fragmentPrefix.Length..]);
        Uri endpoint = new(
            $"{browserUri.Scheme}://{browserUri.Host}:{browserUri.Port}/api/interactions/{Uri.EscapeDataString(interactionId)}/answer");

        using HttpClient http = new();
        http.DefaultRequestHeaders.Add("X-Human-Tool-Call-Token", token);
        using HttpResponseMessage response = await http.PostAsJsonAsync(
            endpoint,
            new InteractionAnswerRequest { Answers = answers },
            ConfigLoader.JsonOptions).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}
