using System.Collections.Concurrent;
using System.ComponentModel;

namespace HumanToolCall;

internal enum InteractionKind
{
    AskUser,
    ChoosePath
}

public sealed class ChoiceOption
{
    [Description("Use 1 2 3 4 etc.")] public string choiceId { get; set; } = string.Empty;

    [Description("Describe the choice in detail.")]
    public string choice { get; set; } = string.Empty;
}

internal sealed class ChoiceDecision
{
    public string decision { get; init; } = string.Empty;

    public IReadOnlyList<ChoiceOption> Options { get; init; } = Array.Empty<ChoiceOption>();

    public string? recommendation { get; init; }
}

internal sealed class InteractionResult
{
    public string status { get; set; } = string.Empty;
    public string? answer { get; set; }
    public string? note { get; set; }
}

internal sealed class PendingInteractionView
{
    public string id { get; init; } = string.Empty;
    public string kind { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public string? question { get; init; }
    public ChoiceDecision? Decision { get; init; }
}

internal sealed class ProgressReportView
{
    public string id { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public string summary { get; init; } = string.Empty;
    public string? completed { get; init; }
    public string? nextStep { get; init; }
    public string? notableDiscovery { get; init; }
}

internal sealed class InteractionSnapshot
{
    public IReadOnlyList<PendingInteractionView> Pending { get; init; } = Array.Empty<PendingInteractionView>();
    public IReadOnlyList<ProgressReportView> Progress { get; init; } = Array.Empty<ProgressReportView>();
}

internal sealed class InteractionAnswerRequest
{
    public string? answer { get; set; }
}

internal sealed class PendingInteraction
{
    internal required PendingInteractionView View { get; init; }

    internal TaskCompletionSource<InteractionResult> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class InteractionBroker
{
    private readonly BackendConfig _config;
    private readonly ConcurrentDictionary<string, PendingInteraction> _pending = new(StringComparer.Ordinal);
    private readonly object _progressGate = new();
    private readonly List<ProgressReportView> _progress = new();

    internal InteractionBroker(BackendConfig config)
    {
        _config = config;
    }

    internal event Action<PendingInteractionView>? interactionAdded;
    internal event Action<string>? interactionRemoved;
    internal event Action<ProgressReportView>? progressAdded;
    internal event Action<string>? progressRemoved;

    internal int PendingCount => _pending.Count;

    internal async Task<InteractionResult> AskAsync(
        InteractionKind kind,
        string? question,
        ChoiceDecision? decision,
        CancellationToken cancellationToken)
    {
        if (_pending.Count >= _config.MaxPendingInteractions)
        {
            return new InteractionResult
            {
                status = "capacity_reached",
                note =
                    "Too many user interactions are already waiting for answers. Continue with available information or try again after one is resolved."
            };
        }

        DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        string id = Guid.NewGuid().ToString("N");
        PendingInteraction pending = new()
        {
            View = new PendingInteractionView
            {
                id = id,
                kind = kind == InteractionKind.AskUser ? "askUser" : "choosePath",
                CreatedAt = createdAt,
                ExpiresAt = createdAt.AddSeconds(_config.InteractionTimeoutSeconds),
                question = question,
                Decision = decision
            }
        };

        if (!_pending.TryAdd(id, pending))
        {
            return new InteractionResult { status = "server_error", note = "Could not allocate an interaction id." };
        }

        interactionAdded?.Invoke(pending.View);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(_config.InteractionTimeoutSeconds));
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            return await pending.Completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested &&
                                                 !cancellationToken.IsCancellationRequested)
        {
            return new InteractionResult
            {
                status = "timed_out",
                note =
                    "The user did not answer within 10 minutes. This is not a refusal. You may try again or ask later if the information remains important."
            };
        }
        catch (OperationCanceledException)
        {
            return new InteractionResult
            {
                status = "cancelled_by_host",
                note = "The MCP host cancelled the pending tool call before the user answered."
            };
        }
        finally
        {
            if (_pending.TryRemove(id, out _))
            {
                interactionRemoved?.Invoke(id);
            }
        }
    }

    internal bool SubmitAnswer(string interactionId, string? answer)
    {
        if (!_pending.TryGetValue(interactionId, out PendingInteraction? pending))
        {
            return false;
        }

        bool completed = pending.Completion.TrySetResult(new InteractionResult
        {
            status = "answered",
            answer = Clean(answer, 16000) ?? string.Empty
        });

        return completed;
    }

    internal bool CancelInteraction(string interactionId)
    {
        if (!_pending.TryGetValue(interactionId, out PendingInteraction? pending))
        {
            return false;
        }

        bool completed = pending.Completion.TrySetResult(new InteractionResult
        {
            status = "user_cancelled",
            note = "The user dismissed this interaction."
        });

        return completed;
    }

    internal void progressAdd(string summary, string? completed, string? nextStep, string? notableDiscovery)
    {
        ProgressReportView report = new()
        {
            id = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            summary = Clean(summary, 6000) ?? string.Empty,
            completed = Clean(completed, 6000),
            nextStep = Clean(nextStep, 6000),
            notableDiscovery = Clean(notableDiscovery, 6000)
        };

        string[] removedIds;
        lock (_progressGate)
        {
            _progress.Add(report);
            int overflow = _progress.Count - _config.MaxRecentProgressReports;
            if (overflow > 0)
            {
                removedIds = _progress.Take(overflow).Select(x => x.id).ToArray();
                _progress.RemoveRange(0, overflow);
            }
            else
            {
                removedIds = Array.Empty<string>();
            }
        }

        foreach (string id in removedIds)
        {
            progressRemoved?.Invoke(id);
        }

        progressAdded?.Invoke(report);
    }

    internal InteractionSnapshot Snapshot()
    {
        PendingInteractionView[] pending = _pending.Values
            .Select(x => x.View)
            .OrderBy(x => x.CreatedAt)
            .ToArray();

        ProgressReportView[] progress;
        lock (_progressGate)
        {
            progress = _progress.ToArray();
        }

        return new InteractionSnapshot
        {
            Pending = pending,
            Progress = progress
        };
    }

    internal void CancelAll(string reason)
    {
        foreach ((string id, PendingInteraction pending) in _pending)
        {
            pending.Completion.TrySetResult(new InteractionResult
            {
                status = "backend_stopped",
                note = reason
            });
        }
    }

    internal static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string clean = value.Trim();
        return clean.Length <= maxLength ? clean : clean[..maxLength];
    }
}