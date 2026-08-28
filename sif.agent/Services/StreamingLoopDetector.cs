using System.Text;

namespace sif.agent.Services;

/// <summary>
/// Detects substantial exact repetitions at the end of an incrementally received
/// text stream. The thresholds are deliberately conservative: ordinary repeated
/// words are ignored, while a phrase must repeat many times and a paragraph must
/// repeat at least four times before it is considered a generation loop.
/// </summary>
internal sealed class StreamingLoopDetector
{
    internal const int MinimumPatternLength = 24;
    internal const int MinimumRepetitions = 4;
    internal const int MinimumRepeatedCharacters = 480;

    private const int MaximumPatternLength = 4096;
    private const int MaximumBufferedCharacters = 32768;
    private const int ScanInterval = 64;

    private readonly StringBuilder _buffer = new();
    private int _charactersSinceLastScan;

    internal StreamRepetition? Append(string text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        _buffer.Append(text);
        _charactersSinceLastScan += text.Length;

        if (_buffer.Length > MaximumBufferedCharacters)
            _buffer.Remove(0, _buffer.Length - MaximumBufferedCharacters);

        if (_buffer.Length < MinimumRepeatedCharacters ||
            _charactersSinceLastScan < ScanInterval)
        {
            return null;
        }

        _charactersSinceLastScan = 0;
        return FindRepeatedSuffix();
    }

    private StreamRepetition? FindRepeatedSuffix()
    {
        var text = _buffer.ToString();
        var maximumPatternLength = Math.Min(MaximumPatternLength, text.Length / MinimumRepetitions);

        for (var patternLength = MinimumPatternLength;
             patternLength <= maximumPatternLength;
             patternLength++)
        {
            var requiredRepetitions = Math.Max(
                MinimumRepetitions,
                (MinimumRepeatedCharacters + patternLength - 1) / patternLength);
            var requiredLength = patternLength * requiredRepetitions;
            if (requiredLength > text.Length)
                continue;

            var patternStart = text.Length - patternLength;
            var pattern = text.AsSpan(patternStart, patternLength);
            if (IsWhitespaceOnly(pattern))
                continue;

            var repetitions = 1;
            var comparisonStart = patternStart - patternLength;
            while (comparisonStart >= 0 &&
                   text.AsSpan(comparisonStart, patternLength).SequenceEqual(pattern))
            {
                repetitions++;
                comparisonStart -= patternLength;
            }

            if (repetitions >= requiredRepetitions)
                return new StreamRepetition(patternLength, repetitions);
        }

        return null;
    }

    private static bool IsWhitespaceOnly(ReadOnlySpan<char> text)
    {
        foreach (var character in text)
        {
            if (!char.IsWhiteSpace(character))
                return false;
        }

        return true;
    }
}

internal readonly record struct StreamRepetition(int PatternLength, int Repetitions)
{
    internal int RepeatedCharacters => PatternLength * Repetitions;
}

internal sealed class ReasoningLoopDetectedException(StreamRepetition repetition)
    : InvalidOperationException(
        $"Sif stopped generation after detecting a repeating loop in the reasoning stream " +
        $"({repetition.PatternLength:N0}-character sequence repeated {repetition.Repetitions:N0} times).")
{
}
