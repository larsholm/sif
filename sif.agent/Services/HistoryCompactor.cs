using System.Text;
using Spectre.Console;

namespace sif.agent.Services;

/// <summary>
/// Compacts chat history by summarizing older messages into the context store
/// when the conversation exceeds a configured token threshold.
/// </summary>
internal static class HistoryCompactor
{
    /// <summary>
    /// When chat history size exceeds CompactionThreshold, call the LLM to summarize
    /// the conversation, store the summary in ContextStore, and replace old history
    /// with system prompt + summary reference + most recent messages.
    /// Returns true if compaction was performed.
    /// </summary>
    public static async Task<bool> MaybeCompactAsync(List<ChatMessage> history, AgentClient client, AgentConfig config, bool hasContextTool, CancellationToken cancellationToken = default)
    {
        const int RecentMessageCount = 4;
        const int MaxCompactionChunkChars = 48000;
        const int ChunkSummaryMaxOutputTokens = 1024;
        const int MergedSummaryMaxOutputTokens = 2048;
        const string CompactionSystemMarker = "--- Compacted conversation context ---\n";

        // Compaction disabled if threshold is 0 or negative
        if (config.CompactionThreshold <= 0)
            return false;

        // Need context tool to store summaries
        if (!hasContextTool)
            return false;

        // Need at least a few messages to be worth compacting
        if (history.Count < 5)
            return false;

        // Keep the configured threshold compatible with the historical chars/4
        // estimate, but also protect the actual request budget using all messages,
        // tool schemas, conservative token estimation, and reserved model output.
        // Stored out-of-band context is intentionally excluded: compaction moves
        // data there, so counting it would immediately retrigger compaction.
        var chars = history.Sum(m => m.Content.Length);
        var estimatedHistoryTokens = (chars + 3) / 4;
        var requestBudget = client.EstimateRequestBudget(history, config.DetectedContextLength);
        var thresholdExceeded = estimatedHistoryTokens >= config.CompactionThreshold;
        var requestBudgetExceeded = requestBudget.AvailableInputTokens.HasValue &&
                                    requestBudget.EstimatedInputTokens >= requestBudget.AvailableInputTokens.Value;

        if (!thresholdExceeded && !requestBudgetExceeded)
            return false;

        // Find the system prompt
        var systemIdx = history.FindIndex(m => m.Role == "system");
        var systemPrompt = systemIdx >= 0 ? history[systemIdx].Content : "";
        var markerIndex = systemPrompt.IndexOf(CompactionSystemMarker, StringComparison.Ordinal);
        var baseSystemPrompt = markerIndex >= 0 ? systemPrompt[..markerIndex] : systemPrompt;

        var nonSystemMessages = history.Where(m => m.Role != "system").ToList();
        var (messagesToSummarize, recentMessages) = PartitionMessagesForCompaction(
            nonSystemMessages,
            RecentMessageCount);

        if (messagesToSummarize.Count == 0)
            return false;

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

            foreach (var msg in messagesToSummarize)
            {
                var formatted = $"[{msg.Role}]\n{msg.Content}\n\n";
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

        var chunks = BuildCompactionChunks();
        var contentToSummarize = string.Concat(chunks);
        var reason = requestBudgetExceeded
            ? $"request ~{requestBudget.EstimatedInputTokens / 1000.0:0.0}k + {requestBudget.ReservedOutputTokens / 1000.0:0.0}k reserved output / {requestBudget.ContextLength!.Value / 1000.0:0.0}k context"
            : $"history ~{estimatedHistoryTokens / 1000.0:0.0}k, threshold {config.CompactionThreshold / 1000.0:0.0}k";
        AnsiConsole.MarkupLine($"[dim]Compacting history ({reason}, {chunks.Count:N0} chunk(s))...[/]");

        try
        {
            var summary = await SummarizeChunksAsync(chunks);

            // Store both the raw compacted history and the summary. Do not clear the
            // context store here; recent messages may still reference older entries.
            var storedEntry = ContextStore.Store("chat history pre-compaction", contentToSummarize);
            var summaryEntry = ContextStore.Store("conversation summary (compaction)", summary);

            // Build the new history: system + summary reference + recent messages
            var newHistory = new List<ChatMessage>();

            var compactionNote =
                $"Previous conversation compacted. Continue using this summary as prior context.\n" +
                $"Context store ids: summary={summaryEntry.Id}, raw_history={storedEntry.Id}. Use ctx_read with those ids if details are needed.\n\n" +
                $"Conversation summary:\n{summary}";
            var compactedSystemPrompt = string.IsNullOrWhiteSpace(baseSystemPrompt)
                ? CompactionSystemMarker + compactionNote
                : baseSystemPrompt.TrimEnd() + "\n\n" + CompactionSystemMarker + compactionNote;
            newHistory.Add(new ChatMessage("system", compactedSystemPrompt));

            // Add recent messages
            foreach (var msg in recentMessages)
                newHistory.Add(new ChatMessage(msg.Role, msg.Content));

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
            AnsiConsole.MarkupLine($"[yellow]Compaction failed ({ex.Message}), continuing with existing history.[/]");
            return false;
        }
    }

    /// <summary>
    /// Split non-system history into summarized and retained messages. In addition
    /// to the recent tail, retain the newest user message so tool-heavy turns do
    /// not compact into a system/assistant-only request. Some OpenAI-compatible
    /// providers reject such requests, and the user message is also the clearest
    /// statement of the active task.
    /// </summary>
    internal static (List<ChatMessage> MessagesToSummarize, List<ChatMessage> RecentMessages)
        PartitionMessagesForCompaction(IReadOnlyList<ChatMessage> messages, int recentMessageCount)
    {
        if (recentMessageCount < 0)
            throw new ArgumentOutOfRangeException(nameof(recentMessageCount));

        var firstRecentIndex = Math.Max(0, messages.Count - recentMessageCount);
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
}
