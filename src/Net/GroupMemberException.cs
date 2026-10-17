namespace HomePodCast.Net;

/// <summary>A member of a group failed; Member says which one.</summary>
internal sealed class GroupMemberException(string member, Exception inner)
    : Exception($"{member}: {inner.Message}", inner)
{
    public string Member { get; } = member;
}
