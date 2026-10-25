using System.Collections.Generic;
using System.Text;

namespace OthLan;

/// <summary>
/// 0.2.0 T6.5: edge rules for session toasts. Thresholds stay in EchoTracker / FreezeLogic;
/// this only decides rising/falling edges for the UI orchestrator.
/// </summary>
public static class SessionNotifyLogic
{
    public const string LineSep = " · ";

    public static bool BecameNoLink(bool wasTimedOut, bool timedOut) => !wasTimedOut && timedOut;
    public static bool BecameLinked(bool wasTimedOut, bool timedOut) => wasTimedOut && !timedOut;
    /// <summary>Client had a running LAN runner and it disappeared (host quit / kicked to menu).</summary>
    public static bool HostSessionEnded(bool wasClientSession, bool hadRunner, bool hasRunner) =>
        wasClientSession && hadRunner && !hasRunner;
    /// <summary>Slot became valid this tick (join).</summary>
    public static bool BecamePresent(bool wasPresent, bool present) => !wasPresent && present;
    /// <summary>Slot stopped being valid this tick (leave), when not already reported via SlotAllocator release.</summary>
    public static bool BecameAbsent(bool wasPresent, bool present) => wasPresent && !present;

    /// <summary>One JoinToast line from several events in the same frame (JoinToast keeps a single string).</summary>
    public static string JoinLines(IReadOnlyList<string> lines)
    {
        if (lines == null || lines.Count == 0) return "";
        if (lines.Count == 1) return lines[0] ?? "";
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            var s = lines[i];
            if (string.IsNullOrEmpty(s)) continue;
            if (sb.Length > 0) sb.Append(LineSep);
            sb.Append(s);
        }
        return sb.ToString();
    }
}
