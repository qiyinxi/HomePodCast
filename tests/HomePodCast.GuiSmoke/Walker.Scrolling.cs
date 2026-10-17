namespace HomePodCast.GuiSmoke;

internal sealed partial class Walker
{
    /// <summary>Advance with real line commands and observe the position. A synthesized thumb position does not set
    /// WinForms' nTrackPos, so it can leave the page at the top despite a nonzero high word (2026-10-17 CI).</summary>
    private static bool ScrollViewportForward(IntPtr pane, int pixels)
    {
        if (Native.VerticalScroll(pane) is not { } before) return false;
        int goal = Math.Min(before.nMax - before.nPage + 1, before.nPos + Math.Max(1, pixels));
        int position = before.nPos;
        for (int line = 0; line < 256 && position < goal; line++)
        {
            Native.SendMessageTimeout(pane, Native.WM_VSCROLL, new IntPtr(Native.SB_LINEDOWN), IntPtr.Zero,
                0x2 /* SMTO_ABORTIFHUNG */, 5000, out _);
            if (Native.VerticalScroll(pane) is not { } after || after.nPos <= position) break;
            position = after.nPos;
        }
        if (position == before.nPos) return false;
        Thread.Sleep(350);
        return true;
    }
}
