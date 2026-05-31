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

        // Estimate chat history size in tokens. Stored context is intentionally excluded:
        // compaction moves data there, so counting it would immediately retrigger compaction.
        var chars = history.Sum(m => m.Content.Length);
        var estimatedTokens = chars / 4;

        // Check if we've crossed the threshold
        if (estimatedTokens < config.CompactionThreshold)
            return false;

        // Find the system prompt
        var systemIdx = history.FindIndex(m => m.Role == "system");
        var systemPrompt = systemIdx >= 0 ? history[systemIdx].Content : "";
        var markerIndex = systemPrompt.IndexOf(CompactionSystemMarker, StringComparison.Ordinal);
        var baseSystemPrompt = markerIndex >= 0 ? systemPrompt[..markerIndex] : systemPrompt;

        var nonSystemMessages = history.Where(m => m.Role != "system").ToList();
        var keepCount = Math.Min(RecentMessageCount, nonSystemMessages.Count);
        var recentMessages = nonSystemMessages.TakeLast(keepCount).ToList();

        // Build content to summarize: all non-system messages except the recent ones
        var messagesToSummarize = nonSystemMessages.Take(nonSystemMessages.Count - keepCount).ToList();

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

        async Task<string> SummarizeChunkAsync(string content, string focus)
        {
            var prompt = $@"Summarize this conversation history for compaction.
Preserve decisions, facts, user preferences, unresolved tasks, code/file changes, tool results, ids, paths, errors, and assumptions needed to continue the conversation.
Focus: {focus}

Conversation:
{content}";

            var (summary, _) = await client.CompleteAsync(
                prompt,
                "You compact chat history. Produce a concise but complete continuation summary. Do not invent facts.");
            return summary.Trim();
        }

        async Task<string> SummarizeChunksAsync(List<string> chunks)
        {
            var summaries = new List<string>();
            for (int i = 0; i < chunks.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var focus = chunks.Count == 1 ? "complete conversation state" : $"chunk {i + 1} of {chunks.Count}";
                summaries.Add(await SummarizeChunkAsync(chunks[i], focus));
            }

            while (summaries.Count > 1)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
                    return await SummarizeChunkAsync(combinedChunks[0], "merge all chunk summaries into one continuation summary");

                summaries.Clear();
                for (int i = 0; i < combinedChunks.Count; i++)
                    summaries.Add(await SummarizeChunkAsync(combinedChunks[i], $"merge summary chunk {i + 1} of {combinedChunks.Count}"));
            }

            return summaries[0];
        }

        var chunks = BuildCompactionChunks();
        var contentToSummarize = string.Concat(chunks);
        AnsiConsole.MarkupLine($"[dim]Compacting history ({estimatedTokens / 1000:0.0}k tokens, threshold {config.CompactionThreshold / 1000:0.0}k tokens, {chunks.Count:N0} chunk(s))...[/]");

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
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Compaction failed ({ex.Message}), continuing with existing history.[/]");
            return false;
        }
    }
}
