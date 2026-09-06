using System.ComponentModel;
using ModelContextProtocol.Server;

namespace HumanToolCall;

[McpServerToolType]
public sealed class UserCommunicationTools
{
    private readonly InteractionBroker _broker;

    internal UserCommunicationTools(InteractionBroker broker)
    {
        _broker = broker;
    }

//
// ------------------------------------------------ Ask User / askUser
//

    [McpServerTool(
        Name = "askUser",
        Title = "Ask User",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = false)]
    [Description("""
                 Ask the user one question while you are still working on the task, then wait for and return the answer.

                 Use this whenever additional information from the user could materially improve the work: missing facts, requirements, preferences, examples, constraints, intended behavior, environment-specific information, or ambiguity. This is not a last-resort tool. Prefer asking over making an arbitrary assumption when the answer could meaningfully affect the result. This is useful before you start your work, or if you catch any

                 The user may not know every answer. Ask anyway. If the user may respond that they do not know, that is a valid responses.

                 After this tool returns, reassess the task immediately. If another question would help, call askUser or choosePath again right away. Receiving one response never implies that questioning is complete, and there is no intended maximum number of question rounds per task.
                 """)]
    public async Task<string> askUserAsync(
        [Description("The question to ask the user.")]
        string question,
        CancellationToken cancellationToken)
    {
        question = InteractionBroker.Clean(question, 8000) ?? string.Empty;
        if (question.Length == 0)
        {
            return "askUser requires a question.";
        }

        InteractionResult result = await _broker.AskAsync(
            InteractionKind.AskUser,
            question,
            null,
            cancellationToken).ConfigureAwait(false);

        return result.status == "answered"
            ? result.answer ?? string.Empty
            : result.note ?? result.status;
    }

//
// ------------------------------------------------ Choose Path / choosePath
//

    [McpServerTool(
        Name = "choosePath",
        Title = "Choose Path",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = false)]
    [Description("""
                 Allows you to present the user with ≥2 choices to guide your approach.
                 A successful result returns 1 option choiceId.
                 """)]
    public async Task<string> choosePathAsync(
        //cancellationToken is not a model tool and is not model-facing.
        CancellationToken cancellationToken,
        [Description("Describe the decision for the user.")]
        string decision,
        [Description("Use ≥2 choice.")]
        List<ChoiceOption> options,
        [Description("Include the choiceId and describe your reasoning.")]
        string? recommendation = null)
    {
        string? validationError = ValidateDecision(ref decision, options, ref recommendation);
        if (validationError is not null)
        {
            return validationError;
        }

        InteractionResult result = await _broker.AskAsync(
            InteractionKind.ChoosePath,
            null,
            new ChoiceDecision
            {
                decision = decision,
                Options = options,
                recommendation = recommendation
            },
            cancellationToken).ConfigureAwait(false);

        return result.status == "answered"
            ? $"selectedId:{result.answer}"
            : result.note ?? result.status;
    }

//
// ------------------------------------------------ Progress Report / progressReport
//

    [McpServerTool(
        Name = "progressReport",
        Title = "Progress Report",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = false)]
    [Description("""
                 Allows you to update the user during longer running work/tasks.
                 Use for meaningful milestones: beginning or finishing substantial phases, important discoveries, resolution of significant uncertainties, etc. Do not use it to narrate tool calls or small steps, and do not send repetitive updates.
                 A successful result returns "received", not a response from the user.
                 """)]
    public string progressReportAdd(
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
        _broker.progressAdd(cleanedSummary, completed, nextStep, notableDiscovery);
        return "received";
    }

    private static string? ValidateDecision(
        ref string decision,
        IReadOnlyList<ChoiceOption>? options,
        ref string? recommendation)
    {
        decision = InteractionBroker.Clean(decision, 8000) ?? string.Empty;
        if (decision.Length == 0)
        {
            return "choosePath requires a decision.";
        }

        if (options is null || options.Count < 2)
        {
            return "choosePath requires at least two options.";
        }

        HashSet<string> optionIds = new(StringComparer.Ordinal);
        foreach (ChoiceOption option in options)
        {
            option.choiceId = InteractionBroker.Clean(option.choiceId, 128) ?? string.Empty;
            option.choice = InteractionBroker.Clean(option.choice, 6000) ?? string.Empty;

            if (option.choiceId.Length == 0 || option.choice.Length == 0)
            {
                return "Every option requires a non-empty id and choice.";
            }

            if (!optionIds.Add(option.choiceId))
            {
                return $"Option id '{option.choiceId}' is duplicated.";
            }
        }

        recommendation = InteractionBroker.Clean(recommendation, 4000);

        return null;
    }
}