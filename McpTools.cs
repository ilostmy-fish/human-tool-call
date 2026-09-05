using System.ComponentModel;
using ModelContextProtocol.Server;

namespace HumanToolCall;

[McpServerToolType]
public sealed class UserCommunicationTools
{
    internal const string ServerInstructions = """
Human Tool Call provides explicit user-communication tools during an ongoing task.

These tools are normal workflow tools, not last-resort fallbacks. Use them whenever user input would materially improve correctness, clarify requirements, choose among legitimate implementation paths, or keep the user informed during longer work.

There is no expected or implicit limit of one or two user-question rounds per task. After receiving an answer, immediately reassess the task. If another question would be useful, call ask_user or choose_path again immediately. Repeat as many times as useful.

Do not avoid asking merely because the user may not know the answer. "I don't know", uncertainty, and partial answers are valid and useful information. Never invent an answer on the user's behalf merely to avoid asking.

Batch independent questions in one call when convenient. Ask sequentially when a later question depends on an earlier answer.

ask_user and choose_path are blocking: after calling one, wait for its result. A successful result contains the user's answers. A timeout or cancellation is information about the interaction, not an answer to the questions.

progress_report is non-blocking: after it returns received, continue the current task immediately. Use it for meaningful milestones, changed plans, important discoveries, or the beginning/end of a substantial phase. Do not narrate every routine tool call.
""";

    private readonly InteractionBroker _broker;
    private readonly BackendConfig _config;

    internal UserCommunicationTools(InteractionBroker broker, BackendConfig config)
    {
        _broker = broker;
        _config = config;
    }

    [McpServerTool(
        Name = "ask_user",
        Title = "Ask User",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("""
Ask the user one or more questions while you are still working on the task, then wait for their answers and return them as structured tool output.

Use this whenever additional information from the user could materially improve the work: missing facts, requirements, preferences, examples, constraints, intended behavior, environment-specific information, or ambiguity. This is not a last-resort tool. Prefer asking over making an arbitrary assumption when the answer could meaningfully affect the result.

The user is not expected to know every answer. Do not suppress a useful question because the user may respond that they do not know, are unsure, or only know part of the answer. Those are valid responses.

You may ask many independent questions in one call. Batch them when that reduces unnecessary round trips. Ask sequentially when a later question depends on an earlier answer.

After this tool returns, reassess the task immediately. If another question would help, call ask_user or choose_path again right away. Receiving one response never implies that questioning is complete, and there is no intended maximum number of question rounds per task.
""")]
    public async Task<InteractionResult> AskUserAsync(
        [Description("Optional short introduction explaining what you need from the user.")]
        string? intro,
        [Description("One or more questions. Give each question a stable id so its answer can be matched unambiguously.")]
        List<UserQuestion> questions,
        CancellationToken cancellationToken)
    {
        string? validationError = ValidateQuestions(questions);
        if (validationError is not null)
        {
            return Invalid(validationError);
        }

        return await _broker.AskAsync(
            InteractionKind.AskUser,
            intro,
            questions,
            null,
            cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(
        Name = "choose_path",
        Title = "Choose Path",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("""
Ask the user to choose among two or more legitimate implementation or decision paths, then wait for and return the user's selections.

Use this when a decision depends substantially on user preference, desired tradeoffs, architecture, scope, compatibility, risk tolerance, maintenance cost, UX, or another judgment that should not be silently made on the user's behalf. Explain each option well enough for an informed choice, including meaningful pros and cons when useful. You may state a recommendation, but a recommendation does not replace asking.

You may include multiple independent decisions in one call. The user may choose an option, provide a custom answer, ask for your recommendation, or say that they do not know.

After this tool returns, reassess the task immediately. You may call choose_path or ask_user again right away. Receiving one response never implies that all decisions or questions are finished, and there is no intended maximum number of feedback rounds per task.
""")]
    public async Task<InteractionResult> ChoosePathAsync(
        [Description("Optional short introduction explaining why these decisions are being presented.")]
        string? intro,
        [Description("One or more independent decisions, each with at least two concrete options.")]
        List<ChoiceDecision> decisions,
        CancellationToken cancellationToken)
    {
        string? validationError = ValidateDecisions(decisions);
        if (validationError is not null)
        {
            return Invalid(validationError);
        }

        return await _broker.AskAsync(
            InteractionKind.ChoosePath,
            intro,
            null,
            decisions,
            cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(
        Name = "progress_report",
        Title = "Progress Report",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("""
Send a brief user-visible progress update during ongoing work. This tool does not request an answer. After it returns status=received, continue the current task immediately.

Use it for meaningful milestones: beginning or finishing a substantial phase, an important discovery that changes the plan, resolution of a significant uncertainty, or a concise statement of what happens next. Do not use it to narrate routine tool calls or small internal steps, and do not send repetitive updates merely because the tool is available.
""")]
    public ProgressReceipt ProgressReport(
        [Description("Concise user-facing summary of the current milestone or state.")]
        string summary,
        [Description("Optional concise description of what has just been completed.")]
        string? completed = null,
        [Description("Optional concise description of the next substantial step.")]
        string? nextStep = null,
        [Description("Optional important discovery that materially affects the plan or result.")]
        string? notableDiscovery = null)
    {
        string cleanedSummary = InteractionBroker.Clean(summary, 6000) ?? "Progress update";
        return _broker.AddProgress(cleanedSummary, completed, nextStep, notableDiscovery);
    }

    private string? ValidateQuestions(IReadOnlyList<UserQuestion>? questions)
    {
        if (questions is null || questions.Count == 0)
        {
            return "ask_user requires at least one question.";
        }

        if (questions.Count > _config.MaxQuestionsPerInteraction)
        {
            return $"ask_user accepts at most {_config.MaxQuestionsPerInteraction} questions in one call.";
        }

        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (UserQuestion question in questions)
        {
            question.Id = InteractionBroker.Clean(question.Id, 128) ?? string.Empty;
            question.Question = InteractionBroker.Clean(question.Question, 8000) ?? string.Empty;
            question.Context = InteractionBroker.Clean(question.Context, 8000);
            question.WhyItMatters = InteractionBroker.Clean(question.WhyItMatters, 4000);

            if (question.Id.Length == 0 || question.Question.Length == 0)
            {
                return "Every question requires a non-empty id and question.";
            }

            if (!ids.Add(question.Id))
            {
                return $"Question id '{question.Id}' is duplicated.";
            }
        }

        return null;
    }

    private string? ValidateDecisions(IReadOnlyList<ChoiceDecision>? decisions)
    {
        if (decisions is null || decisions.Count == 0)
        {
            return "choose_path requires at least one decision.";
        }

        if (decisions.Count > _config.MaxQuestionsPerInteraction)
        {
            return $"choose_path accepts at most {_config.MaxQuestionsPerInteraction} decisions in one call.";
        }

        HashSet<string> decisionIds = new(StringComparer.Ordinal);
        foreach (ChoiceDecision decision in decisions)
        {
            decision.Id = InteractionBroker.Clean(decision.Id, 128) ?? string.Empty;
            decision.Question = InteractionBroker.Clean(decision.Question, 8000) ?? string.Empty;
            decision.Context = InteractionBroker.Clean(decision.Context, 8000);
            decision.RecommendedOptionId = InteractionBroker.Clean(decision.RecommendedOptionId, 128);
            decision.RecommendationReason = InteractionBroker.Clean(decision.RecommendationReason, 4000);

            if (decision.Id.Length == 0 || decision.Question.Length == 0)
            {
                return "Every decision requires a non-empty id and question.";
            }

            if (!decisionIds.Add(decision.Id))
            {
                return $"Decision id '{decision.Id}' is duplicated.";
            }

            if (decision.Options is null || decision.Options.Count < 2)
            {
                return $"Decision '{decision.Id}' requires at least two options.";
            }

            HashSet<string> optionIds = new(StringComparer.Ordinal);
            foreach (ChoiceOption option in decision.Options)
            {
                option.Id = InteractionBroker.Clean(option.Id, 128) ?? string.Empty;
                option.Label = InteractionBroker.Clean(option.Label, 1000) ?? string.Empty;
                option.Description = InteractionBroker.Clean(option.Description, 6000) ?? string.Empty;

                if (option.Id.Length == 0 || option.Label.Length == 0 || option.Description.Length == 0)
                {
                    return $"Every option in decision '{decision.Id}' requires a non-empty id, label, and description.";
                }

                if (!optionIds.Add(option.Id))
                {
                    return $"Option id '{option.Id}' is duplicated within decision '{decision.Id}'.";
                }
            }

            if (decision.RecommendedOptionId is not null && !optionIds.Contains(decision.RecommendedOptionId))
            {
                return $"recommendedOptionId '{decision.RecommendedOptionId}' does not match an option in decision '{decision.Id}'.";
            }
        }

        return null;
    }

    private static InteractionResult Invalid(string note) => new()
    {
        Status = "invalid_request",
        Note = note
    };
}
