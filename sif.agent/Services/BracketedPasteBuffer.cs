using System.Text;

namespace sif.agent.Services;

internal sealed class BracketedPasteBuffer
{
    private const string EndMarker = "\u001b[201~";
    private readonly StringBuilder _text = new();

    internal bool Append(char value)
    {
        _text.Append(value);

        if (_text.Length < EndMarker.Length)
            return false;

        var markerStart = _text.Length - EndMarker.Length;
        for (var index = 0; index < EndMarker.Length; index++)
        {
            if (_text[markerStart + index] != EndMarker[index])
                return false;
        }

        _text.Length = markerStart;
        return true;
    }

    internal string GetText() =>
        _text.ToString().Replace("\r\n", "\n").Replace('\r', '\n');
}
