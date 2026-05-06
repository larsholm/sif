# picoNET agent

A lightweight AI agent console tool supporting local models via OpenAI-compatible APIs.

## Features

- **Chat by default** — `pico` launches into interactive chat immediately
- **Tool calling** — Enable tools for bash, file read/edit/write via `--tools`
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

```bash
# Use all tools
pico --tools bash,read,edit,write

# Or set persistently
pico config --set TOOLS=bash,read,edit,write
pico
```

**Note:** Tool calling is non-streaming (the model decides whether to use tools, then returns the final response). Thinking display requires model support for `<thinking>` tags.

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
| `--tools`            | Enable tools: bash,edit,read,write |

## Environment Variables

| Variable           | Description                              | Default              |
|--------------------|------------------------------------------|----------------------|
| `AGENT_BASE_URL`   | OpenAI-compatible API base URL           | `https://api.openai.com` |
| `AGENT_API_KEY`    | API key (optional for local models)      | -                    |
| `AGENT_MODEL`      | Model name to use                        | `gpt-4o`             |
| `AGENT_TOOLS`      | Comma-separated list of tools            | -                    |
| `AGENT_MAX_TOKENS` | Maximum output tokens                    | model default        |
| `AGENT_TEMPERATURE`| Sampling temperature (0-1)               | model default        |

## Chat Commands

During a chat session, type these commands:

| Command          | Description                              |
|------------------|------------------------------------------|
| `/quit` or `/exit` | Exit the chat session                   |
| `/clear`             | Clear conversation history (keeps system prompt) |
| `/sys <prompt>`     | Change the system prompt                 |

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

## License

MIT
