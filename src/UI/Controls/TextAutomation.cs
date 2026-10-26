using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Provider;

namespace HomePodCast.UI.Controls;

/// <summary>
/// The host's UIA fragment contains its painted texts. Windows merges its ordinary child HWND providers into it.
/// MSAA alone is insufficient: 2026-10-18, Windows read the custom accChildCount but omitted the text children from UIA.
/// The standard COM marshaler keeps callbacks on the UI thread that owns the layout and these provider objects.
/// </summary>
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class TextAutomation(LayoutPanel host) : StandardOleMarshalObject, IRawElementProviderFragmentRoot, IRawElementProviderHwndOverride
{
    private LayoutPanel Host => host;
    private readonly ConditionalWeakTable<Control, IRawElementProviderFragment> _providers = new();
    private readonly IntPtr _hwnd = host.Handle;
    private bool _released, _disconnecting;
    private Control[] Children(Control parent) => LogicalLayout.Children(parent)
        .Where(c => (ParentPainting.IsPainted(c) || LogicalLayout.IsHost(host) && c.IsHandleCreated)
            && LayoutPanel.IsShown(c) && !LayoutPanel.IsCollapsed(c)).ToArray();
    private Control[] Texts => Children(host);
    private bool Contains(Control element)
    {
        if (element != host && LogicalLayout.Host(element) == host) return true;
        for (var c = element; c != null && ParentPainting.IsPainted(c); c = LogicalLayout.Parent(c))
            if (LogicalLayout.Parent(c) == host) return true;
        return false;
    }
    private IRawElementProviderFragment Provider(Control text) => _providers.GetValue(text,
        t => LogicalLayout.IsInput(t) ? new NativePlacement(this, t) : new TextProvider(this, t));
    private void CheckAvailable()
    {
        if (_released || host.IsDisposed || !host.IsHandleCreated)
            throw new ElementNotAvailableException();
    }
    internal void Disconnect()
    {
        if (_released) return;
        _disconnecting = true;
        try
        {
            // Clear the HWND's raised-event map as well as its remote providers. Do not return this provider from
            // a re-entrant WM_GETOBJECT, but keep its HWND identity usable while UIA disconnects it.
            UiaReturnRawElementProvider(_hwnd, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            int result = UiaDisconnectProvider(this);
            if (result < 0) Log.Warn($"UIA: disconnect {_hwnd:X} returned 0x{result:X8}");
        }
        finally { _released = true; _disconnecting = false; }
    }

    internal void Notify(Control text, AccessibleEvents change)
    {
        if (_released || !AutomationInteropProvider.ClientsAreListening) return;
        if (change == AccessibleEvents.Reorder)
        {
            AutomationInteropProvider.RaiseStructureChangedEvent(this,
                new StructureChangedEventArgs(StructureChangeType.ChildrenInvalidated, []));
            return;
        }
        if (text.IsDisposed || !Contains(text)) return;
        var provider = Provider(text);
        if (change == AccessibleEvents.NameChange)
        {
            AutomationInteropProvider.RaiseAutomationPropertyChangedEvent(provider,
                new AutomationPropertyChangedEventArgs(AutomationElementIdentifiers.NameProperty, null, text.AccessibilityObject.Name ?? ""));
            AutomationInteropProvider.RaiseAutomationPropertyChangedEvent(provider,
                new AutomationPropertyChangedEventArgs(ValuePatternIdentifiers.ValueProperty, null, text.AccessibilityObject.Value ?? ""));
        }
        else if (change == AccessibleEvents.StateChange)
            AutomationInteropProvider.RaiseAutomationPropertyChangedEvent(provider,
                new AutomationPropertyChangedEventArgs(AutomationElementIdentifiers.IsEnabledProperty, null, text.Enabled));
        else if (change == AccessibleEvents.LocationChange)
        {
            var bounds = provider.BoundingRectangle;
            // UIA's property-change ABI takes a VARIANT SAFEARRAY, not WindowsBase's managed Rect record.
            AutomationInteropProvider.RaiseAutomationPropertyChangedEvent(provider,
                new AutomationPropertyChangedEventArgs(AutomationElementIdentifiers.BoundingRectangleProperty, null,
                    new double[] { bounds.X, bounds.Y, bounds.Width, bounds.Height }));
        }
    }

    [DllImport("UIAutomationCore.dll")]
    private static extern int UiaDisconnectProvider([MarshalAs(UnmanagedType.Interface)] IRawElementProviderSimple provider);
    [DllImport("UIAutomationCore.dll")]
    private static extern IntPtr UiaReturnRawElementProvider(IntPtr hwnd, IntPtr wParam, IntPtr lParam, IntPtr provider);

    public ProviderOptions ProviderOptions => ProviderOptions.ServerSideProvider;
    public IRawElementProviderSimple HostRawElementProvider
    {
        get { if (!_disconnecting) CheckAvailable(); return AutomationInteropProvider.HostProviderFromHandle(_hwnd); }
    }
    public object? GetPatternProvider(int patternId) => null;
    public object? GetPropertyValue(int propertyId)
    {
        CheckAvailable();
        return propertyId switch
        {
            30003 => host.AccessibleRole switch
            {
                AccessibleRole.List => ControlType.List.Id,
                AccessibleRole.PageTabList => ControlType.Tab.Id,
                AccessibleRole.Grouping => ControlType.Group.Id,
                _ => ControlType.Pane.Id,
            },
            30005 => host.AccessibleName ?? host.Text,
            30009 => false, // IsKeyboardFocusable: text hosts never take focus
            30016 => true, // IsControlElement
            30017 => false, // IsContentElement: the texts carry the content
            _ => null, // Windows supplies the host HWND's other properties
        };
    }
    public IRawElementProviderFragment? Navigate(NavigateDirection direction)
    {
        CheckAvailable();
        var texts = Texts;
        return texts.Length == 0 ? null : direction switch
        {
            NavigateDirection.FirstChild => Provider(texts[0]),
            NavigateDirection.LastChild => Provider(texts[^1]),
            _ => null, // the HWND host supplies parent/sibling navigation
        };
    }
    public int[]? GetRuntimeId() => null; // the host HWND identifies the fragment root
    public System.Windows.Rect BoundingRectangle { get { CheckAvailable(); return ToRect(host.AccessibilityObject.Bounds); } }
    public IRawElementProviderSimple[]? GetEmbeddedFragmentRoots() => null;
    public void SetFocus() { }
    public IRawElementProviderFragmentRoot FragmentRoot => this;
    public IRawElementProviderFragment? ElementProviderFromPoint(double x, double y)
    {
        CheckAvailable();
        Control? Hit(Control parent)
        {
            foreach (var child in Children(parent))
                if (child.AccessibilityObject.HitTest((int)x, (int)y) != null) return Hit(child) ?? child;
            return null;
        }
        return Hit(host) is { } element ? Provider(element) : null;
    }
    public IRawElementProviderFragment? GetFocus() => null; // ordinary child HWNDs keep their focus providers
    public IRawElementProviderSimple? GetOverrideProviderForHwnd(IntPtr hwnd)
    {
        CheckAvailable();
        return Control.FromHandle(hwnd) is { } control && LogicalLayout.IsInput(control) && LogicalLayout.Host(control) == host
            ? Provider(control) : null;
    }
    private static System.Windows.Rect ToRect(Rectangle bounds) => new(bounds.X, bounds.Y, bounds.Width, bounds.Height);

    /// <summary>
    /// UIA's HWND placement override: supply only logical navigation. The HWND retains its WinForms provider, patterns,
    /// properties, children and runtime identity. This is the same interop mechanism WPF uses for a hosted native control.
    /// </summary>
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class NativePlacement(TextAutomation root, Control control) : StandardOleMarshalObject, IRawElementProviderFragmentRoot
    {
        private void CheckAvailable()
        {
            root.CheckAvailable();
            if (control.IsDisposed || !control.IsHandleCreated || !root.Contains(control)) throw new ElementNotAvailableException();
        }
        public ProviderOptions ProviderOptions => ProviderOptions.ServerSideProvider | ProviderOptions.OverrideProvider;
        public IRawElementProviderSimple HostRawElementProvider
        {
            get { CheckAvailable(); return AutomationInteropProvider.HostProviderFromHandle(control.Handle); }
        }
        public object? GetPatternProvider(int patternId) => null;
        public object? GetPropertyValue(int propertyId) => null;
        public IRawElementProviderFragment? Navigate(NavigateDirection direction)
        {
            CheckAvailable();
            var parent = LogicalLayout.Parent(control)!;
            if (direction == NavigateDirection.Parent) return parent == root.Host ? root : root.Provider(parent);
            var siblings = root.Children(parent);
            int index = Array.IndexOf(siblings, control);
            int next = direction == NavigateDirection.NextSibling ? index + 1 : direction == NavigateDirection.PreviousSibling ? index - 1 : -1;
            return index >= 0 && next >= 0 && next < siblings.Length ? root.Provider(siblings[next]) : null;
        }
        public int[]? GetRuntimeId() => null;
        public System.Windows.Rect BoundingRectangle => System.Windows.Rect.Empty;
        public IRawElementProviderSimple[]? GetEmbeddedFragmentRoots() => null;
        public void SetFocus() => throw new NotSupportedException();
        public IRawElementProviderFragmentRoot FragmentRoot => null!; // the input's own provider supplies its fragment
        public IRawElementProviderFragment? ElementProviderFromPoint(double x, double y) => null;
        public IRawElementProviderFragment? GetFocus() => null;
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class TextProvider(TextAutomation root, Control text) : StandardOleMarshalObject, IRawElementProviderFragment, IValueProvider
    {
        private static int _nextId;
        private readonly int _id = Interlocked.Increment(ref _nextId);
        private void CheckAvailable()
        {
            root.CheckAvailable();
            if (text.IsDisposed || !root.Contains(text))
                throw new ElementNotAvailableException();
        }
        public ProviderOptions ProviderOptions => ProviderOptions.ServerSideProvider;
        public IRawElementProviderSimple? HostRawElementProvider => null;
        public object? GetPatternProvider(int patternId)
        {
            CheckAvailable();
            return text is not LayoutPanel && patternId == ValuePatternIdentifiers.Pattern.Id ? this : null;
        }
        public object? GetPropertyValue(int propertyId)
        {
            CheckAvailable();
            return propertyId switch
            {
                30003 => text.AccessibilityObject.Role switch
                {
                    AccessibleRole.StaticText => ControlType.Text.Id,
                    AccessibleRole.ProgressBar => ControlType.ProgressBar.Id,
                    AccessibleRole.Graphic => ControlType.Image.Id,
                    AccessibleRole.Grouping => ControlType.Group.Id,
                    _ => ControlType.Pane.Id,
                },
                30005 => text.AccessibilityObject.Name ?? "",
                30008 or 30009 => false, // focused/focusable
                30010 => text.Enabled,
                30011 => text.Name,
                30013 => text.AccessibleDescription ?? "",
                30016 => true,
                30017 => text is not LayoutPanel,
                30020 => 0, // no native window
                30022 => text.AccessibilityObject.State.HasFlag(AccessibleStates.Offscreen),
                30012 => text.GetType().Name,
                30024 => "HomePodCast",
                _ => null,
            };
        }
        public IRawElementProviderFragment? Navigate(NavigateDirection direction)
        {
            CheckAvailable();
            if (direction == NavigateDirection.Parent)
                return LogicalLayout.Parent(text) == root.Host ? root : root.Provider(LogicalLayout.Parent(text)!);
            var children = root.Children(text);
            if (direction == NavigateDirection.FirstChild) return children.Length == 0 ? null : root.Provider(children[0]);
            if (direction == NavigateDirection.LastChild) return children.Length == 0 ? null : root.Provider(children[^1]);
            var siblings = root.Children(LogicalLayout.Parent(text)!);
            int i = Array.IndexOf(siblings, text);
            int next = direction == NavigateDirection.NextSibling ? i + 1 : direction == NavigateDirection.PreviousSibling ? i - 1 : -1;
            return i >= 0 && next >= 0 && next < siblings.Length ? root.Provider(siblings[next]) : null;
        }
        public int[] GetRuntimeId() => [AutomationInteropProvider.AppendRuntimeId, _id];
        public System.Windows.Rect BoundingRectangle { get { CheckAvailable(); return ToRect(text.AccessibilityObject.Bounds); } }
        public IRawElementProviderSimple[]? GetEmbeddedFragmentRoots() => null;
        public void SetFocus() { }
        public IRawElementProviderFragmentRoot FragmentRoot => root;
        public bool IsReadOnly => true;
        public string Value { get { CheckAvailable(); return text.AccessibilityObject.Value ?? ""; } }
        public void SetValue(string value) => throw new InvalidOperationException("Display text is read-only.");
    }
}
