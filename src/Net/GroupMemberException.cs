namespace HomePodCast.Net;

/// <summary>A member of a group failed; Member says which one.</summary>
internal sealed class GroupMemberException(string member, Exception inner)
    : Exception($"{member}: {inner.Message}", inner)
{
    public string Member { get; } = member;

    /// <summary>
    /// What the group asked of the member when it failed (RECORD, FLUSH, volume, prepare). A speaker that closes the
    /// connection gives only "connection closed by speaker", which does not say which request it refused
    /// (2026-11-07: a stereo pair did it ten times in a row, and the log could not tell RECORD from FLUSH).
    /// </summary>
    public string? Step { get; init; }
}
