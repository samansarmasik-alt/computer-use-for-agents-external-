# Agent setup and usage

PC Use is a Windows-only MCP server that can observe and control the signed-in user's visible desktop. Read this file before using its desktop tools.

## Setup

- Requires Windows 10 or 11, .NET 8 SDK, and an interactive signed-in desktop session.
- Restore and build from the repository root:

  ```powershell
  dotnet restore PcUse.csproj
  dotnet build PcUse.csproj --no-restore
  ```

- Open this repository in OpenCode. The checked-in `opencode.json` configures the local `pc-use` MCP server. Check discovery with `opencode mcp list`.
- Other MCP clients should launch `dotnet run --no-launch-profile --project PcUse.csproj` using this repository as the working directory and connect through stdio.

## Desktop tool rules

- Only inspect or control the desktop when the user's current request calls for it. A screenshot exposes the entire visible virtual desktop to the agent.
- Do not use live mouse or keyboard calls as routine tests. Build and MCP discovery checks do not need desktop input.
- Before coordinates-based actions, take a fresh screenshot when the task requires visual targeting. Use the returned desktop origin and bounds; reject coordinates outside those bounds.
- `computer_ui_inspect` returns bounded control metadata and names; it does not read text values or password contents.
- Treat typing, clicking, dragging, holding keys/buttons, and action batches as real user actions. Do not submit forms, send messages, make purchases, delete data, change account/security settings, or enter credentials unless the user explicitly requested that exact action.
- Release any held key or mouse button with `computer_release_all` when it is no longer needed. Failed or cancelled batches release PC Use's held inputs automatically.
- Keep desktop data in the current task. Do not save screenshots or screen contents to the repository unless explicitly requested.

## Project checks

- Build after source changes with `dotnet build PcUse.csproj --no-restore`.
- MCP transport/tool discovery can be checked with `opencode mcp list` from the repository root.
- Do not claim live desktop behavior was verified unless the relevant tool was actually invoked at the user's request.
