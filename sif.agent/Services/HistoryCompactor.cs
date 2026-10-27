using System.Text;
using Spectre.Console;

namespace sif.agent.Services;

/// <summary>
/// Compacts chat history by summarizing older messages into the context store
/// when the conversation exceeds a configured token threshold.
/// </summary>
internal static class HistoryCompactor
{
    private const int RecentMessageCount = 4;
    private const int RetainedBudgetPercent = 30;
    private const int ForcedRetainedBudgetPercent = 15;
    private const int MinRetainedMessageChars = 2000;
    private const int MaxCompactionChunkChars = 48000;
    private const int ChunkSummaryMaxOutputTokens = 1024;
    private const int MergedSummaryMaxOutputTokens = 2048;
    private const string CompactionSystemMarker = "--- Compacted conversation context ---\n";

    private sealed record CompactionPlan(
        string BaseSystemPrompt,
        string? PreviousSummary,
        List<ChatMessage> MessagesToSummarize,
        List<ChatMessage> RecentMessages,
        int[] RecentCharacterLimits)
    {
        public bool HasOversizedRecentMessages =>
            RecentMessages.Where((message, i) => message.Content.Length > RecentCharacterLimits[i]).Any();
    }

    /// <summary>
    /// When chat history size exceeds CompactionThreshold, call the LLM to summarize
    /// the conversation, store the summary in ContextStore, and replace old history
    /// with system prompt + summary reference + most recent messages.
    /// <paramref name="force"/> compacts regardless of thresholds, used after the
    /// provider reported a full context window, and keeps less recent history.
    /// Returns true if compaction was performed.
    /// </summary>
    public static async Task<bool> MaybeCompactAsync(List<ChatMessage> history, AgentClient client, AgentConfig config, bool hasContextTool, CancellationToken cancellationToken = default, bool force = false)
    {
        // Compaction disabled if threshold is 0 or negative
        if (config.CompactionThreshold <= 0)
            return false;

        // Need context tool to store summaries
        if (!hasContextTool)
            return false;

        // Need at least a few messages to be worth compacting
        if (history.Count < 5)
            return false;

        // Compare the configured threshold with the history size in tokens, as
        // calibrated from the provider's reported prompt usage (chars/4 until the
        // first response). Also protect the actual request budget using all
        // messages, tool schemas, conservative token estimation, and reserved
        // model output. Stored out-of-band context is intentionally excluded:
        // compaction moves data there, so counting it would immediately
        // retrigger compaction.
        var estimatedHistoryTokens = client.EstimateHistoryTokens(history);
        var requestBudget = client.EstimateRequestBudget(history, config.DetectedContextLength);
        var thresholdExceeded = estimatedHistoryTokens >= config.CompactionThreshold;
        var requestBudgetExceeded = requestBudget.AvailableInputTokens.HasValue &&
                                    requestBudget.EstimatedInputTokens >= requestBudget.AvailableInputTokens.Value;

        if (!force && !thresholdExceeded && !requestBudgetExceeded)
            return false;

        var plan = PlanCompaction(history, client, config, force ? ForcedRetainedBudgetPercent : RetainedBudgetPercent);
        if (plan is null)
            return false;

        var reason = force && !thresholdExceeded && !requestBudgetExceeded
            ? $"context window full at {client.LearnedContextLength ?? client.LastPromptTokens ?? 0:N0} tokens"
            : requestBudgetExceeded
            ? $"request ~{requestBudget.EstimatedInputTokens / 1000.0:0.0}k + {requestBudget.ReservedOutputTokens / 1000.0:0.0}k reserved output / {requestBudget.ContextLength!.Value / 1000.0:0.0}k context"
            : $"history ~{estimatedHistoryTokens / 1000.0:0.0}k, threshold {config.CompactionThreshold / 1000.0:0.0}k";
        return await ExecuteCompactionAsync(history, client, config, plan, reason, cancellationToken);
    }

    /// <summary>
    /// Compact on request, regardless of thresholds and of whether automatic
    /// compaction is disabled. Returns true if compaction was performed.
    /// </summary>
    public static async Task<bool> CompactNowAsync(List<ChatMessage> history, AgentClient client, AgentConfig config, CancellationToken cancellationToken = default)
    {
        var plan = PlanCompaction(history, client, config, RetainedBudgetPercent);
        if (plan is null)
        {
            AnsiConsole.MarkupLine("[dim]Nothing to compact.[/]\n");
            return false;
        }

        return await ExecuteCompactionAsync(history, client, config, plan, "requested", cancellationToken);
    }

    private static CompactionPlan? PlanCompaction(List<ChatMessage> history, AgentClient client, AgentConfig config, int retainedBudgetPercent)
    {
        // Find the system prompt
        var systemIdx = history.FindIndex(m => m.Role == "system");
        var systemPrompt = systemIdx >= 0 ? history[systemIdx].Content : "";
        var markerIndex = systemPrompt.IndexOf(CompactionSystemMarker, StringComparison.Ordinal);
        var baseSystemPrompt = markerIndex >= 0 ? systemPrompt[..markerIndex] : systemPrompt;
        var previousSummary = markerIndex >= 0 ? systemPrompt[(markerIndex + CompactionSystemMarker.Length)..] : null;

        var requestBudget = client.EstimateRequestBudget(history, config.DetectedContextLength);
        var retainedCharacterBudget = RetainedCharacterBudget(
            requestBudget.AvailableInputTokens,
            config.CompactionThreshold,
            client.RequestCharactersPerToken,
            retainedBudgetPercent);

        var nonSystemMessages = history.Where(m => m.Role != "system").ToList();
        var (messagesToSummarize, recentMessages) = PartitionMessagesForCompaction(
            nonSystemMessages,
            RecentMessageCount,
            retainedCharacterBudget);
        var plan = new CompactionPlan(
            baseSystemPrompt,
            previousSummary,
            messagesToSummarize,
            recentMessages,
            AllocateRetainedCharacters(recentMessages, retainedCharacterBudget));

        return messagesToSummarize.Count > 0 || plan.HasOversizedRecentMessages ? plan : null;
    }

    private static async Task<bool> ExecuteCompactionAsync(
        List<ChatMessage> history,
        AgentClient client,
        AgentConfig config,
        CompactionPlan plan,
        string reason,
        CancellationToken cancellationToken)
    {
        List<string> BuildCompactionChunks()
        {
            var chunks = new List<string>();
            var current = new StringBuilder();

            void Flush()
            {
                if (current.Length == 0)
                    return;

                chunks.Add(current.ToString());
                current.Clear();
            }

            // A re-compaction replaces the previous summary in the system prompt,
            // so carry it into the new summary instead of dropping it.
            var sections = plan.MessagesToSummarize.Select(msg => $"[{msg.Role}]\n{msg.Content}\n\n");
            if (!string.IsNullOrWhiteSpace(plan.PreviousSummary))
                sections = sections.Prepend($"[previous compaction summary]\n{plan.PreviousSummary}\n\n");

            foreach (var formatted in sections)
            {
                if (formatted.Length > MaxCompactionChunkChars)
                {
                    Flush();
                    for (int offset = 0; offset < formatted.Length; offset += MaxCompactionChunkChars)
                        chunks.Add(formatted.Substring(offset, Math.Min(MaxCompactionChunkChars, formatted.Length - offset)));
                    continue;
                }

                if (current.Length + formatted.Length > MaxCompactionChunkChars)
                    Flush();

                current.Append(formatted);
            }

            Flush();
            return chunks;
        }

        async Task<string> SummarizeChunkAsync(string content, string focus, int maxOutputTokens)
        {
            var prompt = $@"Summarize this conversation history for compaction.
Preserve decisions, facts, user preferences, unresolved tasks, code/file changes, tool results, ids, paths, errors, and assumptions needed to continue the conversation.
Be concise and fit the complete summary within the available output budget.
Focus: {focus}

Conversation:
{content}";

            var summary = await client.CompleteCompactionAsync(
                prompt,
                "You compact chat history. Produce a concise but complete continuation summary. Do not invent facts.",
                maxOutputTokens,
                cancellationToken);
            return summary.Trim();
        }

        async Task<string> SummarizeChunksAsync(List<string> chunks)
        {
            var summaries = new List<string>();
            for (int i = 0; i < chunks.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var focus = chunks.Count == 1 ? "complete conversation state" : $"chunk {i + 1} of {chunks.Count}";
                AnsiConsole.MarkupLine($"[dim]Compacting history: summarizing chunk {i + 1:N0}/{chunks.Count:N0}...[/]");
                summaries.Add(await SummarizeChunkAsync(chunks[i], focus, ChunkSummaryMaxOutputTokens));
            }

            var mergeRound = 0;
            while (summaries.Count > 1)
            {
                cancellationToken.ThrowIfCancellationRequested();
                mergeRound++;
                var combinedChunks = new List<string>();
                var current = new StringBuilder();

                foreach (var summary in summaries)
                {
                    var formatted = summary + "\n\n";
                    if (current.Length + formatted.Length > MaxCompactionChunkChars)
                    {
                        if (current.Length > 0)
                        {
                            combinedChunks.Add(current.ToString());
                            current.Clear();
                        }
                    }

                    current.Append(formatted);
                }

                if (current.Length > 0)
                    combinedChunks.Add(current.ToString());

                if (combinedChunks.Count == 1)
                {
                    AnsiConsole.MarkupLine($"[dim]Compacting history: merging summaries (round {mergeRound:N0})...[/]");
                    return await SummarizeChunkAsync(
                        combinedChunks[0],
                        "merge all chunk summaries into one continuation summary",
                        MergedSummaryMaxOutputTokens);
                }

                summaries.Clear();
                for (int i = 0; i < combinedChunks.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AnsiConsole.MarkupLine($"[dim]Compacting history: merging summary {i + 1:N0}/{combinedChunks.Count:N0} (round {mergeRound:N0})...[/]");
                    summaries.Add(await SummarizeChunkAsync(
                        combinedChunks[i],
                        $"merge summary chunk {i + 1} of {combinedChunks.Count}",
                        MergedSummaryMaxOutputTokens));
                }
            }

            return summaries[0];
        }

        var chunks = plan.MessagesToSummarize.Count > 0 ? BuildCompactionChunks() : [];
        AnsiConsole.MarkupLine($"[dim]Compacting history ({reason}, {chunks.Count:N0} chunk(s))...[/]");

        try
        {
            var newHistory = new List<ChatMessage>();
            if (chunks.Count > 0)
            {
                var summary = await SummarizeChunksAsync(chunks);

                // Store both the raw compacted history and the summary. Do not clear the
                // context store here; recent messages may still reference older entries.
                var storedEntry = ContextStore.Store("chat history pre-compaction", string.Concat(chunks));
                var summaryEntry = ContextStore.Store("conversation summary (compaction)", summary);

                var compactionNote =
                    $"Previous conversation compacted. Continue using this summary as prior context.\n" +
                    $"Context store ids: summary={summaryEntry.Id}, raw_history={storedEntry.Id}. Use ctx_read with those ids if details are needed.\n\n" +
                    $"Conversation summary:\n{summary}";
                var compactedSystemPrompt = string.IsNullOrWhiteSpace(plan.BaseSystemPrompt)
                    ? CompactionSystemMarker + compactionNote
                    : plan.BaseSystemPrompt.TrimEnd() + "\n\n" + CompactionSystemMarker + compactionNote;
                newHistory.Add(new ChatMessage("system", compactedSystemPrompt));
            }
            else
            {
                // Only oversized recent messages need shrinking; keep the system
                // prompt, including any previous summary, as it is.
                newHistory.AddRange(history.Where(m => m.Role == "system").Take(1));
            }

            // Add recent messages, moving the excess of oversized ones to the
            // context store so the retained tail fits the request budget.
            for (var i = 0; i < plan.RecentMessages.Count; i++)
            {
                var msg = plan.RecentMessages[i];
                var limit = plan.RecentCharacterLimits[i];
                var content = msg.Content;
                if (content.Length > limit)
                {
                    var entry = ContextStore.Store($"{msg.Role} message (compaction)", content);
                    content = content[..limit] +
                              $"\n... ({content.Length - limit:N0} more chars stored as {entry.Id}; use ctx_read with that id for the full text)";
                }

                newHistory.Add(new ChatMessage(msg.Role, content));
            }

            history.Clear();
            history.AddRange(newHistory);

            AnsiConsole.MarkupLine($"[dim]Compaction complete. Reduced history to {history.Count} messages.[/]\n");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var debugPath = DebugLog.Save(
                "history-compaction",
                ex,
                $"model={config.Model}; {reason}; chunks={chunks.Count}");
            AnsiConsole.MarkupLine($"[yellow]Compaction failed ({AgentErrorFormatter.ToUserMessage(ex).EscapeMarkup()}), continuing with existing history.[/]");
            AnsiConsole.MarkupLine($"[dim]Log file: {debugPath.EscapeMarkup()}[/]");
            return false;
        }
    }

    /// <summary>
    /// Character budget for the messages retained after compaction: a share of
    /// the smaller of the compaction threshold and the model's available input
    /// tokens, so the compacted request fits small context windows and does not
    /// immediately cross the threshold again.
    /// </summary>
    internal static int RetainedCharacterBudget(
        int? availableInputTokens,
        int compactionThreshold,
        double charactersPerToken,
        int retainedBudgetPercent)
    {
        long referenceTokens = compactionThreshold > 0 ? compactionThreshold : AgentConfig.DefaultCompactionThreshold;
        if (availableInputTokens is > 0)
            referenceTokens = Math.Min(referenceTokens, availableInputTokens.Value);

        return (int)Math.Min(int.MaxValue, referenceTokens * retainedBudgetPercent / 100 * charactersPerToken);
    }

    /// <summary>
    /// Split non-system history into summarized and retained messages. Retain a
    /// recent tail of at most <paramref name="recentMessageCount"/> messages
    /// that fits <paramref name="recentCharacterBudget"/>, always including the
    /// newest message. In addition, retain the newest user message so tool-heavy
    /// turns do not compact into a system/assistant-only request. Some
    /// OpenAI-compatible providers reject such requests, and the user message is
    /// also the clearest statement of the active task.
    /// </summary>
    internal static (List<ChatMessage> MessagesToSummarize, List<ChatMessage> RecentMessages)
        PartitionMessagesForCompaction(IReadOnlyList<ChatMessage> messages, int recentMessageCount, int recentCharacterBudget)
    {
        if (recentMessageCount < 0)
            throw new ArgumentOutOfRangeException(nameof(recentMessageCount));

        var firstRecentIndex = messages.Count;
        var recentCharacters = 0L;
        while (firstRecentIndex > 0 && messages.Count - firstRecentIndex < recentMessageCount)
        {
            var length = messages[firstRecentIndex - 1].Content.Length;
            if (firstRecentIndex < messages.Count && recentCharacters + length > recentCharacterBudget)
                break;

            recentCharacters += length;
            firstRecentIndex--;
        }

        var newestUserIndex = -1;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (string.Equals(messages[i].Role, "user", StringComparison.OrdinalIgnoreCase))
            {
                newestUserIndex = i;
                break;
            }
        }

        var messagesToSummarize = new List<ChatMessage>();
        var recentMessages = new List<ChatMessage>();
        for (var i = 0; i < messages.Count; i++)
        {
            if (i >= firstRecentIndex || i == newestUserIndex)
                recentMessages.Add(messages[i]);
            else
                messagesToSummarize.Add(messages[i]);
        }

        return (messagesToSummarize, recentMessages);
    }

    /// <summary>
    /// Per-message character limits that fit retained messages into the budget.
    /// The budget is shared evenly, smaller messages keep their full length and
    /// pass their unused share on, and no limit drops below a readable minimum.
    /// </summary>
    internal static int[] AllocateRetainedCharacters(IReadOnlyList<ChatMessage> messages, int characterBudget)
    {
        var limits = new int[messages.Count];
        var remainingBudget = (long)characterBudget;
        var remainingCount = messages.Count;
        foreach (var i in Enumerable.Range(0, messages.Count).OrderBy(i => messages[i].Content.Length))
        {
            var share = remainingBudget / remainingCount;
            limits[i] = (int)Math.Min(messages[i].Content.Length, Math.Max(share, MinRetainedMessageChars));
            remainingBudget = Math.Max(0, remainingBudget - limits[i]);
            remainingCount--;
        }

        return limits;
    }
}
