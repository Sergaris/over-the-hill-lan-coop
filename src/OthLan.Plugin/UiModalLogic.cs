namespace OthLan;

/// <summary>
/// 0.2.0 T1.3: at most one modal owner. Pure logic (unit-tested). Unity wiring in <c>UiModal</c>.
/// </summary>
public static class UiModalLogic
{
    /// <summary>Try to claim the modal. Fails if another owner holds it.</summary>
    public static bool TryOpen(string owner, ref string current)
    {
        if (string.IsNullOrEmpty(owner)) return false;
        if (current != null && current != owner) return false;
        current = owner;
        return true;
    }

    public static void Close(string owner, ref string current)
    {
        if (current == owner) current = null;
    }
}
