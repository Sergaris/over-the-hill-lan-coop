namespace OthLan;

/// <summary>
/// 0.2.0 T1.3: single modal slot for Canvas dialogs (mod gate, update prompt, …).
/// </summary>
internal static class UiModal
{
    static string _current;
    public static string Current => _current;

    public static bool TryOpen(string owner) => UiModalLogic.TryOpen(owner, ref _current);
    public static void Close(string owner) => UiModalLogic.Close(owner, ref _current);
}
