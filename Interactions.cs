using System.Collections.Concurrent;
using System.ComponentModel;

namespace HumanToolCall;

internal enum InteractionKind
{
    AskUser,
    ChoosePath
}

public sealed class UserQuestion
{
    [Description("Stable identifier for this question within the tool call. Use a short descriptive identifier such as target_version.")]
    public string Id { get; set; } = string.Empty;

    [Description("The actual question to show to the user.")]
    public string Question { get; set; } = string.Empty;

    [Description("Optional context the user needs in order to understand the question.")]
    public string? Context { get; set; }

    [Description("Optional concise explanation of why this answer affects the work.")]
    public string? WhyItMatters { get; set; }
}

public sealed class ChoiceOption
{
    [Description("Stable identifier returned if the user chooses this option.")]
    public string Id { get; set; } = string.Empty;

    [Description("Short human-readable option label.")]
    public string Label { get; set; } = string.Empty;

    [Description("What this option means in practical terms.")]
    public string Description { get; set; } = string.Empty;

    [Description("Important advantages of this option.")]
    public IReadOnlyList<string>? Pros { get; set; }

    [Description("Important disadvantages or tradeoffs of this option.")]
    public IReadOnlyList<string>? Cons { get; set; }
}

public sealed class ChoiceDecision
{
    [Description("Stable identifier for this decision within the tool call.")]
    public string Id { get; set; } = string.Empty;

    [Description("The decision the user is being asked to make.")]
    public string Question { get; set; } = string.Empty;

    [Description("Optional background needed to understand the decision.")]
    public string? Context { get; set; }

    [Description("Two or more legitimate options. Include enough information for an informed choice.")]
    public IReadOnlyList<ChoiceOption> Options { get; set; } = Array.Empty<ChoiceOption>();

    [Description("Optional option id that you recommend. A recommendation does not replace asking the user.")]
    public string? RecommendedOptionId { get; set; }

    [Description("Optional concise reason for the recommendation.")]
    public string? RecommendationReason { get; set; }
}

public sealed class InteractionResult
{
    public string Status { get; set; } = string.Empty;
    public string InteractionId { get; set; } = string.Empty;
    public Dictionary<string, string> Answers { get; set; } = new(StringComparer.Ordinal);
    public string? Note { get; set; }
}

public sealed class ProgressReceipt
{
    public string Status { get; set; } = "received";
    public string ReportId { get; set; } = string.Empty;
}

internal sealed class PendingInteractionView
{
    public string Id { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string? Intro { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public IReadOnlyList<UserQuestion>? Questions { get; init; }
    public IReadOnlyList<ChoiceDecision>? Decisions { get; init; }
}

internal sealed class ProgressReportView
{
    public string Id { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string? Completed { get; init; }
    public string? NextStep { get; init; }
    public string? NotableDiscovery { get; init; }
}

internal sealed class InteractionSnapshot
{
    public long Version { get; init; }
    public IReadOnlyList<PendingInteractionView> Pending { get; init; } = Array.Empty<PendingInteractionView>();
    public IReadOnlyList<ProgressReportView> Progress { get; init; } = Array.Empty<ProgressReportView>();
}

internal sealed class InteractionAnswerRequest
{
    public Dictionary<string, string> Answers { get; set; } = new(StringComparer.Ordinal);
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
    private readonly object _changeGate = new();
    private TaskCompletionSource<long> _changed = NewChangeSource();
    private long _version;

    internal InteractionBroker(BackendConfig config)
    {
        _config = config;
    }

    internal event Action<PendingInteractionView>? InteractionAdded;
    internal event Action<ProgressReportView>? ProgressAdded;

    internal int PendingCount => _pending.Count;
    internal long Version => Interlocked.Read(ref _version);

    internal async Task<InteractionResult> AskAsync(
        InteractionKind kind,
        string? intro,
        IReadOnlyList<UserQuestion>? questions,
        IReadOnlyList<ChoiceDecision>? decisions,
        CancellationToken cancellationToken)
    {
        if (_pending.Count >= _config.MaxPendingInteractions)
        {
            return new InteractionResult
            {
                Status = "capacity_reached",
                Note = "Too many user interactions are already waiting for answers. Continue with available information or try again after one is resolved."
            };
        }

        DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        string id = Guid.NewGuid().ToString("N");
        PendingInteraction pending = new()
        {
            View = new PendingInteractionView
            {
                Id = id,
                Kind = kind == InteractionKind.AskUser ? "ask_user" : "choose_path",
                Intro = Clean(intro, 4000),
                CreatedAt = createdAt,
                ExpiresAt = createdAt.AddSeconds(_config.InteractionTimeoutSeconds),
                Questions = questions,
                Decisions = decisions
            }
        };

        if (!_pending.TryAdd(id, pending))
        {
            return new InteractionResult { Status = "server_error", Note = "Could not allocate an interaction id." };
        }

        SignalChanged();
        InteractionAdded?.Invoke(pending.View);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(_config.InteractionTimeoutSeconds));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            return await pending.Completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new InteractionResult
            {
                Status = "timed_out",
                InteractionId = id,
                Note = "The user did not answer within the configured interaction window. Do not interpret this as refusal. Continue with best judgment when appropriate, or ask again later if the information remains important."
            };
        }
        catch (OperationCanceledException)
        {
            return new InteractionResult
            {
                Status = "cancelled_by_host",
                InteractionId = id,
                Note = "The MCP host cancelled the pending tool call before the user answered."
            };
        }
        finally
        {
            _pending.TryRemove(id, out _);
            SignalChanged();
        }
    }

    internal bool SubmitAnswers(string interactionId, IReadOnlyDictionary<string, string>? answers)
    {
        if (!_pending.TryGetValue(interactionId, out PendingInteraction? pending))
        {
            return false;
        }

        Dictionary<string, string> normalized = new(StringComparer.Ordinal);
        if (answers is not null)
        {
            foreach ((string key, string value) in answers)
            {
                string cleanKey = Clean(key, 128) ?? string.Empty;
                if (cleanKey.Length == 0)
                {
                    continue;
                }

                normalized[cleanKey] = Clean(value, 16000) ?? string.Empty;
            }
        }

        bool completed = pending.Completion.TrySetResult(new InteractionResult
        {
            Status = "answered",
            InteractionId = interactionId,
            Answers = normalized
        });

        if (completed)
        {
            SignalChanged();
        }

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
            Status = "user_cancelled",
            InteractionId = interactionId,
            Note = "The user dismissed this interaction without supplying answers."
        });

        if (completed)
        {
            SignalChanged();
        }

        return completed;
    }

    internal ProgressReceipt AddProgress(string summary, string? completed, string? nextStep, string? notableDiscovery)
    {
        ProgressReportView report = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            Summary = Clean(summary, 6000) ?? string.Empty,
            Completed = Clean(completed, 6000),
            NextStep = Clean(nextStep, 6000),
            NotableDiscovery = Clean(notableDiscovery, 6000)
        };

        lock (_progressGate)
        {
            _progress.Add(report);
            int overflow = _progress.Count - _config.MaxRecentProgressReports;
            if (overflow > 0)
            {
                _progress.RemoveRange(0, overflow);
            }
        }

        SignalChanged();
        ProgressAdded?.Invoke(report);
        return new ProgressReceipt { Status = "received", ReportId = report.Id };
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
            Version = Version,
            Pending = pending,
            Progress = progress
        };
    }

    internal async Task<long> WaitForChangeAsync(long sinceVersion, TimeSpan maximumWait, CancellationToken cancellationToken)
    {
        if (Version != sinceVersion)
        {
            return Version;
        }

        Task<long> waitTask;
        lock (_changeGate)
        {
            if (Version != sinceVersion)
            {
                return Version;
            }

            waitTask = _changed.Task;
        }

        try
        {
            return await waitTask.WaitAsync(maximumWait, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return Version;
        }
    }

    internal void CancelAll(string reason)
    {
        foreach ((string id, PendingInteraction pending) in _pending)
        {
            pending.Completion.TrySetResult(new InteractionResult
            {
                Status = "backend_stopped",
                InteractionId = id,
                Note = reason
            });
        }

        SignalChanged();
    }

    private void SignalChanged()
    {
        TaskCompletionSource<long> previous;
        long next;

        lock (_changeGate)
        {
            next = Interlocked.Increment(ref _version);
            previous = _changed;
            _changed = NewChangeSource();
        }

        previous.TrySetResult(next);
    }

    private static TaskCompletionSource<long> NewChangeSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

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
