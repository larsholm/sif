namespace sif.agent;

/// <summary>
/// Parses raw command-line arguments into <see cref="CliArgs"/>.
/// </summary>
internal static class CliParser
{
    public static CliArgs ParseArgs(string[] args)
    {
        var opts = new CliArgs();
        bool nextIsValue = false;
        string? pendingFlag = null;

        foreach (var arg in args)
        {
            if (nextIsValue)
            {
                nextIsValue = false;
                if (pendingFlag == "-s" || pendingFlag == "--system") opts.SystemPrompt = arg;
                else if (pendingFlag == "-m" || pendingFlag == "--model") opts.Model = arg;
                else if (pendingFlag == "-u" || pendingFlag == "--base-url") opts.BaseUrl = arg;
                else if (pendingFlag == "-k" || pendingFlag == "--api-key") opts.ApiKey = arg;
                else if (pendingFlag == "-t" || pendingFlag == "--temperature") opts.Temperature = float.TryParse(arg, out var t) ? t : null;
                else if (pendingFlag == "-max" || pendingFlag == "--max-tokens") opts.MaxTokens = int.TryParse(arg, out var m) ? m : null;
                else if (pendingFlag == "--timeout" || pendingFlag == "--model-timeout") opts.ModelTimeoutSeconds = int.TryParse(arg, out var timeout) ? timeout : null;
                else if (pendingFlag == "--tools") opts.Tools = arg.Split(',').Select(s => s.Trim()).ToArray();
                else if (pendingFlag == "--thinking") opts.Thinking = ParseThinkingArg(arg);
                else if (pendingFlag == "-p" || pendingFlag == "--profile") opts.Profile = arg;
                pendingFlag = null;
                continue;
            }

            if (arg is "-s" or "--system" or "-m" or "--model" or "-u" or "--base-url" or "-k" or "--api-key" or "--tools" or "--thinking" or "-t" or "--temperature" or "-max" or "--max-tokens" or "--timeout" or "--model-timeout" or "-p" or "--profile")
            {
                nextIsValue = true;
                pendingFlag = arg;
                continue;
            }

            if (arg.StartsWith("-s ") || arg.StartsWith("--system "))
                opts.SystemPrompt = arg[(arg.StartsWith("-s ") ? 3 : 9)..].Trim();
            else if (arg.StartsWith("-m ") || arg.StartsWith("--model "))
                opts.Model = arg[(arg.StartsWith("-m ") ? 3 : 8)..].Trim();
            else if (arg.StartsWith("-u ") || arg.StartsWith("--base-url "))
                opts.BaseUrl = arg[(arg.StartsWith("-u ") ? 3 : 11)..].Trim();
            else if (arg.StartsWith("-k ") || arg.StartsWith("--api-key "))
                opts.ApiKey = arg[(arg.StartsWith("-k ") ? 3 : 11)..].Trim();
            else if (arg.StartsWith("-p ") || arg.StartsWith("--profile "))
                opts.Profile = arg[(arg.StartsWith("-p ") ? 3 : 10)..].Trim();
            else if (arg.StartsWith("--thinking "))
                opts.Thinking = ParseThinkingArg(arg["--thinking ".Length..].Trim());
            else if (arg.StartsWith("--timeout "))
                opts.ModelTimeoutSeconds = int.TryParse(arg["--timeout ".Length..].Trim(), out var timeout) ? timeout : null;
            else if (arg.StartsWith("--model-timeout "))
                opts.ModelTimeoutSeconds = int.TryParse(arg["--model-timeout ".Length..].Trim(), out var timeout) ? timeout : null;
            else if (arg is "-n" or "--no-stream")
                opts.NoStream = true;
            else if (arg.StartsWith("-s="))
                opts.SystemPrompt = arg[3..];
            else if (arg.StartsWith("--system="))
                opts.SystemPrompt = arg[9..];
            else if (arg.StartsWith("-m="))
                opts.Model = arg[3..];
            else if (arg.StartsWith("--model="))
                opts.Model = arg[8..];
            else if (arg.StartsWith("-u="))
                opts.BaseUrl = arg[3..];
            else if (arg.StartsWith("--base-url="))
                opts.BaseUrl = arg[11..];
            else if (arg.StartsWith("-k="))
                opts.ApiKey = arg[3..];
            else if (arg.StartsWith("-p="))
                opts.Profile = arg[3..];
            else if (arg.StartsWith("--profile="))
                opts.Profile = arg[10..];
            else if (arg.StartsWith("--api-key="))
                opts.ApiKey = arg[11..];
            else if (arg.StartsWith("-t="))
                opts.Temperature = float.TryParse(arg[3..], out var t) ? t : null;
            else if (arg.StartsWith("--temperature="))
                opts.Temperature = float.TryParse(arg[14..], out var t2) ? t2 : null;
            else if (arg.StartsWith("-max="))
                opts.MaxTokens = int.TryParse(arg[5..], out var m) ? m : null;
            else if (arg.StartsWith("--max-tokens="))
                opts.MaxTokens = int.TryParse(arg[13..], out var m2) ? m2 : null;
            else if (arg.StartsWith("--timeout="))
                opts.ModelTimeoutSeconds = int.TryParse(arg["--timeout=".Length..], out var timeout) ? timeout : null;
            else if (arg.StartsWith("--model-timeout="))
                opts.ModelTimeoutSeconds = int.TryParse(arg["--model-timeout=".Length..], out var timeout) ? timeout : null;
            else if (arg.StartsWith("--tools="))
                opts.Tools = arg[8..].Split(',').Select(s => s.Trim()).ToArray();
            else if (arg.StartsWith("--thinking="))
                opts.Thinking = ParseThinkingArg(arg["--thinking=".Length..].Trim());
            else if (!arg.StartsWith("-"))
            {
                opts.Positional ??= new List<string>();
                opts.Positional.Add(arg);
            }
        }
        return opts;
    }

    public static bool ParseThinkingArg(string arg)
    {
        return arg.ToLowerInvariant() is "true" or "1" or "yes";
    }
}
