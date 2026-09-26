using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json.Serialization;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<ComputerTools>();

var host = builder.Build();
try
{
    await host.RunAsync();
}
finally
{
    await ComputerTools.ReleaseHeldInputsAtShutdownAsync();
}

[McpServerToolType]
[SupportedOSPlatform("windows")]
public sealed class ComputerTools
{
    private const int MaximumTextLength = 5000;
    private const int MaximumBatchActions = 30;
    private const int MaximumBatchTextLength = 5000;
    private const int MaximumBatchDelayMs = 10000;
    private const long MaximumScreenPixels = 50000000;

    private static readonly SemaphoreSlim DesktopGate = new(1, 1);
    private static readonly InputController Input = new();

    [McpServerTool(Name = "computer_screenshot")]
    [Description("Capture the current Windows virtual desktop on demand. Returns a PNG and desktop bounds; the screenshot is kept in memory and not saved.")]
    public Task<CallToolResult> CaptureDesktop(CancellationToken cancellationToken) =>
        RunExclusiveAsync(CaptureDesktopCore, cancellationToken);

    [McpServerTool(Name = "computer_mouse_move")]
    [Description("Move the mouse cursor to an absolute virtual-desktop pixel coordinate.")]
    public Task<CallToolResult> MouseMove(
        [Description("Absolute desktop X coordinate in pixels.")] int x,
        [Description("Absolute desktop Y coordinate in pixels.")] int y,
        CancellationToken cancellationToken) =>
        RunExclusiveAsync(() =>
        {
            Input.Move(x, y);
            return Success($"Mouse moved to ({x}, {y}).");
        }, cancellationToken);

    [McpServerTool(Name = "computer_mouse_click")]
    [Description("Click a mouse button at an absolute virtual-desktop pixel coordinate.")]
    public Task<CallToolResult> MouseClick(
        [Description("Absolute desktop X coordinate in pixels.")] int x,
        [Description("Absolute desktop Y coordinate in pixels.")] int y,
        [Description("Button: left, right, middle, x1, or x2.")] string button = "left",
        [Description("Number of clicks, from 1 to 3.")] int clickCount = 1,
        CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(() =>
        {
            Input.Click(x, y, button, clickCount);
            return Success($"Clicked {button} at ({x}, {y}) {clickCount} time(s).");
        }, cancellationToken);

    [McpServerTool(Name = "computer_mouse_scroll")]
    [Description("Scroll at an absolute desktop coordinate. Tick values are bounded to -10..10; positive vertical ticks scroll up and positive horizontal ticks scroll right.")]
    public Task<CallToolResult> MouseScroll(
        [Description("Absolute desktop X coordinate in pixels.")] int x,
        [Description("Absolute desktop Y coordinate in pixels.")] int y,
        [Description("Vertical wheel ticks, from -10 to 10. Positive scrolls up.")] int verticalTicks,
        [Description("Horizontal wheel ticks, from -10 to 10. Positive scrolls right.")] int horizontalTicks = 0,
        CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(() =>
        {
            Input.Scroll(x, y, verticalTicks, horizontalTicks);
            return Success($"Scrolled at ({x}, {y}): vertical={verticalTicks}, horizontal={horizontalTicks}.");
        }, cancellationToken);

    [McpServerTool(Name = "computer_mouse_drag")]
    [Description("Drag a mouse button between two absolute virtual-desktop pixel coordinates. The button is released on failure or cancellation.")]
    public Task<CallToolResult> MouseDrag(
        [Description("Drag start X coordinate in desktop pixels.")] int startX,
        [Description("Drag start Y coordinate in desktop pixels.")] int startY,
        [Description("Drag end X coordinate in desktop pixels.")] int endX,
        [Description("Drag end Y coordinate in desktop pixels.")] int endY,
        [Description("Button: left, right, middle, x1, or x2.")] string button = "left",
        [Description("Drag duration from 0 to 3000 milliseconds.")] int durationMs = 250,
        CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(() =>
        {
            Input.Drag(startX, startY, endX, endY, button, durationMs, cancellationToken);
            return Success($"Dragged {button} from ({startX}, {startY}) to ({endX}, {endY}).");
        }, cancellationToken);

    [McpServerTool(Name = "computer_mouse_button")]
    [Description("Hold or release a mouse button. Use computer_release_all if a held button needs to be cleared immediately.")]
    public Task<CallToolResult> MouseButton(
        [Description("Button: left, right, middle, x1, or x2.")] string button,
        [Description("Action: press/hold or release.")] string action,
        CancellationToken cancellationToken) =>
        RunExclusiveAsync(
            () => Success(Input.MouseButtonAction(button, action)),
            cancellationToken);

    [McpServerTool(Name = "computer_key")]
    [Description("Press, hold, or release a named keyboard key. Supports letters, digits, F1-F24, and documented names such as CTRL, SHIFT, ALT, ENTER, ESC, TAB, SPACE, arrows, HOME, END, INSERT, and DELETE.")]
    public Task<CallToolResult> Key(
        [Description("Key name, for example CTRL, A, ENTER, LEFT, or F5.")] string key,
        [Description("Action: press, hold, or release.")] string action = "press",
        CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(
            () => Success(Input.KeyAction(key, action)),
            cancellationToken);

    [McpServerTool(Name = "computer_type_text")]
    [Description("Type Unicode text into the currently focused control. Limited to 5000 UTF-16 code units per call.")]
    public Task<CallToolResult> TypeText(
        [Description("Text to type into the focused application.")] string text,
        CancellationToken cancellationToken) =>
        RunExclusiveAsync(() =>
        {
            Input.TypeText(text, MaximumTextLength);
            return Success($"Typed {text.Length} UTF-16 code unit(s).");
        }, cancellationToken);

    [McpServerTool(Name = "computer_release_all")]
    [Description("Release every keyboard key and mouse button that PC Use is currently holding.")]
    public Task<CallToolResult> ReleaseAll(CancellationToken cancellationToken) =>
        RunExclusiveAsync(() =>
        {
            var result = Input.ReleaseAll();
            return HasCleanupFailures(result) ? Error(result) : Success(result);
        }, cancellationToken);

    [McpServerTool(Name = "computer_action_batch")]
    [Description("Run 1-30 ordered desktop actions as one exclusive batch. All actions are validated before the first input; reports MCP progress and releases held inputs if an action fails or is cancelled. Action types: move, click, scroll, drag, type, key, mouse_button, wait, release_all.")]
    public Task<CallToolResult> ActionBatch(
        [Description("Ordered actions. Fields depend on type: move/click/scroll use x,y; drag uses x,y,endX,endY; type uses text; key uses key and optional action; mouse_button uses button and action; wait uses durationMs.")] IReadOnlyList<ComputerAction> actions,
        IProgress<ProgressNotificationValue> progress,
        CancellationToken cancellationToken) =>
        RunBatchAsync(actions, progress, cancellationToken);

    [McpServerTool(Name = "computer_ui_inspect")]
    [Description("Inspect bounded Windows UI Automation metadata for the foreground window or the element at a desktop point. Reads names and control metadata only; it never reads text values or password contents.")]
    public Task<CallToolResult> InspectUi(
        [Description("Optional absolute desktop X coordinate. Provide x and y together to inspect the element at that point.")] int? x = null,
        [Description("Optional absolute desktop Y coordinate. Provide x and y together to inspect the element at that point.")] int? y = null,
        [Description("Maximum descendant depth, from 0 to 8.")] int maxDepth = 3,
        [Description("Maximum elements returned, from 1 to 250.")] int maxElements = 100,
        CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(() =>
        {
            var json = UiAutomationInspector.Inspect(x, y, maxDepth, maxElements, Input.DesktopBounds);
            return Success(json);
        }, cancellationToken);

    internal static async Task ReleaseHeldInputsAtShutdownAsync()
    {
        await DesktopGate.WaitAsync();
        try
        {
            _ = Input.ReleaseAll();
        }
        finally
        {
            DesktopGate.Release();
        }
    }

    private static CallToolResult CaptureDesktopCore()
    {
        var bounds = Input.DesktopBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidOperationException("Windows did not report valid virtual desktop bounds.");
        }

        if ((long)bounds.Width * bounds.Height > MaximumScreenPixels)
        {
            throw new InvalidOperationException(
                $"The virtual desktop is too large to capture safely ({bounds.Width} x {bounds.Height} pixels; limit {MaximumScreenPixels} pixels).");
        }

        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(
                bounds.Location,
                Point.Empty,
                bounds.Size,
                CopyPixelOperation.SourceCopy);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock
                {
                    Text = $"Virtual desktop bounds: left={bounds.Left}, top={bounds.Top}, width={bounds.Width}, height={bounds.Height}. Image coordinates start at (0, 0); add left/top to map a point to Windows desktop coordinates. Screenshot was kept in memory and not saved."
                },
                ImageContentBlock.FromBytes(stream.ToArray(), "image/png")
            ]
        };
    }

    private static async Task<CallToolResult> RunExclusiveAsync(
        Func<CallToolResult> operation,
        CancellationToken cancellationToken)
    {
        var acquired = false;
        try
        {
            await DesktopGate.WaitAsync(cancellationToken);
            acquired = true;
            cancellationToken.ThrowIfCancellationRequested();
            return operation();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cleanup = acquired ? Input.ReleaseAll() : "No input action had started.";
            return Error($"Operation cancelled. {cleanup}");
        }
        catch (Exception exception)
        {
            var cleanup = acquired ? Input.ReleaseAll() : "No input action had started.";
            return Error($"{SafeError(exception)} Held-input cleanup: {cleanup}");
        }
        finally
        {
            if (acquired)
            {
                DesktopGate.Release();
            }
        }
    }

    private static async Task<CallToolResult> RunBatchAsync(
        IReadOnlyList<ComputerAction> actions,
        IProgress<ProgressNotificationValue> progress,
        CancellationToken cancellationToken)
    {
        var acquired = false;
        var completed = 0;
        try
        {
            await DesktopGate.WaitAsync(cancellationToken);
            acquired = true;
            cancellationToken.ThrowIfCancellationRequested();
            ValidateBatch(actions);

            progress.Report(new ProgressNotificationValue
            {
                Progress = 0,
                Total = actions.Count,
                Message = "Desktop action batch started."
            });

            for (var index = 0; index < actions.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ApplyAction(actions[index], cancellationToken);
                completed++;
                progress.Report(new ProgressNotificationValue
                {
                    Progress = completed,
                    Total = actions.Count,
                    Message = $"Completed action {completed} of {actions.Count}."
                });
            }

            return Success($"Completed {completed} of {actions.Count} desktop actions.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cleanup = acquired ? Input.ReleaseAll() : "No input action had started.";
            progress.Report(new ProgressNotificationValue
            {
                Progress = completed,
                Total = actions?.Count,
                Message = $"Batch cancelled after {completed} completed action(s)."
            });
            return Error($"Batch cancelled after {completed} completed action(s). Held-input cleanup: {cleanup}");
        }
        catch (Exception exception)
        {
            var cleanup = acquired ? Input.ReleaseAll() : "No input action had started.";
            progress.Report(new ProgressNotificationValue
            {
                Progress = completed,
                Total = actions?.Count,
                Message = $"Batch stopped after {completed} completed action(s)."
            });
            return Error($"Batch stopped after {completed} completed action(s). {SafeError(exception)} Held-input cleanup: {cleanup}");
        }
        finally
        {
            if (acquired)
            {
                DesktopGate.Release();
            }
        }
    }

    private static void ValidateBatch(IReadOnlyList<ComputerAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        if (actions.Count is < 1 or > MaximumBatchActions)
        {
            throw new ArgumentOutOfRangeException(nameof(actions), $"A batch must contain 1 to {MaximumBatchActions} actions.");
        }

        var totalTextLength = 0;
        var totalDelayMs = 0;
        for (var index = 0; index < actions.Count; index++)
        {
            var action = actions[index] ?? throw new ArgumentException($"Action {index + 1} is null.", nameof(actions));
            var type = Normalize(action.Type);
            switch (type)
            {
                case "move":
                case "click":
                case "scroll":
                    Input.ValidatePoint(Required(action.X, "x", index), Required(action.Y, "y", index));
                    if (type == "click")
                    {
                        _ = InputController.ParseMouseButton(action.Button ?? "left");
                        InputController.ValidateClickCount(action.ClickCount ?? 1);
                        totalDelayMs += Math.Max(0, (action.ClickCount ?? 1) - 1) * 50;
                    }
                    else if (type == "scroll")
                    {
                        InputController.ValidateScroll(action.VerticalTicks ?? 0, action.HorizontalTicks ?? 0);
                    }

                    break;
                case "drag":
                    Input.ValidatePoint(Required(action.X, "x", index), Required(action.Y, "y", index));
                    Input.ValidatePoint(Required(action.EndX, "endX", index), Required(action.EndY, "endY", index));
                    _ = InputController.ParseMouseButton(action.Button ?? "left");
                    InputController.ValidateDragDuration(action.DurationMs ?? 250);
                    totalDelayMs += action.DurationMs ?? 250;
                    break;
                case "type":
                    if (action.Text is null)
                    {
                        throw new ArgumentException($"Action {index + 1} of type 'type' requires text.", nameof(actions));
                    }

                    totalTextLength += action.Text.Length;
                    if (totalTextLength > MaximumBatchTextLength)
                    {
                        throw new ArgumentOutOfRangeException(nameof(actions), $"Combined batch text is limited to {MaximumBatchTextLength} UTF-16 code units.");
                    }

                    if (action.Text.Contains('\0'))
                    {
                        throw new ArgumentException($"Action {index + 1} text cannot contain a null character.", nameof(actions));
                    }

                    break;
                case "key":
                    InputController.ValidateKey(action.Key ?? throw new ArgumentException($"Action {index + 1} of type 'key' requires key.", nameof(actions)));
                    ValidateKeyAction(action.Action ?? "press");
                    break;
                case "mouse_button":
                    _ = InputController.ParseMouseButton(action.Button ?? throw new ArgumentException($"Action {index + 1} of type 'mouse_button' requires button.", nameof(actions)));
                    ValidateMouseButtonAction(action.Action ?? throw new ArgumentException($"Action {index + 1} of type 'mouse_button' requires action.", nameof(actions)));
                    break;
                case "wait":
                    var waitMs = action.DurationMs ?? 0;
                    if (waitMs is < 0 or > 3000)
                    {
                        throw new ArgumentOutOfRangeException(nameof(actions), $"Action {index + 1} wait duration must be between 0 and 3000 milliseconds.");
                    }

                    totalDelayMs += waitMs;
                    break;
                case "release_all":
                    break;
                default:
                    throw new ArgumentException($"Action {index + 1} has unsupported type '{action.Type}'.", nameof(actions));
            }

            if (totalDelayMs > MaximumBatchDelayMs)
            {
                throw new ArgumentOutOfRangeException(nameof(actions), $"Combined requested delay is limited to {MaximumBatchDelayMs} milliseconds.");
            }
        }
    }

    private static void ApplyAction(ComputerAction action, CancellationToken cancellationToken)
    {
        var type = Normalize(action.Type);
        switch (type)
        {
            case "move":
                Input.Move(action.X!.Value, action.Y!.Value);
                break;
            case "click":
                Input.Click(action.X!.Value, action.Y!.Value, action.Button ?? "left", action.ClickCount ?? 1);
                break;
            case "scroll":
                Input.Scroll(action.X!.Value, action.Y!.Value, action.VerticalTicks ?? 0, action.HorizontalTicks ?? 0);
                break;
            case "drag":
                Input.Drag(
                    action.X!.Value,
                    action.Y!.Value,
                    action.EndX!.Value,
                    action.EndY!.Value,
                    action.Button ?? "left",
                    action.DurationMs ?? 250,
                    cancellationToken);
                break;
            case "type":
                Input.TypeText(action.Text!, MaximumBatchTextLength);
                break;
            case "key":
                _ = Input.KeyAction(action.Key!, action.Action ?? "press");
                break;
            case "mouse_button":
                _ = Input.MouseButtonAction(action.Button!, action.Action!);
                break;
            case "wait":
                var waitMs = action.DurationMs ?? 0;
                if (waitMs > 0)
                {
                    cancellationToken.WaitHandle.WaitOne(waitMs);
                }

                cancellationToken.ThrowIfCancellationRequested();
                break;
            case "release_all":
                var cleanup = Input.ReleaseAll();
                if (HasCleanupFailures(cleanup))
                {
                    throw new InvalidOperationException(cleanup);
                }

                break;
            default:
                throw new InvalidOperationException($"Unsupported action type '{action.Type}'.");
        }
    }

    private static void ValidateKeyAction(string action)
    {
        if (Normalize(action) is not ("press" or "hold" or "release"))
        {
            throw new ArgumentException("Key action must be press, hold, or release.");
        }
    }

    private static void ValidateMouseButtonAction(string action)
    {
        if (Normalize(action) is not ("press" or "hold" or "release"))
        {
            throw new ArgumentException("Mouse button action must be press, hold, or release.");
        }
    }

    private static int Required(int? value, string field, int actionIndex) =>
        value ?? throw new ArgumentException($"Action {actionIndex + 1} requires {field}.");

    private static bool HasCleanupFailures(string result) =>
        result.Contains("cleanup failures:", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Trim().ToLowerInvariant();
    }

    private static string SafeError(Exception exception)
    {
        var message = exception.Message.ReplaceLineEndings(" ");
        if (message.Length > 500)
        {
            message = message[..500];
        }

        return $"{exception.GetType().Name}: {message}";
    }

    private static CallToolResult Success(string message) =>
        new()
        {
            Content = [new TextContentBlock { Text = message }]
        };

    private static CallToolResult Error(string message) =>
        new()
        {
            IsError = true,
            Content = [new TextContentBlock { Text = message }]
        };
}

public sealed class ComputerAction
{
    [JsonPropertyName("type")]
    [Required]
    [Description("move, click, scroll, drag, type, key, mouse_button, wait, or release_all.")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("x")]
    [Description("Absolute desktop X coordinate; start X for a drag.")]
    public int? X { get; init; }

    [JsonPropertyName("y")]
    [Description("Absolute desktop Y coordinate; start Y for a drag.")]
    public int? Y { get; init; }

    [JsonPropertyName("endX")]
    [Description("Drag end X coordinate.")]
    public int? EndX { get; init; }

    [JsonPropertyName("endY")]
    [Description("Drag end Y coordinate.")]
    public int? EndY { get; init; }

    [JsonPropertyName("button")]
    [Description("Mouse button: left, right, middle, x1, or x2.")]
    public string? Button { get; init; }

    [JsonPropertyName("clickCount")]
    [Description("Click count from 1 to 3; click actions only.")]
    public int? ClickCount { get; init; }

    [JsonPropertyName("verticalTicks")]
    [Description("Vertical scroll ticks from -10 to 10; positive scrolls up.")]
    public int? VerticalTicks { get; init; }

    [JsonPropertyName("horizontalTicks")]
    [Description("Horizontal scroll ticks from -10 to 10; positive scrolls right.")]
    public int? HorizontalTicks { get; init; }

    [JsonPropertyName("key")]
    [Description("Key name such as CTRL, A, ENTER, LEFT, or F5.")]
    public string? Key { get; init; }

    [JsonPropertyName("action")]
    [Description("Key or mouse button action: press, hold, or release.")]
    public string? Action { get; init; }

    [JsonPropertyName("text")]
    [Description("Unicode text for a type action.")]
    public string? Text { get; init; }

    [JsonPropertyName("durationMs")]
    [Description("Duration for drag or wait actions; wait is limited to 3000 ms and drag to 3000 ms.")]
    public int? DurationMs { get; init; }
}
