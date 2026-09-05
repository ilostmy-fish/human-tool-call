# Human Tool Call

A lightweight Windows tray application, local browser UI, MCP server, and OpenAI tunnel supervisor in one process.

Human Tool Call gives an MCP-capable model three explicit ways to communicate with the user while it is still working:

- `ask_user` — block on one or many free-form questions and return the answers to the same tool call.
- `choose_path` — block on one or many structured implementation/decision choices and return the selections.
- `progress_report` — publish a non-blocking progress update and immediately return `received`.

The server instructions explicitly tell the model that there is no one-question or one-clarification-round limit. It may ask again immediately after receiving an answer, and it should not suppress a useful question just because the user may not know the answer.

## Architecture

One `HumanToolCall.exe` owns everything except OpenAI's `tunnel-client.exe` child process:

```text
ChatGPT plugin
    |
OpenAI Secure MCP Tunnel
    |
tunnel-client.exe --profile human-tool-call
    |
http://127.0.0.1:64500/mcp
    |
HumanToolCall.exe
    |-- MCP server
    |-- pending-interaction broker
    |-- browser UI/API
    |-- tray UI
    `-- tunnel supervisor
```

Blocking tool calls create an in-memory pending interaction and await it. The browser submits the user's answer to the local backend, which completes that pending operation and returns the answer through the original MCP call. If the model/host cancels the call or the configured local timeout expires, the pending interaction is removed and the tool returns a cancellation/timeout status where possible.

The configured `interactionTimeoutSeconds` is the application's own maximum wait. An upstream ChatGPT/MCP/tunnel timeout may still cancel a call earlier; that practical host limit should be tested empirically.

## Tray UI

The tray intentionally stays small:

```text
Backend: On/Off
Tunnel: On/Off/Error
Pending: N
----------------
Open UI
Status
----------------
Start Backend
Stop Backend
Start Tunnel
Stop Tunnel
----------------
Close | N
```

There is no configuration editor in the UI. When idle with the backend and tunnel stopped, the application has no polling timer; it is essentially the tray/message loop plus in-memory state.

## Configuration location is selected in source

Before building, choose where the executable expects its configuration by editing one constant in `Configuration.cs`:

```text
ConfigPathPolicy.Location
```

Available values are:

- `ConfigPathMode.ExecutableDirectory`
- `ConfigPathMode.Documents` (uses `Documents\HumanToolCall`)
- `ConfigPathMode.LocalAppData` (uses `%LOCALAPPDATA%\HumanToolCall`)

The filename is `human-tool-call.json`. If it does not exist, the application writes a default file at the selected location on first launch.

`human-tool-call.example.json` contains the full configuration shape. Startup behavior is controlled there, including:

- `startWithWindows`
- `startBackendOnLaunch`
- `startTunnelOnLaunch`
- `openBrowserOnLaunch`
- `stopTunnelOnExit`
- question/progress notifications

When `startWithWindows` is true, the application maintains an HKCU `Run` entry pointing at its current executable path. No admin rights are required.

`startTunnelOnLaunch` requires `startBackendOnLaunch`, because the tunnel profile targets the local MCP server.

## Local browser UI

The backend binds only to `127.0.0.1`. The tray's **Open UI** action launches:

```text
http://127.0.0.1:64500/
```

with a random per-process bootstrap token in the URL fragment. The page moves that token into `sessionStorage`, removes it from the visible URL, and sends it in a custom request header for local API calls. Opening the URL manually without the tray-provided token does not expose pending questions or allow answers.

The browser uses long polling rather than a periodic short-interval timer, so an idle tab has negligible request activity.

## OpenAI tunnel profile

Create a separate OpenAI tunnel and tunnel-client profile for Human Tool Call. With the default local settings, the MCP URL is:

```text
http://127.0.0.1:64500/mcp
```

For tunnel-client v0.0.11-style syntax, the profile setup is conceptually:

```text
tunnel-client.exe init --sample sample_mcp_remote_no_auth --profile human-tool-call --tunnel-id "tunnel_YOUR_ID" --mcp-server-url "http://127.0.0.1:64500/mcp"
```

The tray launches it as:

```text
tunnel-client.exe run --profile "human-tool-call" --health.listen-addr "127.0.0.1:8082"
```

The tunnel supervisor follows the safety behavior used by the pinned `tunnel-tray-v1` 1.1 implementation: it inspects `tunnel-client.exe` processes through WMI, classifies only the configured executable/profile, reconciles malformed/duplicate managed invocations, probes `/healthz`, and refuses to create a duplicate when process state cannot be classified safely.

The OpenAI control-plane key is never stored as plaintext in this repository or config. If `controlPlaneApiKeyFile` is non-empty, it is expected to be a Windows CurrentUser DPAPI blob produced by PowerShell `ConvertFrom-SecureString` without `-Key`/`-SecureKey`. If that field is empty, the configured environment variable is used instead.

## ChatGPT plugin

Create a ChatGPT developer-mode plugin using the Human Tool Call tunnel and `No Auth`. Once connected, ChatGPT should discover these model-facing tools:

```text
ask_user
choose_path
progress_report
```

The browser submission API is local implementation plumbing and is not exposed as a model-facing MCP tool.

## Build

Requirements: .NET 8 SDK on Windows.

```text
dotnet restore
dotnet build -c Release
dotnet publish -c Release
```

The project defaults to `win-x64`, self-contained, single-file publishing. The publish directory contains `HumanToolCall.exe`; that executable can be copied by itself. `tunnel-client.exe`, its profile/config data, and the control-plane credential remain external by design.

## Security notes

- The backend listens only on the IPv4 loopback interface.
- Browser API calls require a random per-process token supplied by the tray.
- MCP remains unauthenticated locally because OpenAI Secure MCP Tunnel is the intended transport boundary and connects through loopback.
- Tool inputs are treated as untrusted, length-limited, and rendered in the browser with DOM `textContent`, not HTML injection.
- Pending answers and progress reports are in memory only.
- No secret is written to application logs.

## Reference implementation

The tunnel process-management design was intentionally based only on `tunnel-tray-v1` commit `ea1b5542b1e1e7e9e2b21ba79a046f5c89322e10`, targeting `JetBrainsMcpTray_1.1.cs`. Later commits in that repository are unrelated to this project.
