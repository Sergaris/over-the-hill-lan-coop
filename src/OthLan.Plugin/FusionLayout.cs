using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;

namespace OthLan;

/// <summary>
/// Struct field offsets used by the native detours, resolved by NAME from the running game's IL2CPP metadata
/// (il2cpp_class_get_field_from_name / il2cpp_field_get_offset) instead of hard-coding them. For a value type,
/// il2cpp_field_get_offset is relative to the boxed object, so the object header (0x10) is subtracted.
/// Nullable&lt;T&gt; members are resolved on the inflated generic class of each field's own type.
/// The resolved values are what the detours use; the gate also compares them to the manifest.
/// </summary>
internal static unsafe class FusionLayout
{
    const int ObjHeader = 0x10;
    public static bool Ready;
    public static readonly Dictionary<string, int> All = new();

    // StartGameArgs
    public static int SgaGameMode, SgaConnectionToken, SgaPlayerUniqueId, SgaSessionProperties;
    // NetworkRunnerInitializeArgs (each Nullable<T>: field offset + hasValue/value inside it)
    public static int IaAddress, IaAddressHas, IaAddressVal;
    public static int IaPlayerCount, IaPlayerCountHas, IaPlayerCountVal;
    public static int IaSimMode, IaSimModeHas, IaSimModeVal;
    public static int IaConfig;
    public static int NetAddressSize;

    public static void Resolve()
    {
        All.Clear(); Ready = false;
        var sga = Class("Fusion.Runtime.dll", "Fusion", "StartGameArgs");
        var ia = Class("Fusion.Runtime.dll", "Fusion", "NetworkRunnerInitializeArgs");
        SgaGameMode = Field(sga, "GameMode", "StartGameArgs.GameMode");
        SgaConnectionToken = Field(sga, "ConnectionToken", "StartGameArgs.ConnectionToken");
        SgaPlayerUniqueId = Field(sga, "PlayerUniqueId", "StartGameArgs.PlayerUniqueId");
        SgaSessionProperties = Field(sga, "SessionProperties", "StartGameArgs.SessionProperties");
        IaAddress = Field(ia, "Address", "NetworkRunnerInitializeArgs.Address");
        (IaAddressHas, IaAddressVal) = NullableMembers(ia, "Address", "Nullable<NetAddress>");
        IaPlayerCount = Field(ia, "PlayerCount", "NetworkRunnerInitializeArgs.PlayerCount");
        (IaPlayerCountHas, IaPlayerCountVal) = NullableMembers(ia, "PlayerCount", "Nullable<Int32>");
        IaSimMode = Field(ia, "SimulationMode", "NetworkRunnerInitializeArgs.SimulationMode");
        (IaSimModeHas, IaSimModeVal) = NullableMembers(ia, "SimulationMode", "Nullable<SimulationModes>");
        IaConfig = Field(ia, "Config", "NetworkRunnerInitializeArgs.Config");
        var na = Class("Fusion.Sockets.dll", "Fusion.Sockets", "NetAddress");
        uint align = 0;
        NetAddressSize = (int)IL2CPP.il2cpp_class_value_size(na, ref align);
        All["sizeof(NetAddress)"] = NetAddressSize;
        if (NetAddressSize != System.Runtime.CompilerServices.Unsafe.SizeOf<Fusion.Sockets.NetAddress>())
            throw new InvalidOperationException($"NetAddress is {NetAddressSize} bytes in the game but {System.Runtime.CompilerServices.Unsafe.SizeOf<Fusion.Sockets.NetAddress>()} in the interop");
        Ready = true;
    }

    static IntPtr Class(string asm, string ns, string name)
    {
        var c = IL2CPP.GetIl2CppClass(asm, ns, name);
        if (c == IntPtr.Zero) throw new MissingMemberException($"IL2CPP class {ns}.{name} not found in {asm}");
        return c;
    }

    static int Offset(IntPtr klass, string field, out IntPtr f)
    {
        f = IL2CPP.il2cpp_class_get_field_from_name(klass, field);
        if (f == IntPtr.Zero) throw new MissingFieldException($"IL2CPP field {field} not found");
        var off = (int)IL2CPP.il2cpp_field_get_offset(f);
        return IL2CPP.il2cpp_class_is_valuetype(klass) ? off - ObjHeader : off;
    }

    static int Field(IntPtr klass, string field, string key) { var o = Offset(klass, field, out _); All[key] = o; return o; }

    static (int has, int val) NullableMembers(IntPtr owner, string field, string key)
    {
        Offset(owner, field, out var f);
        var type = IL2CPP.il2cpp_field_get_type(f);
        var nc = IL2CPP.il2cpp_class_from_type(type);
        if (nc == IntPtr.Zero) throw new MissingMemberException($"{key}: no class for the field type");
        int has;
        try { has = Offset(nc, "hasValue", out _); } catch (MissingFieldException) { has = Offset(nc, "has_value", out _); }
        var val = Offset(nc, "value", out _);
        All[key + ".hasValue"] = has; All[key + ".value"] = val;
        return (has, val);
    }
}
