using System;
using System.Security.Cryptography;
using Iced.Intel;

namespace OthLan;

/// <summary>
/// Address-independent fingerprint of a native function (x64). Used when the game files do not match a known build:
/// a game patch moves every function (all RVAs shift), but if Fusion itself did not change, the machine code of the
/// hooked Fusion methods is identical except for embedded addresses. Each instruction is decoded with Iced (shipped
/// with BepInEx); RIP-relative displacements and near branch/call targets are zeroed; everything else, including the
/// struct field offsets the code uses, stays in the hash. Pure: shared by the plugin and tests/SigTool.
/// </summary>
public static class CodeSig
{
    /// <summary>Upper bound on bytes hashed (a function longer than this is hashed up to here).</summary>
    public const int MaxBytes = 0x4000;
    /// <summary>Bytes hashed when the function has no unwind entry (leaf function): stop at the first ret.</summary>
    public const int LeafMaxBytes = 0x80;

    /// <summary>Returns "len:sha256-prefix". If <paramref name="leaf"/>, decoding stops after the first ret/jmp.</summary>
    public static string Compute(byte[] code, int length, bool leaf)
    {
        length = Math.Min(Math.Min(length, code.Length), leaf ? LeafMaxBytes : MaxBytes);
        var norm = new byte[length];
        Array.Copy(code, norm, length);
        var reader = new ByteArrayCodeReader(code, 0, length);
        var dec = Decoder.Create(64, reader, 0x10000000UL);
        var end = length;
        while (reader.CanReadByte)
        {
            var start = (int)(dec.IP - 0x10000000UL);
            dec.Decode(out var ins);
            if (ins.IsInvalid) break;
            var co = dec.GetConstantOffsets(ins);
            if (ins.IsIPRelativeMemoryOperand && co.HasDisplacement)
                Array.Clear(norm, start + co.DisplacementOffset, co.DisplacementSize);
            if (IsNearBranch(ins) && co.HasImmediate)
                Array.Clear(norm, start + co.ImmediateOffset, co.ImmediateSize);
            if (leaf && (ins.FlowControl == FlowControl.Return || ins.FlowControl == FlowControl.UnconditionalBranch))
            { end = start + ins.Length; break; }
        }
        using var sha = SHA256.Create();
        var h = sha.ComputeHash(norm, 0, end);
        return $"{end}:{Convert.ToHexString(h, 0, 8).ToLowerInvariant()}";
    }

    static bool IsNearBranch(in Instruction i)
    {
        for (int k = 0; k < i.OpCount; k++)
        {
            var kind = i.GetOpKind(k);
            if (kind == OpKind.NearBranch16 || kind == OpKind.NearBranch32 || kind == OpKind.NearBranch64) return true;
        }
        return false;
    }
}
