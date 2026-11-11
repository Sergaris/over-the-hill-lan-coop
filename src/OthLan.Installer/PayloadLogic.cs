using System;
using System.Collections.Generic;

namespace OthLan.Installer;

/// <summary>
/// Pure helpers for installer payload obtain: sha256 compare and candidate fallback order.
/// No I/O — used by <see cref="Payload"/> and LogicTests.
/// </summary>
public static class PayloadLogic
{
    public enum SourceKind { Embedded, Local, Download }

    /// <summary>Preferred order: embedded → file next to exe → download.</summary>
    public static readonly SourceKind[] DefaultOrder =
        { SourceKind.Embedded, SourceKind.Local, SourceKind.Download };

    public static bool HashEquals(string actual, string expected)
    {
        if (string.IsNullOrEmpty(actual) || string.IsNullOrEmpty(expected)) return false;
        return actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Walk candidates in order. Each entry is (label, sha256 of bytes) or sha=null if that source is absent.
    /// Returns the label of the first hash match, or null if none matched. <paramref name="rejected"/> lists
    /// labels that were present but failed the hash (for logging / fallback).
    /// </summary>
    public static string PickFirstMatching(
        string expectedSha,
        IReadOnlyList<(string label, string sha)> candidates,
        out string[] rejected)
    {
        var bad = new List<string>();
        if (candidates != null)
        {
            for (var i = 0; i < candidates.Count; i++)
            {
                var (label, sha) = candidates[i];
                if (sha == null) continue;
                if (HashEquals(sha, expectedSha))
                {
                    rejected = bad.ToArray();
                    return label;
                }
                bad.Add(label);
            }
        }
        rejected = bad.ToArray();
        return null;
    }

    public static string MismatchMessage(string fileName, string sourceLabel, string actual, string expected) =>
        $"{fileName}: {sourceLabel} sha256 {actual} != expected {expected}";
}
