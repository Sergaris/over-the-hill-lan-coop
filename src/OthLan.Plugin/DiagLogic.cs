using System;

namespace OthLan;

/// <summary>
/// 0.2.0 T0.3: when Diagnostics may retry reading Fusion runner config.
/// Pure logic (unit-tested). IO/logging stays in <c>Diagnostics</c>.
/// </summary>
public static class DiagLogic
{
    public const int MaxRuntimeConfigRetries = 20;

    /// <summary>Fusion throws before RuntimeConfig is ready; message may not be InvalidOperationException on IL2CPP.</summary>
    public static bool IsRuntimeConfigNotReady(Exception e)
    {
        if (e == null) return false;
        var m = e.Message;
        return !string.IsNullOrEmpty(m)
               && m.IndexOf("RuntimeConfig", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>Retry while under the cap and the failure is the early-RuntimeConfig case.</summary>
    public static bool ShouldRetryRuntimeConfig(Exception e, int retriesSoFar, int maxRetries = MaxRuntimeConfigRetries)
        => retriesSoFar < maxRetries && IsRuntimeConfigNotReady(e);
}
