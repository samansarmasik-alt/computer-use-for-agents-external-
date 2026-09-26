# PC Use

PC Use is a local Windows MCP server for observing and controlling the visible desktop, including applications outside a browser.

## Requirements and start

- Windows 10 or 11, in the signed-in interactive user session.
- .NET 8 SDK.
- No administrator elevation is required. Run with the normal user account that owns the desktop session.

From this folder:

```powershell
dotnet run --no-launch-profile --project PcUse.csproj
```

The server uses MCP stdio. It emits no logs to stdout, opens no network listener, and provides no shell or process execution tool.

### OpenCode

This repository includes a project-level `opencode.json` that starts PC Use automatically when OpenCode opens this folder. From the repository root, run:

```powershell
opencode mcp list
```

OpenCode may ask you to trust the repository configuration the first time. The MCP server runs locally as your signed-in Windows user. To use another MCP client, configure a stdio server with `dotnet run --no-launch-profile --project PcUse.csproj` and set its working directory to this repository.

## Tools

| Tool | Purpose |
| --- | --- |
| `computer_screenshot` | Capture the full virtual desktop on request; returns a PNG and desktop origin/bounds. |
| `computer_mouse_move` | Move to an absolute desktop pixel coordinate. |
| `computer_mouse_click` | Click left, right, middle, x1, or x2 once to three times. |
| `computer_mouse_scroll` | Scroll vertically or horizontally by up to 10 wheel ticks. |
| `computer_mouse_drag` | Drag a mouse button between two absolute desktop coordinates. |
| `computer_mouse_button` | Hold or release a mouse button. |
| `computer_key` | Press, hold, or release a named key. |
| `computer_type_text` | Type up to 5000 Unicode UTF-16 code units into the focused control. |
| `computer_release_all` | Release every key and mouse button held by this server. |
| `computer_action_batch` | Run 1 to 30 ordered actions with progress reports and failure cleanup. |
| `computer_ui_inspect` | Inspect a bounded UI Automation tree for the foreground window or the element at a point. |

Coordinates use the Windows virtual desktop pixel space. The screenshot starts at image coordinate (0, 0); add its reported left and top values to map image points to Windows coordinates. Coordinates outside the current virtual desktop are rejected.

Key names include letters, digits, F1 to F24, CTRL, SHIFT, ALT, WIN, ENTER, ESC, TAB, SPACE, arrows, HOME, END, PAGEUP, PAGEDOWN, INSERT, DELETE, and numpad keys. computer_type_text uses Unicode input so it can type characters that do not have a key in the current keyboard layout.

UI Automation reads element names, control types, automation IDs, process IDs, state, and bounds. It does not read text values or password contents. Inspection is limited to depth 8 and 250 elements.

## Batches

Each action has a type and type-specific fields. For example:

```json
{
  "actions": [
    { "type": "move", "x": 500, "y": 400 },
    { "type": "click", "x": 500, "y": 400 },
    { "type": "type", "text": "Hello" },
    { "type": "key", "key": "ENTER", "action": "press" }
  ]
}
```

Supported action types are move, click, scroll, drag, type, key, mouse_button, wait, and release_all. The server validates every action before it starts the batch. Limits are 30 actions, 5000 combined text code units, at most 3000 ms per wait or drag, and at most 10000 ms of requested delay per batch. A failed or cancelled action stops the batch and releases held input. The result includes the number of completed actions; MCP clients that request progress receive an update after each action.

## Safety and behavior

- Screenshots are captured only when requested and held in memory; they are not written to disk.
- Input and inspection calls are serialized so two MCP calls cannot interleave desktop actions.
- Held keys and buttons remain held until released, computer_release_all is called, an action fails or is cancelled, or the server exits. Use computer_release_all to recover from a forgotten hold.
- This server cannot interact with the secure desktop. Windows may also block input into applications running at a higher integrity level.
- UI Automation and screenshots can expose information visible on screen to the MCP client. Only connect a client you trust.

## Implementation

- C# on .NET 8, using the official Model Context Protocol C# SDK.
- Screen capture uses Graphics.CopyFromScreen; virtual desktops larger than 50 million pixels are rejected to bound memory use.
- Mouse and keyboard input use Windows SetCursorPos and SendInput.
- UI inspection uses Windows UI Automation Control View.

## Verification performed

- `dotnet build PcUse.csproj` completed with 0 warnings and 0 errors from a clean checkout.
- An MCP stdio smoke check completed initialization, listed all 11 tools, and confirmed the batch tool exposes its `actions` schema.
- OpenCode MCP discovery is verified with `opencode mcp list`.
- Live desktop input and screenshot calls were not invoked during verification.

PC Use targets native Windows desktop controls and does not use browser automation.
