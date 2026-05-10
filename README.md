# sif agent

<p align="center">
  <img src="assets/sif.png" alt="Sif" width="720">
</p>

`sif` is a lightweight AI agent console tool written in C#. It works with OpenAI-compatible APIs, local model servers, and a small set of built-in tools for reading, editing, searching, serving, and inspecting local context.

## Highlights

- **Interactive by default** - run `sif` to start a chat session.
- **Local model friendly** - use vLLM, Ollama, LM Studio, LiteLLM, OpenAI, or any compatible endpoint.
- **Tool calling** - enable shell, file, context, sleep, static server, and diagnostics tools.
- **MCP servers** - connect to Model Context Protocol servers over stdio, HTTP, streamable HTTP, or SSE.
- **Context store** - large tool outputs are stored out-of-band and can be searched or read back by handle.
- **VS Code context** - the companion extension exposes active editor, cursor, and selection context to `sif`.
- **Skills** - reusable markdown instructions can be loaded from project or user skill folders.
- **Persistent config** - store model, endpoint, tool, and reasoning settings in `~/.sif/sif-agent.json`.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- An OpenAI-compatible API endpoint
- Unix-like shells: `curl`, `tar`, and `zip`
- Windows PowerShell: no external archive tool required

## Install

Linux/macOS:

```bash
curl -fsSL https://raw.githubusercontent.com/larsholm/sif/main/install.sh | bash
```

Windows PowerShell:

```powershell
irm https://raw.githubusercontent.com/larsholm/sif/main/install.ps1 | iex
```

This downloads the latest `main` archive, builds the agent, and installs it as a global .NET tool. The `sif` command will be available in your PATH.

The installer also installs the companion VS Code extension into `~/.vscode/extensions`. Set `VSCODE_EXTENSIONS=/path/to/extensions` before running the installer to target another VS Code-compatible extensions directory.

On first launch, `sif` opens a setup wizard for the API base URL, optional API key, model, tools, and thinking/reasoning display. The wizard fetches available models from the configured endpoint when possible and falls back to manual model entry if the endpoint cannot be queried. Run `sif setup` any time to update those values.

For local development from a checked-out repository:

```bash
./sif.agent/build.sh install
```

On Windows, run `.\install.ps1` from the repository root.

## Quick Start

```bash
# Start an interactive chat
sif

# Use a specific endpoint and model
sif -u http://localhost:11434/v1 -m llama3.2

# Run one prompt and exit
sif complete "Explain async/await in C#"

# Pipe a prompt from stdin
cat prompt.txt | sif complete -
```

## Tools

Tools can be enabled with `--tools`, `AGENT_TOOLS`, or persistent config. The CLI flag has highest priority, then `AGENT_TOOLS`, then `~/.sif/sif-agent.json`.

The default tool set is:

```text
bash,read,edit,write,sleep,serve,context
```

Available tools:

| Tool | Description |
|------|-------------|
| `bash` | Execute safe shell commands such as `ls`, `cat`, `grep`, and `find` |
| `read` | Read file contents |
| `edit` | Edit files by replacing exact text |
| `write` | Create or overwrite files |
| `sleep` | Pause briefly before continuing or retrying |
| `serve` | Start a local static HTTP server for a directory |
| `context` | Enable `ctx_index`, `ctx_search`, `ctx_read`, and `ctx_stats` |
| `diagnostics` | Inspect sif configuration, `AGENT_` environment variables, and chat history |

```bash
# Use the default tool set explicitly
sif --tools bash,read,edit,write,sleep,serve,context

# Add runtime diagnostics for this session
sif --tools bash,read,edit,write,sleep,serve,context,diagnostics

# Enable tools through the environment
export AGENT_TOOLS=bash,read,edit,write,context,diagnostics
sif

# Store tools persistently
sif config --set TOOLS=bash,read,edit,write,context,diagnostics
sif
```

When `context` is enabled, large local and MCP tool results are stored under `~/.sif/context/` and replaced in model context with a compact handle such as `ctx_abc123`. The model can then call `ctx_search` for focused snippets or `ctx_read` for a specific stored result.

The `diagnostics` tool is for inspecting sif's runtime state only. It is not a debugger and does not launch, attach to, or manage .NET debug adapter sessions. There is also a legacy `debug` tool alias for the same diagnostics behavior.

Tool calling is non-streaming: the model decides whether to call tools, then returns the final response. Thinking and reasoning display works for OpenAI o-series models and Qwen3.x models via vLLM.

## MCP Servers

`sif` can connect to Model Context Protocol servers configured in `~/.sif/sif-agent.json`. MCP tools are listed at startup and exposed to the model alongside local tools.

```json
{
  "McpServers": {
    "filesystem": {
      "Type": "stdio",
      "Command": "npx",
      "Args": ["-y", "@modelcontextprotocol/server-filesystem", "/home/lars/source"]
    },
    "remote-tools": {
      "Type": "streamableHttp",
      "Url": "https://example.com/mcp",
      "Headers": {
        "Authorization": "Bearer TOKEN"
      }
    }
  }
}
```

Supported `Type` values are `stdio`, `http`, `streamableHttp`, `streamable-http`, and `sse`. Stdio servers use `Command`, `Args`, and optional `Env`; HTTP-based servers use `Url` and optional `Headers`. Set `Disabled` to `true` to keep a server in the config without connecting to it.

## Configuration

```bash
# Show current configuration
sif config

# Run the setup wizard
sif setup

# Set persistent values
sif config --set BASE_URL=http://localhost:11434/v1
sif config --set MODEL=llama3.2
sif config --set TOOLS=bash,read,edit,write,context
sif config --set THINKING_ENABLED=true
```

Configuration is loaded from `~/.sif/sif-agent.json`, then overridden by environment variables, then overridden by CLI flags.

| Variable | Description | Default |
|----------|-------------|---------|
| `AGENT_BASE_URL` | OpenAI-compatible API base URL | `https://api.openai.com` |
| `AGENT_API_KEY` | API key, optional for local models | - |
| `AGENT_MODEL` | Model name | `gpt-4o` |
| `AGENT_TOOLS` | Comma-separated list of tools | default tool set |
| `AGENT_MAX_TOKENS` | Maximum output tokens | model default |
| `AGENT_TEMPERATURE` | Sampling temperature | model default |
| `AGENT_THINKING_ENABLED` | Enable model thinking or reasoning display | `false` |

## CLI Reference

| Flag | Description |
|------|-------------|
| `-m, --model` | Model name |
| `-u, --base-url` | API base URL |
| `-k, --api-key` | API key |
| `-s, --system` | System prompt |
| `-n, --no-stream` | Disable streaming output |
| `--tools` | Enable tools, comma-separated |
| `--thinking` | Enable model thinking or reasoning display |

## Chat Commands

During an interactive chat session:

| Command | Description |
|---------|-------------|
| `/quit`, `/exit` | Exit the chat session |
| `/clear` | Clear conversation history, keeping the system prompt |
| `/sys <prompt>` | Change the system prompt |
| `/context` | Show chat history and stored context summary |
| `/context list` | List stored context entries |
| `/context search <query>` | Search stored context entries |
| `/context read <id> [query]` | Read a stored entry, optionally focused by query |
| `/context delete <id>` | Delete a stored context entry |
| `/context drop <count>` | Remove recent non-system chat messages |
| `/context clear-history` | Clear conversation history, keeping the system prompt |
| `/context clear-store` | Delete stored context entries for this session |
| `/context clear all` | Clear both chat history and stored context |
| `/vscode` | Show detected VS Code terminal/editor context |
| `/help` | Show help and options |

## VS Code Context

When `sif` runs inside a VS Code integrated terminal, it detects that environment automatically. VS Code does not expose the active editor or selection to terminal child processes by default, so the companion extension writes live editor context to a small JSON file and exposes it through `SIF_VSCODE_CONTEXT_FILE` for new integrated terminals.

| Variable | Description |
|----------|-------------|
| `SIF_VSCODE_CONTEXT_FILE` | JSON file containing live editor context |
| `SIF_VSCODE_FILE` | Active editor file path or `file://` URI |
| `SIF_VSCODE_LINE` | Active cursor line number |
| `SIF_VSCODE_COLUMN` | Active cursor column number |
| `SIF_VSCODE_SELECTED_TEXT` | Current selected text |
| `SIF_VSCODE_SELECTED_TEXT_B64` | Current selected text as UTF-8 base64 |

Use `/vscode` in chat to inspect what `sif` can see.

The VS Code extension lives in `sif.vscode/`. For local development, open the repository in VS Code and run the extension host with that folder as the extension development path. The `sif: Start Chat With Editor Context` command opens a `sif` terminal and keeps the context file updated as the active editor, cursor, or selection changes. Regular integrated terminals opened after the extension activates also receive `SIF_VSCODE_CONTEXT_FILE`.

## Skills

`sif` loads skill files at startup and appends them to the system prompt. Use skills for reusable instructions that should apply when a request matches the skill description.

Supported locations:

```text
./.sif/skills/
../.sif/skills/        # parent directories are checked up to filesystem root
~/.sif/skills/
```

Supported file layouts:

```text
.sif/skills/my-skill.md
.sif/skills/my-skill/SKILL.md
```

Skill files are plain markdown. Frontmatter such as `name` and `description` is allowed and is passed through to the model as part of the skill content.

## Examples

### Local Model

```bash
sif config --set BASE_URL=http://100.118.58.55:8020/v1
sif config --set MODEL=qwen3.6-27b-autoround
sif config --set TOOLS=bash,read,edit,write,context
sif
```

### Ollama

```bash
sif -u http://localhost:11434/v1 -m llama3.2

export AGENT_BASE_URL=http://localhost:11434/v1
export AGENT_MODEL=llama3.2
sif
```

### LM Studio

```bash
export AGENT_BASE_URL=http://localhost:1234/v1
export AGENT_MODEL=local-model
sif
```

### LiteLLM Proxy

```bash
export AGENT_BASE_URL=http://localhost:4000
export AGENT_API_KEY=your-key
export AGENT_MODEL=gpt-4o
sif
```

### Thinking / Reasoning

```bash
# Enable thinking display for Qwen3.x via vLLM
sif --thinking true -u http://100.118.58.55:8020/v1 -m qwen3.6-27b-autoround

# Enable thinking display for OpenAI o-series
sif --thinking true -m o3-mini

# Store the setting
sif config --set THINKING_ENABLED=true
```

For Qwen3.x models, thinking is enabled by default on the server. The `--thinking` flag enables display of the reasoning output. When thinking is enabled on non-OpenAI models, non-streaming mode is used automatically because reasoning is exposed as a separate response field that the SDK cannot stream.

## License

MIT
