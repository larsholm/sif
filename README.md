# picoNET agent

A lightweight AI agent console tool supporting local models via OpenAI-compatible APIs.

## Features

- **Interactive chat** - Multi-turn conversations with full message history
- **One-off completion** - Quick prompts from CLI or stdin
- **Local model support** - Works with any OpenAI-compatible endpoint (Ollama, LM Studio, LiteLLM, etc.)
- **Streaming output** - See responses as they're generated
- **System prompts** - Set custom system instructions for your agent
- **Config management** - Persist settings in `~/.piconet/piconet-agent.json`

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A running OpenAI-compatible API (Ollama, LM Studio, LiteLLM, or OpenAI)

## Installation

### From Source (Global Tool)

```bash
cd picoNET.agent
./build.sh install
```

This builds the project and installs it as a global .NET tool. The `piconet-agent` command will be available in your PATH.

### Local Development

```bash
cd picoNET.agent
dotnet run -- <command> [options]
```

### Rebuild & Update

```bash
cd picoNET.agent
./build.sh install
```

## Usage

### Interactive Chat

```bash
# Start chat with defaults
piconet-agent chat

# With a custom endpoint (e.g., Ollama)
piconet-agent chat -u http://localhost:11434 -m llama3.2

# With a system prompt
piconet-agent chat -s "You are a helpful C# coding assistant."

# Without streaming (shows full response at once)
piconet-agent chat --no-stream
```

### One-off Completion

```bash
# From command line
piconet-agent complete "Explain async/await in C#"

# With custom endpoint and model
piconet-agent complete "Write a Fibonacci function" -u http://localhost:11434 -m qwen3:8b

# With system prompt
piconet-agent complete "Explain closures" -s "Respond concisely in 2 sentences."

# From stdin
cat prompt.txt | piconet-agent complete -
```

### Configuration

```bash
# Show current configuration
piconet-agent config

# Set persistent config values
piconet-agent config --set MODEL=qwen3:8b
piconet-agent config --set BASE_URL=http://localhost:11434
```

## Environment Variables

| Variable           | Description                              | Default              |
|--------------------|------------------------------------------|----------------------|
| `AGENT_BASE_URL`   | OpenAI-compatible API base URL           | `https://api.openai.com` |
| `AGENT_API_KEY`    | API key (optional for local models)      | -                    |
| `AGENT_MODEL`      | Model name to use                        | `gpt-4o`             |
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

### Ollama

```bash
# One-time with arguments
piconet-agent complete "Hello!" -u http://localhost:11434 -m llama3.2

# Or set via environment
export AGENT_BASE_URL=http://localhost:11434
export AGENT_MODEL=llama3.2
piconet-agent chat
```

### LM Studio

```bash
export AGENT_BASE_URL=http://localhost:1234/v1
export AGENT_MODEL=local-model
piconet-agent chat
```

### LiteLLM Proxy

```bash
export AGENT_BASE_URL=http://localhost:4000
export AGENT_API_KEY=your-key
export AGENT_MODEL=gpt-4o
piconet-agent chat
```

## License

MIT
