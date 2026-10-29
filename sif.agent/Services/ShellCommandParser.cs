using System.Text;

namespace sif.agent;

/// <summary>
/// Breaks a shell command line into the simple commands it would run, including commands
/// inside pipelines, lists, subshells, command/process substitutions and heredoc bodies.
/// This is not a complete shell grammar: constructs it cannot follow are reported through
/// <see cref="Result.Unsupported"/> so callers can fall back to asking the user.
/// </summary>
internal sealed class ShellCommandParser
{
    private const int MaxDepth = 32;

    internal readonly record struct ShellWord(string Text, bool Dynamic);

    internal readonly record struct ShellRedirect(string Operator, string Target);

    internal sealed class SimpleCommand
    {
        public List<ShellWord> Words { get; } = [];
        public List<ShellRedirect> Redirects { get; } = [];
        public bool IsEmpty => Words.Count == 0 && Redirects.Count == 0;
    }

    internal sealed record Result(IReadOnlyList<SimpleCommand> Commands, string? Unsupported);

    private readonly string _text;
    private readonly bool _posix;
    private readonly List<SimpleCommand> _commands;
    private readonly List<(string Delimiter, bool Expand, bool StripTabs)> _pendingHeredocs = [];
    private int _pos;
    private int _depth;
    private string? _unsupported;

    private ShellCommandParser(string text, bool posix, List<SimpleCommand> commands, int depth)
    {
        _text = text;
        _posix = posix;
        _commands = commands;
        _depth = depth;
    }

    /// <param name="posix">True for sh/bash syntax; false for PowerShell, where the backtick is an escape character.</param>
    public static Result Parse(string text, bool posix)
    {
        var parser = new ShellCommandParser(text, posix, [], 0);
        parser.ParseSequence(closer: null);
        return new Result(parser._commands, parser._unsupported);
    }

    private char Peek(int offset = 0) =>
        _pos + offset < _text.Length ? _text[_pos + offset] : '\0';

    private void Fail(string reason) => _unsupported ??= reason;

    private void Finish(ref SimpleCommand current)
    {
        if (!current.IsEmpty)
            _commands.Add(current);
        current = new SimpleCommand();
    }

    private void ParseSequence(char? closer)
    {
        if (++_depth > MaxDepth)
        {
            Fail("nesting is too deep");
            _depth--;
            return;
        }

        var current = new SimpleCommand();
        while (_unsupported is null)
        {
            if (_pos >= _text.Length)
            {
                if (closer is not null)
                    Fail($"missing '{closer}'");
                break;
            }

            var c = _text[_pos];
            if (c is ' ' or '\t' or '\r')
            {
                _pos++;
                continue;
            }

            if (_posix && c == '\\' && Peek(1) == '\n')
            {
                _pos += 2;
                continue;
            }

            if (c == closer)
            {
                _pos++;
                break;
            }

            if (c == '\n')
            {
                _pos++;
                Finish(ref current);
                ReadHeredocBodies();
                continue;
            }

            if (c == '&' && Peek(1) == '>')
            {
                ReadRedirect(current);
                continue;
            }

            if (c is ';' or '|' or '&')
            {
                if (c == ';' && Peek(1) == ';')
                {
                    Fail("case statements are not supported");
                    break;
                }

                _pos += Peek(1) == c || (c == '|' && Peek(1) == '&') ? 2 : 1;
                Finish(ref current);
                continue;
            }

            if (c is '<' or '>')
            {
                ReadRedirect(current);
                continue;
            }

            if (c == '(')
            {
                if (Peek(1) == '(' && (current.Words.Count == 0 || current.Words is [{ Text: "for" }]))
                {
                    SkipArithmetic();
                    continue;
                }

                if (current.Words.Count > 0)
                {
                    if (_posix)
                    {
                        Fail("function definitions are not supported");
                        break;
                    }

                    // PowerShell sub-expression used as an argument, e.g. Write-Output (Get-Date).
                    _pos++;
                    ParseSequence(')');
                    current.Words.Add(new ShellWord("(...)", Dynamic: true));
                    continue;
                }

                _pos++;
                ParseSequence(')');
                continue;
            }

            if (c == ')')
            {
                Fail("unbalanced ')'");
                break;
            }

            if (c == '#')
            {
                while (_pos < _text.Length && _text[_pos] != '\n')
                    _pos++;
                continue;
            }

            if (char.IsAsciiDigit(c) && TrySkipFileDescriptorPrefix())
                continue;

            current.Words.Add(ReadWord());
            if (current.Words is [{ Text: "[[" }])
                SkipConditionalExpression(current);
        }

        Finish(ref current);
        _depth--;
    }

    /// <summary>
    /// The body of [[ ... ]] is a test expression, not a command list: operators such as ||, &lt; and
    /// regex parentheses must not split it. Only command substitutions inside it are followed.
    /// </summary>
    private void SkipConditionalExpression(SimpleCommand current)
    {
        var end = _pos;
        while ((end = _text.IndexOf("]]", end, StringComparison.Ordinal)) >= 0)
        {
            var standalone = _text[end - 1] is ' ' or '\t' &&
                (end + 2 >= _text.Length || _text[end + 2] is ' ' or '\t' or '\r' or '\n' or ';' or '&' or '|' or ')');
            if (standalone)
                break;
            end += 2;
        }

        if (end < 0)
        {
            Fail("missing ']]'");
            return;
        }

        ParseChild(_text[_pos..end], substitutionsOnly: true);
        current.Words.Add(new ShellWord("]]", Dynamic: false));
        _pos = end + 2;
    }

    /// <summary>Skips the "2" in "2>&1" so it is not mistaken for a word.</summary>
    private bool TrySkipFileDescriptorPrefix()
    {
        var end = _pos;
        while (end < _text.Length && char.IsAsciiDigit(_text[end]))
            end++;

        if (end >= _text.Length || _text[end] is not ('<' or '>'))
            return false;

        _pos = end;
        return true;
    }

    private void ReadRedirect(SimpleCommand current)
    {
        var start = _pos;
        if (_text[_pos] == '&')
            _pos++;

        if (_pos == start && Peek(1) == '(')
        {
            // Process substitution: <(cmd) or >(cmd) is an argument whose contents run as commands.
            _pos += 2;
            ParseSequence(')');
            current.Words.Add(new ShellWord("<(...)", Dynamic: true));
            return;
        }

        while (_pos < _text.Length && _text[_pos] is '<' or '>' or '&' or '|')
            _pos++;

        var op = _text[start.._pos];
        if (op == "<<" && Peek() == '-')
        {
            _pos++;
            op = "<<-";
        }

        while (Peek() is ' ' or '\t')
            _pos++;

        if (_pos >= _text.Length || _text[_pos] is '\n' or ';' or '|' or '&' or '(' or ')' or '<' or '>')
            return;

        var target = ReadWordValue(out var quoted, out _);
        if (op is "<<" or "<<-")
            _pendingHeredocs.Add((target, Expand: !quoted, StripTabs: op == "<<-"));
        else
            current.Redirects.Add(new ShellRedirect(op, target));
    }

    private void ReadHeredocBodies()
    {
        foreach (var (delimiter, expand, stripTabs) in _pendingHeredocs)
        {
            var body = new StringBuilder();
            while (_pos < _text.Length)
            {
                var lineEnd = _text.IndexOf('\n', _pos);
                if (lineEnd < 0)
                    lineEnd = _text.Length;

                var line = _text[_pos..lineEnd];
                _pos = Math.Min(lineEnd + 1, _text.Length);

                var candidate = stripTabs ? line.TrimStart('\t') : line;
                if (candidate.TrimEnd('\r') == delimiter)
                    break;

                if (expand)
                    body.Append(line).Append('\n');
            }

            if (expand)
                ParseChild(body.ToString(), substitutionsOnly: true);
        }

        _pendingHeredocs.Clear();
    }

    private ShellWord ReadWord()
    {
        var text = ReadWordValue(out _, out var dynamic);
        return new ShellWord(text, dynamic);
    }

    private string ReadWordValue(out bool quoted, out bool dynamic)
    {
        quoted = false;
        dynamic = false;
        var sb = new StringBuilder();

        while (_pos < _text.Length && _unsupported is null)
        {
            var c = _text[_pos];
            if (c is ' ' or '\t' or '\r' or '\n' or ';' or '&' or '|' or '<' or '>' or ')')
                break;

            if (c == '(')
            {
                // Array assignment a=(1 2) or extglob @(x|y): keep the raw text as part of the word.
                if (_posix && sb.Length > 0 && sb[^1] is '=' or '@' or '!' or '+' or '*' or '?')
                {
                    sb.Append(ReadBalancedRaw('(', ')'));
                    continue;
                }

                break;
            }

            switch (c)
            {
                case '\'':
                    quoted = true;
                    sb.Append(ReadSingleQuoted());
                    break;
                case '"':
                    quoted = true;
                    sb.Append(ReadDoubleQuoted(ref dynamic));
                    break;
                case '$':
                    sb.Append(ReadDollar(ref dynamic, ref quoted));
                    break;
                case '`' when _posix:
                    dynamic = true;
                    ReadBacktick();
                    sb.Append("`...`");
                    break;
                case '`':
                case '\\' when _posix:
                    quoted = true;
                    _pos++;
                    if (_pos < _text.Length)
                    {
                        if (_text[_pos] != '\n')
                            sb.Append(_text[_pos]);
                        _pos++;
                    }
                    break;
                default:
                    sb.Append(c);
                    _pos++;
                    break;
            }
        }

        return sb.ToString();
    }

    private string ReadSingleQuoted()
    {
        var sb = new StringBuilder();
        _pos++;
        while (_pos < _text.Length)
        {
            var c = _text[_pos++];
            if (c == '\'')
            {
                // PowerShell escapes a single quote by doubling it.
                if (!_posix && Peek() == '\'')
                {
                    sb.Append('\'');
                    _pos++;
                    continue;
                }

                return sb.ToString();
            }

            sb.Append(c);
        }

        Fail("unterminated single quote");
        return sb.ToString();
    }

    private string ReadDoubleQuoted(ref bool dynamic)
    {
        var sb = new StringBuilder();
        _pos++;
        while (_pos < _text.Length && _unsupported is null)
        {
            var c = _text[_pos];
            if (c == '"')
            {
                if (!_posix && Peek(1) == '"')
                {
                    sb.Append('"');
                    _pos += 2;
                    continue;
                }

                _pos++;
                return sb.ToString();
            }

            if ((_posix && c == '\\') || (!_posix && c == '`'))
            {
                var next = Peek(1);
                if (!_posix || next is '$' or '`' or '"' or '\\' or '\n')
                {
                    if (next != '\n')
                        sb.Append(next);
                    _pos += 2;
                    continue;
                }

                sb.Append(c);
                _pos++;
                continue;
            }

            if (c == '$')
            {
                var ignored = false;
                sb.Append(ReadDollar(ref dynamic, ref ignored, inDoubleQuotes: true));
                continue;
            }

            if (_posix && c == '`')
            {
                dynamic = true;
                ReadBacktick();
                sb.Append("`...`");
                continue;
            }

            sb.Append(c);
            _pos++;
        }

        Fail("unterminated double quote");
        return sb.ToString();
    }

    private string ReadDollar(ref bool dynamic, ref bool quoted, bool inDoubleQuotes = false)
    {
        var next = Peek(1);
        if (next == '(')
        {
            dynamic = true;
            if (Peek(2) == '(')
            {
                _pos++;
                SkipArithmetic();
                return "$((...))";
            }

            _pos += 2;
            ParseSequence(')');
            return "$(...)";
        }

        if (next == '{')
        {
            dynamic = true;
            _pos++;
            var raw = ReadBalancedRaw('{', '}');
            ParseChild(raw, substitutionsOnly: true);
            return "$" + raw;
        }

        if (_posix && !inDoubleQuotes && next == '\'')
        {
            // ANSI-C quoting: $'...'
            quoted = true;
            _pos += 2;
            var sb = new StringBuilder();
            while (_pos < _text.Length && _text[_pos] != '\'')
            {
                if (_text[_pos] == '\\' && _pos + 1 < _text.Length)
                    sb.Append(_text[_pos++]);
                sb.Append(_text[_pos++]);
            }

            if (_pos >= _text.Length)
                Fail("unterminated $' quote");
            _pos++;
            return sb.ToString();
        }

        _pos++;
        if (char.IsAsciiLetterOrDigit(next) || next is '_' or '@' or '*' or '#' or '?' or '!' or '-' or '$')
            dynamic = true;
        return "$";
    }

    private void ReadBacktick()
    {
        var sb = new StringBuilder();
        _pos++;
        while (_pos < _text.Length)
        {
            var c = _text[_pos];
            if (c == '\\' && Peek(1) is '`' or '\\' or '$')
            {
                sb.Append(Peek(1));
                _pos += 2;
                continue;
            }

            if (c == '`')
            {
                _pos++;
                ParseChild(sb.ToString(), substitutionsOnly: false);
                return;
            }

            sb.Append(c);
            _pos++;
        }

        Fail("unterminated backtick");
    }

    /// <summary>Reads from an opening bracket to its match and returns the raw text, brackets included.</summary>
    private string ReadBalancedRaw(char open, char close)
    {
        var start = _pos;
        var depth = 0;
        while (_pos < _text.Length)
        {
            var c = _text[_pos++];
            if (c == '\\')
            {
                _pos++;
                continue;
            }

            if (c == open)
                depth++;
            else if (c == close && --depth == 0)
                return _text[start.._pos];
        }

        Fail($"missing '{close}'");
        return _text[start..];
    }

    /// <summary>Skips an arithmetic expression starting at "((". Command substitution inside it is not followed.</summary>
    private void SkipArithmetic()
    {
        var raw = ReadBalancedRaw('(', ')');
        if (raw.Contains("$(", StringComparison.Ordinal) || (_posix && raw.Contains('`')))
            Fail("command substitution inside arithmetic is not supported");
    }

    private void ParseChild(string text, bool substitutionsOnly)
    {
        var child = new ShellCommandParser(text, _posix, _commands, _depth);
        if (substitutionsOnly)
            child.ScanForSubstitutions();
        else
            child.ParseSequence(closer: null);

        if (child._unsupported is not null)
            Fail(child._unsupported);
    }

    /// <summary>
    /// Finds command substitutions in text that is otherwise data, such as an unquoted heredoc body
    /// or a ${...} parameter expansion.
    /// </summary>
    private void ScanForSubstitutions()
    {
        while (_pos < _text.Length && _unsupported is null)
        {
            var c = _text[_pos];
            if (c == '\\')
            {
                _pos += 2;
                continue;
            }

            if (c == '$' && Peek(1) == '(')
            {
                if (Peek(2) == '(')
                {
                    _pos++;
                    SkipArithmetic();
                    continue;
                }

                _pos += 2;
                ParseSequence(')');
                continue;
            }

            if (_posix && c == '`')
            {
                ReadBacktick();
                continue;
            }

            _pos++;
        }
    }
}
