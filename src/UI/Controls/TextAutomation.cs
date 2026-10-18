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
internal sealed class TextAutomation(LayoutPanel host) : StandardOleMarshalObject, IRawElementProviderFragmentRoot
{
    private LayoutPanel Host => host;
    private readonly ConditionalWeakTable<TextBlock, TextProvider> _providers = new();
    private bool _released;
    private TextBlock[] Texts => host.Controls.OfType<TextBlock>().Where(t => t.ParentPainted && t.Visible && !t.Collapsed).ToArray();
    private TextProvider Provider(TextBlock text) => _providers.GetValue(text, t => new TextProvider(this, t));
    private void CheckAvailable()
    {
        if (_released || host.IsDisposed || !host.IsHandleCreated) throw new ElementNotAvailableException();
    }
    internal void Disconnect()
    {
        _released = true;
        UiaDisconnectProvider(this);
    }

    internal void Notify(TextBlock text, AccessibleEvents change)
    {
        if (_released || !AutomationInteropProvider.ClientsAreListening) return;
        if (change == AccessibleEvents.Reorder)
        {
            AutomationInteropProvider.RaiseStructureChangedEvent(this,
                new StructureChangedEventArgs(StructureChangeType.ChildrenInvalidated, []));
            return;
        }
        if (text.IsDisposed || text.Parent != host) return;
        var provider = Provider(text);
        if (change == AccessibleEvents.NameChange)
        {
            AutomationInteropProvider.RaiseAutomationPropertyChangedEvent(provider,
                new AutomationPropertyChangedEventArgs(AutomationElementIdentifiers.NameProperty, null, text.AccessibleName ?? text.Text));
            AutomationInteropProvider.RaiseAutomationPropertyChangedEvent(provider,
                new AutomationPropertyChangedEventArgs(ValuePatternIdentifiers.ValueProperty, null, text.Text));
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

    public ProviderOptions ProviderOptions => ProviderOptions.ServerSideProvider;
    public IRawElementProviderSimple HostRawElementProvider
    {
        get { CheckAvailable(); return AutomationInteropProvider.HostProviderFromHandle(host.Handle); }
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
        return Texts.FirstOrDefault(t => t.AccessibilityObject.HitTest((int)x, (int)y) != null) is { } text ? Provider(text) : null;
    }
    public IRawElementProviderFragment? GetFocus() => null; // ordinary child HWNDs keep their focus providers
    private static System.Windows.Rect ToRect(Rectangle bounds) => new(bounds.X, bounds.Y, bounds.Width, bounds.Height);

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class TextProvider(TextAutomation root, TextBlock text) : StandardOleMarshalObject, IRawElementProviderFragment, IValueProvider
    {
        private static int _nextId;
        private readonly int _id = Interlocked.Increment(ref _nextId);
        private void CheckAvailable()
        {
            root.CheckAvailable();
            if (text.IsDisposed || text.Parent != root.Host) throw new ElementNotAvailableException();
        }
        public ProviderOptions ProviderOptions => ProviderOptions.ServerSideProvider;
        public IRawElementProviderSimple? HostRawElementProvider => null;
        public object? GetPatternProvider(int patternId)
        {
            CheckAvailable();
            return patternId == ValuePatternIdentifiers.Pattern.Id ? this : null;
        }
        public object? GetPropertyValue(int propertyId)
        {
            CheckAvailable();
            return propertyId switch
            {
                30003 => ControlType.Text.Id,
                30005 => text.AccessibleName ?? text.Text,
                30008 or 30009 => false, // focused/focusable
                30010 => text.Enabled,
                30011 => text.Name,
                30013 => text.AccessibleDescription ?? "",
                30016 or 30017 => true,
                30020 => 0, // no native window
                30022 => text.AccessibilityObject.State.HasFlag(AccessibleStates.Offscreen),
                30024 => "HomePodCast",
                _ => null,
            };
        }
        public IRawElementProviderFragment? Navigate(NavigateDirection direction)
        {
            CheckAvailable();
            if (direction == NavigateDirection.Parent) return root;
            var texts = root.Texts;
            int i = Array.IndexOf(texts, text);
            int next = direction == NavigateDirection.NextSibling ? i + 1 : direction == NavigateDirection.PreviousSibling ? i - 1 : -1;
            return i >= 0 && next >= 0 && next < texts.Length ? root.Provider(texts[next]) : null;
        }
        public int[] GetRuntimeId() => [AutomationInteropProvider.AppendRuntimeId, _id];
        public System.Windows.Rect BoundingRectangle { get { CheckAvailable(); return ToRect(text.AccessibilityObject.Bounds); } }
        public IRawElementProviderSimple[]? GetEmbeddedFragmentRoots() => null;
        public void SetFocus() { }
        public IRawElementProviderFragmentRoot FragmentRoot => root;
        public bool IsReadOnly => true;
        public string Value { get { CheckAvailable(); return text.Text; } }
        public void SetValue(string value) => throw new InvalidOperationException("Display text is read-only.");
    }
}
