namespace OthLan;

/// <summary>
/// After <c>RPC_DetachWinchAndDisableWinchPoints</c> the networked <c>WinchPointsDisabled</c> stays true.
/// <c>EnableWinchPoints</c> only changes the local collider layer (11 ↔ 7); peers keep layer 7 until the flag
/// clears and OnChanged runs <c>EnableWinchPoints(true)</c>. Host must clear the networked flag when the rope
/// is gone — local re-enable alone fixes host→client but not client↔client (0.2.5.2 / 0.2.5.4).
/// Pure logic (unit-tested); Unity side lives in <c>WinchRelease</c>.
/// </summary>
public static class WinchReleaseLogic
{
    /// <summary>Host: clear stuck disabled points when the car has no rope.</summary>
    public static bool ShouldClearDisabled(bool pointsDisabled, bool winchAttached)
        => pointsDisabled && !winchAttached;
}
