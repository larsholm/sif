# sif agent

A lightweight AI agent console tool supporting local models via OpenAI-compatible APIs.

## Features

- **Chat by default** — `sif` launches into interactive chat immediately
- **Tool calling** — Enable tools for bash, file read/edit/write, sleep, and local static serving via `--tools`
- **Context mode** — Large tool outputs are stored out-of-band and can be searched or read back by handle
- **One-off completion** — Quick prompts from CLI or stdin with `sif complete`
- **Local model support** — Works with any OpenAI-compatible endpoint (vLLM, Ollama, LM Studio, LiteLLM, etc.)
- **Streaming output** — See responses as they're generated
- **System prompts** — Set custom system instructions for your agent
- **Skill files** — Add reusable markdown instructions under `.sif/skills` or `~/.sif/skills`
- **Config management** — Persist settings in `~/.sif/sif-agent.json`

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A running OpenAI-compatible API (Ollama, LM Studio, LiteLLM, or OpenAI)

## Installation

```bash
cd sif.agent
./build.sh install
```

This builds the project and installs it as a global .NET tool. The `sif` command will be available in your PATH.

The install command also installs the companion VS Code extension into `~/.vscode/extensions`. Set `VSCODE_EXTENSIONS=/path/to/extensions` before running the installer to target another VS Code-compatible extensions directory.

## Tools

Enable tool calling with `--tools` (comma-separated):

| Tool    | Description                                            |
|---------|--------------------------------------------------------|
| `bash`  | Execute safe shell commands (ls, cat, grep, find, etc.) |
| `read`  | Read file contents                                      |
| `edit`  | Edit files by replacing exact text                      |
| `write` | Create or overwrite files                               |
| `sleep` | Pause briefly before continuing or retrying              |
| `serve` | Start a local static HTTP server for a directory         |
| `context` | Add `ctx_index`, `ctx_search`, `ctx_read`, and `ctx_stats` tools for large context |
| `diagnostics` | Inspect sif agent configuration, AGENT_ environment variables, and chat history |

```bash
# Use the default tool set explicitly
sif --tools bash,read,edit,write,context

# Add sif runtime diagnostics when needed
sif --tools bash,read,edit,write,context,diagnostics

# Or set persistently
sif config --set TOOLS=bash,read,edit,write,context
sif
```

When `context` is enabled, large local and MCP tool results are automatically stored under `~/.sif/context/` and replaced in the model context with a compact handle such as `ctx_abc123`. The model can then call `ctx_search` for focused snippets or `ctx_read` for a specific stored result.

The `diagnostics` tool is for inspecting sif's own runtime state. It is not a debugger and does not launch, attach to, or manage .NET debug adapter sessions.

**Note:** Tool calling is non-streaming (the model decides whether to use tools, then returns the final response). Thinking/reasoning display works for OpenAI o-series models and Qwen3.x models via vLLM.

## Usage

### Interactive Chat (Default)

```bash
# Start chat (default behavior)
sif

# With custom endpoint and model
sif -u http://100.118.58.55:8020/v1 -m qwen3.6-27b-autoround

# With a system prompt
sif -s "You are a helpful C# coding assistant."

# Without streaming (shows full response at once)
sif --no-stream
```

### One-off Completion

```bash
# From command line
sif complete "Explain async/await in C#"

# With custom endpoint and model
sif complete "Write a Fibonacci function" -u http://100.118.58.55:8020/v1 -m qwen3.6-27b-autoround

# With system prompt
sif complete "Explain closures" -s "Respond concisely in 2 sentences."

# From stdin
cat prompt.txt | sif complete -
```

### Configuration

```bash
# Show current configuration
sif config

# Set persistent config values
sif config --set MODEL=qwen3.6-27b-autoround
sif config --set BASE_URL=http://100.118.58.55:8020/v1
```

### Skills

sif loads skill files at startup and appends them to the system prompt. Use skills for reusable instructions that should apply when a request matches the skill description.

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

### Options

| Flag                 | Description                    |
|----------------------|--------------------------------|
| `-m, --model`        | Model name                     |
| `-u, --base-url`     | API base URL                   |
| `-k, --api-key`      | API key                        |
| `-s, --system`       | System prompt                  |
| `-n, --no-stream`    | Disable streaming output       |
| `--tools`            | Enable tools: bash,edit,read,write,sleep,serve,context,diagnostics |
| `--thinking`         | Enable model thinking/reasoning (true/false) |

## Environment Variables

| Variable           | Description                              | Default              |
|--------------------|------------------------------------------|----------------------|
| `AGENT_BASE_URL`   | OpenAI-compatible API base URL           | `https://api.openai.com` |
| `AGENT_API_KEY`    | API key (optional for local models)      | -                    |
| `AGENT_MODEL`      | Model name to use                        | `gpt-4o`             |
| `AGENT_TOOLS`      | Comma-separated list of tools            | -                    |
| `AGENT_MAX_TOKENS` | Maximum output tokens                    | model default        |
| `AGENT_TEMPERATURE`| Sampling temperature (0-1)               | model default        |
| `AGENT_THINKING_ENABLED` | Enable model thinking/reasoning      | `false`              |

## VS Code Terminal Context

When `sif` runs inside a VS Code integrated terminal, it detects that environment automatically. VS Code does not expose the active editor or selection to terminal child processes by default, but the companion extension writes live editor context to a small JSON file and exposes it through `SIF_VSCODE_CONTEXT_FILE` for new integrated terminals.

| Variable | Description |
|----------|-------------|
| `SIF_VSCODE_CONTEXT_FILE` | JSON file containing live editor context, used by the VS Code extension |
| `SIF_VSCODE_FILE` | Active editor file path or `file://` URI |
| `SIF_VSCODE_LINE` | Active cursor line number |
| `SIF_VSCODE_COLUMN` | Active cursor column number |
| `SIF_VSCODE_SELECTED_TEXT` | Current selected text |
| `SIF_VSCODE_SELECTED_TEXT_B64` | Current selected text as UTF-8 base64, useful for multiline selections |

Use `/vscode` in chat to inspect what `sif` can see.

The VS Code extension lives in `sif.vscode/`. For local development, open the repository in VS Code and run the extension host with that folder as the extension development path. The command `sif: Start Chat With Editor Context` opens a `sif` terminal and keeps the context file updated as the active editor, cursor, or selection changes. Regular integrated terminals opened after the extension activates also receive `SIF_VSCODE_CONTEXT_FILE`.

## Chat Commands

During a chat session, type these commands:

| Command          | Description                              |
|------------------|------------------------------------------|
| `/quit` or `/exit` | Exit the chat session                   |
| `/clear`             | Clear conversation history (keeps system prompt) |
| `/sys <prompt>`     | Change the system prompt                 |
| `/context`           | Show current chat history and stored context summary |
| `/context list`      | List stored context entries for this session |
| `/context search <query>` | Search stored context entries |
| `/context read <id> [query]` | Read a stored entry, optionally focused by query |
| `/context delete <id>` | Delete a stored context entry |
| `/context drop <count>` | Remove recent non-system chat messages |
| `/context clear` | Clear conversation history (keeps system prompt) |
| `/context clear-history` | Clear conversation history (keeps system prompt) |
| `/context clear-store` | Delete stored context entries for this session |
| `/context clear all` | Clear both chat history and stored context |
| `/vscode`            | Show detected VS Code terminal/editor context |
| `/help`              | Show help and options                    |

## Examples

### Your Local Model

```bash
# Set persistent config
sif config --set BASE_URL=http://100.118.58.55:8020/v1
sif config --set MODEL=qwen3.6-27b-autoround
sif config --set TOOLS=bash,read,edit

# Then just type:
sif
```

### Ollama

```bash
# One-time with arguments
sif -u http://localhost:11434 -m llama3.2

# Or set via environment
export AGENT_BASE_URL=http://localhost:11434
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
# Enable thinking for Qwen3.x via vLLM (enabled by default)
sif --thinking true -u http://100.118.58.55:8020/v1 -m qwen3.6-27b-autoround

# Enable thinking for OpenAI o-series
sif --thinking true -m o3-mini

# Persistent setting
sif config --set THINKING_ENABLED=true
```

**Note:** For Qwen3.x models, thinking is enabled by default on the server. The `--thinking` flag enables display of the reasoning output. When thinking is enabled on non-OpenAI models, non-streaming mode is used automatically (reasoning is a separate response field the SDK can't stream).
## License

MIT
