# picoNET agent

A lightweight AI agent console tool supporting local models via OpenAI-compatible APIs.

## Features

- **Chat by default** — `pico` launches into interactive chat immediately
- **Tool calling** — Enable tools for bash, file read/edit/write, sleep, and local static serving via `--tools`
- **Context mode** — Large tool outputs are stored out-of-band and can be searched or read back by handle
- **One-off completion** — Quick prompts from CLI or stdin with `pico complete`
- **Local model support** — Works with any OpenAI-compatible endpoint (vLLM, Ollama, LM Studio, LiteLLM, etc.)
- **Streaming output** — See responses as they're generated
- **System prompts** — Set custom system instructions for your agent
- **Config management** — Persist settings in `~/.piconet/piconet-agent.json`

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A running OpenAI-compatible API (Ollama, LM Studio, LiteLLM, or OpenAI)

## Installation

```bash
cd picoNET.agent
./build.sh install
```

This builds the project and installs it as a global .NET tool. The `pico` command will be available in your PATH.

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
| `diagnostics` | Inspect pico agent configuration, AGENT_ environment variables, and chat history |

```bash
# Use the default tool set explicitly
pico --tools bash,read,edit,write,context

# Add pico runtime diagnostics when needed
pico --tools bash,read,edit,write,context,diagnostics

# Or set persistently
pico config --set TOOLS=bash,read,edit,write,context
pico
```

When `context` is enabled, large local and MCP tool results are automatically stored under `~/.pico/context/` and replaced in the model context with a compact handle such as `ctx_abc123`. The model can then call `ctx_search` for focused snippets or `ctx_read` for a specific stored result.

The `diagnostics` tool is for inspecting pico's own runtime state. It is not a debugger and does not launch, attach to, or manage .NET debug adapter sessions.

**Note:** Tool calling is non-streaming (the model decides whether to use tools, then returns the final response). Thinking/reasoning display works for OpenAI o-series models and Qwen3.x models via vLLM.

## Usage

### Interactive Chat (Default)

```bash
# Start chat (default behavior)
pico

# With custom endpoint and model
pico -u http://100.118.58.55:8020/v1 -m qwen3.6-27b-autoround

# With a system prompt
pico -s "You are a helpful C# coding assistant."

# Without streaming (shows full response at once)
pico --no-stream
```

### One-off Completion

```bash
# From command line
pico complete "Explain async/await in C#"

# With custom endpoint and model
pico complete "Write a Fibonacci function" -u http://100.118.58.55:8020/v1 -m qwen3.6-27b-autoround

# With system prompt
pico complete "Explain closures" -s "Respond concisely in 2 sentences."

# From stdin
cat prompt.txt | pico complete -
```

### Configuration

```bash
# Show current configuration
pico config

# Set persistent config values
pico config --set MODEL=qwen3.6-27b-autoround
pico config --set BASE_URL=http://100.118.58.55:8020/v1
```

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
| `/help`              | Show help and options                    |

## Examples

### Your Local Model

```bash
# Set persistent config
pico config --set BASE_URL=http://100.118.58.55:8020/v1
pico config --set MODEL=qwen3.6-27b-autoround
pico config --set TOOLS=bash,read,edit

# Then just type:
pico
```

### Ollama

```bash
# One-time with arguments
pico -u http://localhost:11434 -m llama3.2

# Or set via environment
export AGENT_BASE_URL=http://localhost:11434
export AGENT_MODEL=llama3.2
pico
```

### LM Studio

```bash
export AGENT_BASE_URL=http://localhost:1234/v1
export AGENT_MODEL=local-model
pico
```

### LiteLLM Proxy

```bash
export AGENT_BASE_URL=http://localhost:4000
export AGENT_API_KEY=your-key
export AGENT_MODEL=gpt-4o
pico
```

### Thinking / Reasoning

```bash
# Enable thinking for Qwen3.x via vLLM (enabled by default)
pico --thinking true -u http://100.118.58.55:8020/v1 -m qwen3.6-27b-autoround

# Enable thinking for OpenAI o-series
pico --thinking true -m o3-mini

# Persistent setting
pico config --set THINKING_ENABLED=true
```

**Note:** For Qwen3.x models, thinking is enabled by default on the server. The `--thinking` flag enables display of the reasoning output. When thinking is enabled on non-OpenAI models, non-streaming mode is used automatically (reasoning is a separate response field the SDK can't stream).
## License

MIT
