using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;
using WpfPoint = System.Windows.Point;

internal static class UiAutomationInspector
{
    public static string Inspect(
        int? x,
        int? y,
        int maxDepth,
        int maxElements,
        Rectangle desktopBounds)
    {
        if ((x is null) != (y is null))
        {
            throw new ArgumentException("Provide both x and y to inspect the UI element at a desktop point, or omit both to inspect the foreground window.");
        }

        if (maxDepth is < 0 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDepth), "maxDepth must be between 0 and 8.");
        }

        if (maxElements is < 1 or > 250)
        {
            throw new ArgumentOutOfRangeException(nameof(maxElements), "maxElements must be between 1 and 250.");
        }

        AutomationElement target;
        string targetDescription;
        if (x.HasValue && y.HasValue)
        {
            if (!desktopBounds.Contains(x.Value, y.Value))
            {
                throw new ArgumentOutOfRangeException(nameof(x), "The requested point is outside the current virtual desktop.");
            }

            target = AutomationElement.FromPoint(new WpfPoint(x.Value, y.Value));
            targetDescription = $"element at desktop point ({x.Value}, {y.Value})";
        }
        else
        {
            var foregroundWindow = GetForegroundWindow();
            if (foregroundWindow == IntPtr.Zero)
            {
                throw new InvalidOperationException("Windows did not report a foreground window.");
            }

            target = AutomationElement.FromHandle(foregroundWindow);
            targetDescription = "foreground window";
        }

        var elements = new List<UiElementSummary>(Math.Min(maxElements, 32));
        var pending = new Stack<TraversalFrame>();
        pending.Push(new TraversalFrame(target, 0));
        var truncated = false;
        var walker = TreeWalker.ControlViewWalker;

        while (pending.Count > 0 && elements.Count < maxElements)
        {
            var frame = pending.Peek();
            if (!frame.Emitted)
            {
                elements.Add(ReadElement(frame.Element, elements.Count + 1, frame.Depth));
                frame.Emitted = true;
            }

            if (frame.Depth >= maxDepth)
            {
                try
                {
                    truncated |= walker.GetFirstChild(frame.Element) is not null;
                }
                catch (ElementNotAvailableException)
                {
                    truncated = true;
                }
                catch (COMException)
                {
                    truncated = true;
                }

                pending.Pop();
                continue;
            }

            if (!frame.Initialized)
            {
                try
                {
                    frame.NextChild = walker.GetFirstChild(frame.Element);
                    frame.Initialized = true;
                }
                catch (ElementNotAvailableException)
                {
                    truncated = true;
                    pending.Pop();
                    continue;
                }
                catch (COMException)
                {
                    truncated = true;
                    pending.Pop();
                    continue;
                }
            }

            if (frame.NextChild is null)
            {
                pending.Pop();
                continue;
            }

            if (frame.ChildrenVisited >= maxElements)
            {
                truncated = true;
                pending.Pop();
                continue;
            }

            var child = frame.NextChild;
            try
            {
                frame.NextChild = walker.GetNextSibling(child);
            }
            catch (ElementNotAvailableException)
            {
                frame.NextChild = null;
                truncated = true;
            }
            catch (COMException)
            {
                frame.NextChild = null;
                truncated = true;
            }

            frame.ChildrenVisited++;
            pending.Push(new TraversalFrame(child, frame.Depth + 1));
        }

        truncated |= pending.Count > 0;
        var result = new UiInspectionResult(
            targetDescription,
            maxDepth,
            maxElements,
            elements.Count,
            truncated,
            "Names and control metadata only. Text values and password contents are never read.",
            elements);

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    private static UiElementSummary ReadElement(AutomationElement element, int index, int depth)
    {
        var isPassword = Safe(() => element.Current.IsPassword, false);
        var name = isPassword ? "[password field]" : Safe(() => element.Current.Name, string.Empty);
        var controlType = Safe(() => element.Current.ControlType.ProgrammaticName, string.Empty);
        if (controlType.StartsWith("ControlType.", StringComparison.Ordinal))
        {
            controlType = controlType["ControlType.".Length..];
        }

        var bounds = Safe(() => element.Current.BoundingRectangle, System.Windows.Rect.Empty);
        RectSummary? rect = bounds.IsEmpty
            ? null
            : new RectSummary(bounds.X, bounds.Y, bounds.Width, bounds.Height);

        return new UiElementSummary(
            index,
            depth,
            name,
            controlType,
            Safe(() => element.Current.AutomationId, string.Empty),
            Safe(() => element.Current.ProcessId, 0),
            Safe(() => element.Current.IsEnabled, false),
            Safe(() => element.Current.IsOffscreen, false),
            Safe(() => element.Current.IsKeyboardFocusable, false),
            isPassword,
            rect);
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (ElementNotAvailableException)
        {
            return fallback;
        }
        catch (COMException)
        {
            return fallback;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    private sealed record UiInspectionResult(
        string Target,
        int MaxDepth,
        int MaxElements,
        int ReturnedElements,
        bool Truncated,
        string DataPolicy,
        IReadOnlyList<UiElementSummary> Elements);

    private sealed record UiElementSummary(
        int Index,
        int Depth,
        string Name,
        string ControlType,
        string AutomationId,
        int ProcessId,
        bool IsEnabled,
        bool IsOffscreen,
        bool IsKeyboardFocusable,
        bool IsPassword,
        RectSummary? Bounds);

    private sealed record RectSummary(double X, double Y, double Width, double Height);

    private sealed class TraversalFrame(AutomationElement element, int depth)
    {
        public AutomationElement Element { get; } = element;

        public int Depth { get; } = depth;

        public bool Emitted { get; set; }

        public bool Initialized { get; set; }

        public int ChildrenVisited { get; set; }

        public AutomationElement? NextChild { get; set; }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
