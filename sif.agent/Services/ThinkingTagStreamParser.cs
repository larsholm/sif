namespace sif.agent.Services;

internal readonly record struct StreamTextSegment(string Text, bool IsReasoning);

internal sealed class ThinkingTagStreamParser
{
    private static readonly (string Text, bool StartsReasoning)[] Markers =
    [
        ("<thinking>", true),
        ("</thinking>", false),
        ("<thought>", true),
        ("</thought>", false),
        ("<reasoning>", true),
        ("</reasoning>", false),
        ("<think>", true),
        ("</think>", false),
    ];

    private string _pending = "";
    private bool _insideReasoning;

    internal IReadOnlyList<StreamTextSegment> Append(string text)
    {
        _pending += text;
        return Drain(final: false);
    }

    internal IReadOnlyList<StreamTextSegment> Complete() => Drain(final: true);

    private IReadOnlyList<StreamTextSegment> Drain(bool final)
    {
        var segments = new List<StreamTextSegment>();

        while (_pending.Length > 0)
        {
            var markerIndex = FindNextMarker(_pending, out var marker);
            if (markerIndex >= 0)
            {
                AddSegment(segments, _pending[..markerIndex], _insideReasoning);
                _pending = _pending[(markerIndex + marker.Text.Length)..];
                _insideReasoning = marker.StartsReasoning;
                continue;
            }

            var retainedLength = final ? 0 : GetPartialMarkerSuffixLength(_pending);
            var emittedLength = _pending.Length - retainedLength;
            AddSegment(segments, _pending[..emittedLength], _insideReasoning);
            _pending = _pending[emittedLength..];
            break;
        }

        return segments;
    }

    private static int FindNextMarker(string text, out (string Text, bool StartsReasoning) marker)
    {
        var earliestIndex = -1;
        marker = default;

        foreach (var candidate in Markers)
        {
            var index = text.IndexOf(candidate.Text, StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && (earliestIndex < 0 || index < earliestIndex))
            {
                earliestIndex = index;
                marker = candidate;
            }
        }

        return earliestIndex;
    }

    private static int GetPartialMarkerSuffixLength(string text)
    {
        var maximumLength = 0;

        foreach (var marker in Markers)
        {
            var candidateLength = Math.Min(text.Length, marker.Text.Length - 1);
            for (; candidateLength > maximumLength; candidateLength--)
            {
                if (text.EndsWith(marker.Text[..candidateLength], StringComparison.OrdinalIgnoreCase))
                {
                    maximumLength = candidateLength;
                    break;
                }
            }
        }

        return maximumLength;
    }

    private static void AddSegment(List<StreamTextSegment> segments, string text, bool isReasoning)
    {
        if (text.Length == 0)
            return;

        if (segments.Count > 0 && segments[^1].IsReasoning == isReasoning)
        {
            var previous = segments[^1];
            segments[^1] = previous with { Text = previous.Text + text };
            return;
        }

        segments.Add(new StreamTextSegment(text, isReasoning));
    }
}
