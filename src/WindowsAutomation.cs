using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace JetDesk;

/// <summary>
/// Local Windows UI Automation adapter. Calls must be serialized on an MTA worker.
/// A model can only address controls/apps that this adapter has actually observed.
/// </summary>
public sealed class WindowsAutomation
{
    private const int MaxVisitedNodes = 1800;
    private const int MaxControls = 450;
    private const int MaxDepth = 22;
    private readonly int _ownPid = Environment.ProcessId;
    private readonly Dictionary<string, ObservedControl> _controls = new(StringComparer.Ordinal);
    private readonly Dictionary<long, uint> _observedWindowPids = new();
    private readonly Dictionary<string, AppInfo> _apps = new(StringComparer.Ordinal);
    private List<AppInfo>? _appList;
    private Snapshot? _lastSnapshot;
    private int _snapshotNumber;

    public long? TargetHandle { get; set; }

    private sealed record ObservedControl(AutomationElement Element, int[] RuntimeId,
        long WindowHandle, int ProcessId, string Name, string AutomationId, ControlType Type, List<string> Actions);

    public List<WindowInfo> ListWindows()
    {
        var result = new List<WindowInfo>();
        Native.EnumWindows((handle, _) =>
        {
            if (!Native.IsWindowVisible(handle) || IsCloaked(handle)) return true;
            Native.GetWindowThreadProcessId(handle, out uint pid);
            if (pid == _ownPid || pid == 0) return true;
            string title = WindowTitle(handle);
            if (string.IsNullOrWhiteSpace(title)) return true;
            string process = ProcessName((int)pid);
            result.Add(new WindowInfo(WindowId(handle), Trim(title, 250), process, handle.ToInt64()));
            return result.Count < 80;
        }, IntPtr.Zero);
        return result;
    }

    public List<AppInfo> ListApps()
    {
        if (_appList is not null) return _appList.ToList();
        var discovered = new List<(string Name, string Path)>();
        object? shellObject = null, folderObject = null, itemsObject = null;
        try
        {
            Type? shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) return [];
            shellObject = Activator.CreateInstance(shellType);
            dynamic shell = shellObject!;
            folderObject = shell.NameSpace("shell:AppsFolder");
            if (folderObject is null) return [];
            dynamic folder = folderObject;
            itemsObject = folder.Items();
            dynamic items = itemsObject;
            int count = Math.Min((int)items.Count, 1000);
            for (int i = 0; i < count; i++)
            {
                object? itemObject = null;
                try
                {
                    itemObject = items.Item(i);
                    dynamic item = itemObject;
                    string name = (string)item.Name;
                    string path = (string)item.Path;
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(path))
                        discovered.Add((name, path));
                }
                catch (COMException) { }
                finally { ReleaseCom(itemObject); }
            }
        }
        catch (COMException) { }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        finally
        {
            ReleaseCom(itemsObject);
            ReleaseCom(folderObject);
            ReleaseCom(shellObject);
        }

        _appList = discovered.DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select((entry, index) => new AppInfo($"app{index + 1}", Trim(entry.Name, 120), entry.Path))
            .ToList();
        foreach (AppInfo app in _appList) _apps[app.Id] = app;
        return _appList.ToList();
    }

    public Snapshot Observe(long? targetHandle = null)
    {
        _controls.Clear();
        _snapshotNumber++;
        var windows = ListWindows();
        var apps = ListApps();
        if (targetHandle.HasValue) TargetHandle = targetHandle;
        WindowInfo? target = windows.FirstOrDefault(w => w.Handle == TargetHandle);
        if (target is null)
        {
            long foreground = Native.GetForegroundWindow().ToInt64();
            target = windows.FirstOrDefault(w => w.Handle == foreground);
            TargetHandle = target?.Handle;
        }
        if (target is null)
            return SaveSnapshot(new Snapshot(new WindowInfo("desktop", "Desktop — choose a window or app", "", 0),
                [], windows, apps, null, false));
        if (Native.IsIconic(new IntPtr(target.Handle))) FocusWindow(target.Handle);

        var results = new List<ControlInfo>();
        string? focusedId = null;
        bool truncated = false;
        try
        {
            var cache = MakeCache();
            AutomationElement root = AutomationElement.FromHandle(new IntPtr(target.Handle));
            var pending = new Stack<(AutomationElement Element, int Depth, string Parent)>();
            pending.Push((root.GetUpdatedCache(cache), 0, target.Title));
            int visited = 0;
            var timer = Stopwatch.StartNew();
            while (pending.Count != 0)
            {
                if (++visited > MaxVisitedNodes || results.Count >= MaxControls || timer.ElapsedMilliseconds > 7000)
                {
                    truncated = true;
                    break;
                }
                var (element, depth, parent) = pending.Pop();
                try
                {
                    bool offscreen = Cached(element, AutomationElement.IsOffscreenProperty, true);
                    bool enabled = Cached(element, AutomationElement.IsEnabledProperty, false);
                    bool password = Cached(element, AutomationElement.IsPasswordProperty, false);
                    string name = Clean(Cached(element, AutomationElement.NameProperty, ""), 300);
                    ControlType type = Cached(element, AutomationElement.ControlTypeProperty, ControlType.Custom);
                    string role = type.ProgrammaticName.Replace("ControlType.", "", StringComparison.Ordinal);
                    var rectangle = Cached(element, AutomationElement.BoundingRectangleProperty, System.Windows.Rect.Empty);
                    string nextParent = string.IsNullOrEmpty(name) ? parent : Trim($"{role}: {name}", 180);

                    // Traverse unnamed containers too: their children may be the actual controls.
                    if (depth < MaxDepth)
                    {
                        var children = new List<AutomationElement>();
                        AutomationElement? child = TreeWalker.ControlViewWalker.GetFirstChild(element, cache);
                        while (child is not null && children.Count < 700 && timer.ElapsedMilliseconds <= 7000)
                        {
                            children.Add(child);
                            child = TreeWalker.ControlViewWalker.GetNextSibling(child, cache);
                        }
                        if (child is not null) truncated = true;
                        for (int i = children.Count - 1; i >= 0; i--)
                            pending.Push((children[i], depth + 1, nextParent));
                    }
                    else truncated = true;

                    if (offscreen || rectangle.IsEmpty || rectangle.Width <= 0 || rectangle.Height <= 0) continue;
                    var actions = GetActions(element, type, enabled, password);
                    string value = password ? "" : ReadValue(element, type);
                    if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(value) && actions.Count == 0) continue;
                    // Chromium exposes Invoke on many anonymous layout wrappers. Their named
                    // descendants carry the actual meaning; keep genuine scrolling/input regions.
                    if ((type == ControlType.Group || type == ControlType.Pane) &&
                        string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(value) &&
                        actions.Count == 1 && actions[0] == "invoke") continue;
                    // Window/title and anonymous layout panes add no useful independent action.
                    if (depth == 0 || (type == ControlType.Pane && actions.Count == 0 && name == parent)) continue;
                    string id = $"c{_snapshotNumber}_{results.Count + 1}";
                    int[] runtimeId = Cached(element, AutomationElement.RuntimeIdProperty, Array.Empty<int>());
                    if (runtimeId.Length == 0) runtimeId = element.GetRuntimeId();
                    int processId = Cached(element, AutomationElement.ProcessIdProperty, 0);
                    string automationId = Cached(element, AutomationElement.AutomationIdProperty, "");
                    _controls[id] = new ObservedControl(element, runtimeId, target.Handle, processId, name, automationId, type, actions);
                    results.Add(new ControlInfo(id, role, name, value, Trim(parent, 180),
                        automationId,
                        ToRect(rectangle), actions, enabled, password));
                    if (Cached(element, AutomationElement.HasKeyboardFocusProperty, false)) focusedId = id;
                }
                catch (ElementNotAvailableException) { }
                catch (InvalidOperationException) { }
                catch (COMException) { }
            }
        }
        catch (ElementNotAvailableException) { truncated = true; }
        catch (COMException) { truncated = true; }
        catch (InvalidOperationException) { truncated = true; }
        return SaveSnapshot(new Snapshot(target, results, windows, apps, focusedId, truncated));
    }

    public string Execute(Decision decision, Snapshot snapshot, string? text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!ReferenceEquals(snapshot, _lastSnapshot))
            throw new InvalidOperationException("The UI snapshot was superseded; observe again before acting.");
        string operation = decision.Operation.ToLowerInvariant();
        if (operation == "wait")
        {
            if (ct.WaitHandle.WaitOne(650)) ct.ThrowIfCancellationRequested();
            return "Waited for the application.";
        }
        if (operation == "activate_window")
        {
            WindowInfo window = snapshot.Windows.FirstOrDefault(w => w.Id == decision.TargetId)
                ?? throw new InvalidOperationException("Window was not present in the observed window inventory.");
            RequireLiveWindow(window);
            var current = ListWindows().FirstOrDefault(w => w.Handle == window.Handle && w.ProcessName == window.ProcessName)
                ?? throw new InvalidOperationException("The chosen window no longer exists.");
            TargetHandle = current.Handle;
            FocusWindow(current.Handle);
            return $"Activated {current.Title}.";
        }
        if (operation == "launch_app")
        {
            AppInfo app = snapshot.Apps.FirstOrDefault(a => a.Id == decision.TargetId)
                ?? throw new InvalidOperationException("Application was not present in the observed application inventory.");
            LaunchObservedApp(app, ct);
            return $"Opened {app.Name}.";
        }
        if (operation == "press_key")
        {
            RequireLiveWindow(snapshot.Window);
            FocusWindow(snapshot.Window.Handle);
            SendNamedKey(decision.Key ?? throw new InvalidOperationException("No key was selected."), ct);
            return $"Pressed {decision.Key}.";
        }
        if (decision.TargetId is null || !_controls.TryGetValue(decision.TargetId, out ObservedControl? observed))
            throw new InvalidOperationException("Control is not part of the current observed state.");
        if (!observed.Actions.Contains(operation, StringComparer.Ordinal))
            throw new InvalidOperationException($"The observed control does not support {operation}.");
        RequireLiveWindow(snapshot.Window);
        AutomationElement element = ResolveCurrent(observed);
        if (!element.Current.IsEnabled || element.Current.IsOffscreen)
            throw new InvalidOperationException("The control is disabled or no longer visible.");
        FocusWindow(observed.WindowHandle);
        ct.ThrowIfCancellationRequested();

        switch (operation)
        {
            case "invoke":
                if (element.TryGetCurrentPattern(InvokePattern.Pattern, out object invoke))
                    ((InvokePattern)invoke).Invoke();
                else ClickElement(element, observed.ProcessId);
                return "Invoked the selected control.";
            case "set_text":
                if (text is null || text.Length > 4096) throw new InvalidOperationException("Invalid text candidate.");
                if (element.Current.IsPassword) throw new InvalidOperationException("Password fields require manual input.");
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object value))
                {
                    var pattern = (ValuePattern)value;
                    if (pattern.Current.IsReadOnly) throw new InvalidOperationException("This field is read-only.");
                    pattern.SetValue(text);
                    try { element.SetFocus(); } catch (InvalidOperationException) { }
                }
                else
                {
                    FocusElement(element, observed.ProcessId);
                    SendChord([Native.VK_CONTROL], 0x41);
                    SendUnicode(text, observed.WindowHandle, ct);
                }
                return $"Entered the selected text candidate ({text.Length} characters).";
            case "select":
                if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object selection))
                    ((SelectionItemPattern)selection).Select();
                else ClickElement(element, observed.ProcessId);
                return "Selected the control.";
            case "toggle":
                if (element.TryGetCurrentPattern(TogglePattern.Pattern, out object toggle))
                    ((TogglePattern)toggle).Toggle();
                else ClickElement(element, observed.ProcessId);
                return "Toggled the control.";
            case "expand":
                if (!element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out object expansion))
                    throw new InvalidOperationException("The expansion pattern is no longer available.");
                var expand = (ExpandCollapsePattern)expansion;
                if (expand.Current.ExpandCollapseState == ExpandCollapseState.Collapsed) expand.Expand();
                else if (expand.Current.ExpandCollapseState == ExpandCollapseState.Expanded) expand.Collapse();
                return "Changed the expansion state.";
            case "scroll":
                Scroll(element, decision.Direction ?? "down");
                return $"Scrolled {decision.Direction ?? "down"}.";
            default:
                throw new InvalidOperationException($"Unsupported operation: {operation}.");
        }
    }

    private Snapshot SaveSnapshot(Snapshot snapshot)
    {
        _observedWindowPids.Clear();
        foreach (WindowInfo window in snapshot.Windows)
        {
            Native.GetWindowThreadProcessId(new IntPtr(window.Handle), out uint pid);
            _observedWindowPids[window.Handle] = pid;
        }
        return _lastSnapshot = snapshot;
    }

    private void LaunchObservedApp(AppInfo app, CancellationToken ct)
    {
        if (!_apps.TryGetValue(app.Id, out AppInfo? known) || known.LaunchId != app.LaunchId)
            throw new InvalidOperationException("This application is not in the local launch inventory.");
        var before = ListWindows();
        object? shellObject = null, folderObject = null, itemsObject = null;
        bool launched = false;
        try
        {
            shellObject = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            dynamic shell = shellObject!;
            folderObject = shell.NameSpace("shell:AppsFolder");
            dynamic folder = folderObject!;
            itemsObject = folder.Items();
            dynamic items = itemsObject;
            for (int i = 0; i < (int)items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                object? itemObject = null;
                try
                {
                    itemObject = items.Item(i);
                    dynamic item = itemObject;
                    if (!string.Equals((string)item.Path, app.LaunchId, StringComparison.OrdinalIgnoreCase)) continue;
                    item.InvokeVerb("open");
                    launched = true;
                    break;
                }
                finally { ReleaseCom(itemObject); }
            }
        }
        finally
        {
            ReleaseCom(itemsObject);
            ReleaseCom(folderObject);
            ReleaseCom(shellObject);
        }
        if (!launched) throw new InvalidOperationException("The installed application is no longer available.");
        long previousTarget = TargetHandle ?? 0;
        for (int attempt = 0; attempt < 14; attempt++)
        {
            if (ct.WaitHandle.WaitOne(250)) ct.ThrowIfCancellationRequested();
            var current = ListWindows();
            long foreground = Native.GetForegroundWindow().ToInt64();
            WindowInfo? candidate = current.FirstOrDefault(w => w.Handle == foreground &&
                (w.Handle != previousTarget || MatchesApp(w, app)));
            candidate ??= current.FirstOrDefault(w => !before.Any(old => old.Handle == w.Handle));
            candidate ??= current.FirstOrDefault(w => MatchesApp(w, app));
            if (candidate is null) continue;
            TargetHandle = candidate.Handle;
            return;
        }
        // The app may still be loading. The next snapshot exposes its windows for explicit selection.
        TargetHandle = null;
    }

    private static bool MatchesApp(WindowInfo window, AppInfo app) =>
        window.Title.Contains(app.Name, StringComparison.OrdinalIgnoreCase) ||
        app.Name.Contains(window.ProcessName, StringComparison.OrdinalIgnoreCase);

    private static CacheRequest MakeCache()
    {
        var cache = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.Full };
        foreach (AutomationProperty property in new[] {
            AutomationElement.NameProperty, AutomationElement.AutomationIdProperty,
            AutomationElement.ControlTypeProperty, AutomationElement.BoundingRectangleProperty,
            AutomationElement.IsEnabledProperty, AutomationElement.IsOffscreenProperty,
            AutomationElement.IsPasswordProperty, AutomationElement.HasKeyboardFocusProperty,
            AutomationElement.ProcessIdProperty, AutomationElement.RuntimeIdProperty,
            ValuePattern.ValueProperty, ValuePattern.IsReadOnlyProperty,
            TogglePattern.ToggleStateProperty, SelectionItemPattern.IsSelectedProperty,
            ExpandCollapsePattern.ExpandCollapseStateProperty,
            ScrollPattern.HorizontallyScrollableProperty, ScrollPattern.VerticallyScrollableProperty }) cache.Add(property);
        foreach (AutomationPattern pattern in new[] { InvokePattern.Pattern, ValuePattern.Pattern, SelectionItemPattern.Pattern,
            TogglePattern.Pattern, ExpandCollapsePattern.Pattern, ScrollPattern.Pattern, TextPattern.Pattern }) cache.Add(pattern);
        return cache;
    }

    private static List<string> GetActions(AutomationElement element, ControlType type, bool enabled, bool password)
    {
        var actions = new List<string>();
        if (!enabled) return actions;
        if (HasPattern(element, InvokePattern.Pattern)) actions.Add("invoke");
        else if (type == ControlType.Button || type == ControlType.Hyperlink || type == ControlType.MenuItem ||
            type == ControlType.SplitButton) actions.Add("invoke");
        // HTML checkboxes can expose a writable ValuePattern for their submitted value.
        // That is not a text-entry field and must not compete with their toggle action.
        bool textEntryRole = type == ControlType.Edit || type == ControlType.ComboBox ||
            type == ControlType.Document || type == ControlType.Custom || type == ControlType.Spinner;
        if (!password && textEntryRole && ((HasPattern(element, ValuePattern.Pattern) &&
            !Cached(element, ValuePattern.IsReadOnlyProperty, true)) ||
            (type == ControlType.Edit && !HasPattern(element, ValuePattern.Pattern)))) actions.Add("set_text");
        if (HasPattern(element, SelectionItemPattern.Pattern) || type == ControlType.TabItem || type == ControlType.ListItem ||
            type == ControlType.TreeItem || type == ControlType.RadioButton) actions.Add("select");
        if (HasPattern(element, TogglePattern.Pattern) || type == ControlType.CheckBox) actions.Add("toggle");
        if (HasPattern(element, ExpandCollapsePattern.Pattern) &&
            Cached(element, ExpandCollapsePattern.ExpandCollapseStateProperty, ExpandCollapseState.LeafNode) != ExpandCollapseState.LeafNode)
            actions.Add("expand");
        if (HasPattern(element, ScrollPattern.Pattern) &&
            (Cached(element, ScrollPattern.VerticallyScrollableProperty, false) || Cached(element, ScrollPattern.HorizontallyScrollableProperty, false)))
            actions.Add("scroll");
        return actions;
    }

    private static string ReadValue(AutomationElement element, ControlType type)
    {
        var parts = new List<string>();
        string value = Cached(element, ValuePattern.ValueProperty, "");
        if (!string.IsNullOrEmpty(value)) parts.Add(Clean(value, 400));
        if (HasPattern(element, TogglePattern.Pattern)) parts.Add("toggle=" + Cached(element, TogglePattern.ToggleStateProperty, ToggleState.Indeterminate));
        if (HasPattern(element, SelectionItemPattern.Pattern)) parts.Add("selected=" + Cached(element, SelectionItemPattern.IsSelectedProperty, false));
        if (HasPattern(element, ExpandCollapsePattern.Pattern)) parts.Add("expanded=" + Cached(element, ExpandCollapsePattern.ExpandCollapseStateProperty, ExpandCollapseState.LeafNode));
        if (parts.Count == 0 && type == ControlType.Edit && element.TryGetCachedPattern(TextPattern.Pattern, out object text))
        {
            try { parts.Add(Clean(((TextPattern)text).DocumentRange.GetText(400), 400)); }
            catch (InvalidOperationException) { }
            catch (ElementNotAvailableException) { }
        }
        return string.Join("; ", parts);
    }

    private static T Cached<T>(AutomationElement element, AutomationProperty property, T fallback)
    {
        object value = element.GetCachedPropertyValue(property, true);
        return value is T typed ? typed : fallback;
    }

    private static bool HasPattern(AutomationElement element, AutomationPattern pattern) => element.TryGetCachedPattern(pattern, out _);

    private static AutomationElement ResolveCurrent(ObservedControl observed)
    {
        AutomationElement element = observed.Element;
        if (!Automation.Compare(element.GetRuntimeId(), observed.RuntimeId) || element.Current.ProcessId != observed.ProcessId)
            throw new InvalidOperationException("The control changed since the last observation; observe again.");
        if (element.Current.ControlType != observed.Type || Clean(element.Current.Name, 300) != observed.Name)
            throw new InvalidOperationException("The control's label or type changed since the last observation; observe again.");
        if (!string.IsNullOrEmpty(observed.AutomationId) && element.Current.AutomationId != observed.AutomationId)
            throw new InvalidOperationException("The control's automation identifier changed; observe again.");
        return element;
    }

    private void RequireLiveWindow(WindowInfo window)
    {
        IntPtr handle = new(window.Handle);
        if (window.Handle == 0 || !Native.IsWindow(handle)) throw new InvalidOperationException("No live target window is selected.");
        Native.GetWindowThreadProcessId(handle, out uint pid);
        if (pid == _ownPid || !_observedWindowPids.TryGetValue(window.Handle, out uint observedPid) || pid != observedPid ||
            ProcessName((int)pid) != window.ProcessName)
            throw new InvalidOperationException("The target window changed; observe again.");
    }

    private static void FocusWindow(long windowHandle)
    {
        IntPtr handle = new(windowHandle);
        if (!Native.IsWindow(handle)) throw new InvalidOperationException("The target window is no longer available.");
        if (Native.IsIconic(handle)) Native.ShowWindowAsync(handle, 9);
        Native.SetForegroundWindow(handle);
        // Win32 activation is asynchronous across input queues.
        for (int i = 0; i < 10 && Native.GetForegroundWindow() != handle; i++) Thread.Sleep(30);
        if (Native.GetForegroundWindow() != handle)
            throw new InvalidOperationException("Windows did not activate the target. Click its window once and try again.");
    }

    private static void FocusElement(AutomationElement element, int expectedPid)
    {
        try { element.SetFocus(); }
        catch (InvalidOperationException) { ClickElement(element, expectedPid); }
        AutomationElement? focus = AutomationElement.FocusedElement;
        if (focus is null || !Automation.Compare(focus, element)) ClickElement(element, expectedPid);
    }

    private static void ClickElement(AutomationElement element, int expectedPid)
    {
        System.Windows.Point point;
        if (!element.TryGetClickablePoint(out point))
        {
            System.Windows.Rect bounds = element.Current.BoundingRectangle;
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) throw new InvalidOperationException("Control has no clickable area.");
            point = new System.Windows.Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        }
        var nativePoint = new Native.POINT((int)Math.Round(point.X), (int)Math.Round(point.Y));
        IntPtr underPoint = Native.WindowFromPoint(nativePoint);
        Native.GetWindowThreadProcessId(underPoint, out uint actualPid);
        if (actualPid != expectedPid) throw new InvalidOperationException("Another window covers the selected control.");
        if (!Native.SetCursorPos(nativePoint.X, nativePoint.Y)) throw new InvalidOperationException("Could not position the pointer.");
        SendInputs([
            new Native.INPUT { type = 0, data = new Native.InputUnion { mouse = new Native.MOUSEINPUT { dwFlags = 0x0002 } } },
            new Native.INPUT { type = 0, data = new Native.InputUnion { mouse = new Native.MOUSEINPUT { dwFlags = 0x0004 } } }
        ]);
    }

    private static void Scroll(AutomationElement element, string direction)
    {
        direction = direction.ToLowerInvariant();
        if (direction is not ("up" or "down" or "left" or "right")) throw new InvalidOperationException("Invalid scroll direction.");
        if (!element.TryGetCurrentPattern(ScrollPattern.Pattern, out object pattern))
            throw new InvalidOperationException("The scroll pattern is no longer available.");
        var scroll = (ScrollPattern)pattern;
        ScrollAmount horizontal = direction == "left" ? ScrollAmount.LargeDecrement : direction == "right" ? ScrollAmount.LargeIncrement : ScrollAmount.NoAmount;
        ScrollAmount vertical = direction == "up" ? ScrollAmount.LargeDecrement : direction == "down" ? ScrollAmount.LargeIncrement : ScrollAmount.NoAmount;
        scroll.Scroll(horizontal, vertical);
    }

    private static void SendNamedKey(string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var known = new Dictionary<string, (ushort[] Modifiers, ushort Key)>(StringComparer.OrdinalIgnoreCase)
        {
            ["Enter"] = ([], 0x0D), ["Escape"] = ([], 0x1B), ["Tab"] = ([], 0x09),
            ["Shift+Tab"] = ([0x10], 0x09), ["Up"] = ([], 0x26), ["Down"] = ([], 0x28),
            ["Left"] = ([], 0x25), ["Right"] = ([], 0x27), ["Home"] = ([], 0x24),
            ["End"] = ([], 0x23), ["PageUp"] = ([], 0x21), ["PageDown"] = ([], 0x22),
            ["Space"] = ([], 0x20), ["Backspace"] = ([], 0x08),
            ["Ctrl+L"] = ([Native.VK_CONTROL], 0x4C), ["Ctrl+F"] = ([Native.VK_CONTROL], 0x46),
            ["Ctrl+A"] = ([Native.VK_CONTROL], 0x41)
        };
        if (!known.TryGetValue(key, out var stroke)) throw new InvalidOperationException("This key is not allowed.");
        SendChord(stroke.Modifiers, stroke.Key);
    }

    private static void SendChord(ushort[] modifiers, ushort key)
    {
        var inputs = new List<Native.INPUT>();
        foreach (ushort modifier in modifiers) inputs.Add(KeyInput(modifier, 0, 0));
        inputs.Add(KeyInput(key, 0, 0));
        inputs.Add(KeyInput(key, 0, 2));
        foreach (ushort modifier in modifiers.Reverse()) inputs.Add(KeyInput(modifier, 0, 2));
        SendInputs(inputs.ToArray());
    }

    private static void SendUnicode(string value, long targetHandle, CancellationToken ct)
    {
        foreach (char character in value)
        {
            ct.ThrowIfCancellationRequested();
            if (Native.GetForegroundWindow().ToInt64() != targetHandle)
                throw new InvalidOperationException("Input stopped because the foreground window changed.");
            SendInputs([KeyInput(0, character, 4), KeyInput(0, character, 6)]);
        }
    }

    private static Native.INPUT KeyInput(ushort key, ushort scan, uint flags) =>
        new() { type = 1, data = new Native.InputUnion { keyboard = new Native.KEYBDINPUT { wVk = key, wScan = scan, dwFlags = flags } } };

    private static void SendInputs(Native.INPUT[] inputs)
    {
        if (Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>()) != inputs.Length)
            throw new InvalidOperationException("Windows rejected input injection. Elevated apps require matching elevation.");
    }

    private static ScreenRect ToRect(System.Windows.Rect r) => new((int)Math.Round(r.X), (int)Math.Round(r.Y),
        (int)Math.Round(r.Width), (int)Math.Round(r.Height));
    private static string Clean(string text, int max) => Trim(text.Replace('\0', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim(), max);
    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max] + "…";
    private static string WindowId(IntPtr window) => $"window{window.ToInt64():X}";
    private static string WindowTitle(IntPtr handle)
    {
        var builder = new StringBuilder(Math.Min(Native.GetWindowTextLength(handle) + 1, 2048));
        Native.GetWindowText(handle, builder, builder.Capacity);
        return builder.ToString();
    }
    private static string ProcessName(int pid)
    {
        try { using Process process = Process.GetProcessById(pid); return process.ProcessName; }
        catch (ArgumentException) { return "unknown"; }
        catch (InvalidOperationException) { return "unknown"; }
    }
    private static bool IsCloaked(IntPtr handle)
    {
        try { return Native.DwmGetWindowAttribute(handle, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0; }
        catch (DllNotFoundException) { return false; }
    }
    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    private static class Native
    {
        public const ushort VK_CONTROL = 0x11;
        public delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr window, StringBuilder title, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLength(IntPtr window);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool ShowWindowAsync(IntPtr window, int command);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT point);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
        [StructLayout(LayoutKind.Sequential)] public readonly struct POINT(int x, int y) { public readonly int X = x; public readonly int Y = y; }
        [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public InputUnion data; }
        [StructLayout(LayoutKind.Explicit)] public struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mouse; [FieldOffset(0)] public KEYBDINPUT keyboard; }
        [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public UIntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public UIntPtr dwExtraInfo; }
    }
}
