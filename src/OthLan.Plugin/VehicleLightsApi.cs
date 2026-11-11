using System;
using System.Text;
using Il2CppInterop.Runtime;
using Il2CppSystem.Reflection;
using UnityEngine;

namespace OthLan;

/// <summary>
/// 0.2.6.2: apply / read vehicle headlights across Lights API generations.
/// Build ≤26136884: LightsController.AreHeadlightsOn set(bool).
/// Build 26326013+: set_AreHeadlightsOn gone; CurrentHeadlightsMode (Off/Low/High) + getter AreHeadlightsOn.
/// Interop stubs may be from an older dump — resolve methods on the live Il2Cpp type and log real enum names/values once.
/// </summary>
internal static class VehicleLightsApi
{
    enum Path : byte { Unknown, ModeEnum, BoolSetter, Dead }

    static Path _lightsPath;
    static Path _inputPath;
    static bool _lightsLogged, _inputLogged, _enumLogged, _netLogged;
    static MethodInfo _setMode, _getMode, _setInputMode, _getInputMode, _toggleOld, _getNetMode, _lightsPropSet;
    static bool _applyFailLogged;
    static Il2CppSystem.Type _modeEnumType;
    /// <summary>Game int → our mode; filled from live Enum.GetNames (defaults Off=0,Low=1,High=2).</summary>
    static readonly VehicleLightLogic.HeadlightMode[] IntToMode = new VehicleLightLogic.HeadlightMode[8];
    static readonly int[] ModeToInt = new int[3] { 0, 1, 2 };
    static bool _mapReady;

    static VehicleLightsApi()
    {
        IntToMode[0] = VehicleLightLogic.HeadlightMode.Off;
        IntToMode[1] = VehicleLightLogic.HeadlightMode.Low;
        IntToMode[2] = VehicleLightLogic.HeadlightMode.High;
    }

    public static void ApplyHeadlights(LightsController lc, VehicleLightLogic.HeadlightMode mode)
    {
        if (lc == null || lc.WasCollected) return;
        EnsureLights(lc);
        // Steam 26326013+: never call typed set_AreHeadlightsOn (MissingMethod). Mode enum only.
        if (_setMode == null && _lightsPropSet == null)
        {
            LogLightsOnce("no CurrentHeadlightsMode setter on LightsController");
            _lightsPath = Path.Dead;
            return;
        }
        try
        {
            var boxed = BoxMode(_setMode ?? _lightsPropSet, ModeToInt[(int)mode]);
            if (_lightsPropSet != null)
                _lightsPropSet.Invoke(lc, Args1(boxed));
            else
                _setMode.Invoke(lc, Args1(boxed));
            _lightsPath = Path.ModeEnum;
            return;
        }
        catch (Exception e)
        {
            // Do not fall back to typed bool setter — it MissingMethods and can escape Il2Cpp stubs.
            _lightsPath = Path.Dead;
            if (!_applyFailLogged)
            {
                _applyFailLogged = true;
                Plugin.L.LogWarning($"[Lights] ApplyHeadlights CurrentHeadlightsMode invoke failed: {e.GetType().Name}: {e.Message}");
                if (e.InnerException != null)
                    Plugin.L.LogWarning($"[Lights] inner: {e.InnerException.GetType().Name}: {e.InnerException.Message}");
            }
        }
    }

    public static VehicleLightLogic.HeadlightMode ReadHeadlights(LightsController lc)
    {
        if (lc == null || lc.WasCollected) return VehicleLightLogic.HeadlightMode.Off;
        EnsureLights(lc);
        if (_lightsPath == Path.ModeEnum && _getMode != null)
        {
            try
            {
                var v = _getMode.Invoke(lc, null);
                if (v != null) return FromGameInt(ToInt(v));
            }
            catch { /* fall through */ }
        }
        try
        {
            var getOn = lc.GetIl2CppType().GetMethod("get_AreHeadlightsOn", BF);
            if (getOn == null) return VehicleLightLogic.HeadlightMode.Off;
            var boxed = getOn.Invoke(lc, null);
            if (boxed != null && Convert.ToBoolean(boxed))
                return VehicleLightLogic.HeadlightMode.Low;
        }
        catch { }
        return VehicleLightLogic.HeadlightMode.Off;
    }

    /// <summary>Self-test / local: set headlights on VehicleInput (mode API or ToggleHeadlights).</summary>
    public static void SetVehicleInput(VehicleInput vi, VehicleLightLogic.HeadlightMode mode)
    {
        if (vi == null || vi.WasCollected) return;
        EnsureInput(vi);
        if (_inputPath == Path.ModeEnum && _setInputMode != null)
        {
            try
            {
                var boxed = BoxMode(_setInputMode, ModeToInt[(int)mode]);
                _setInputMode.Invoke(vi, Args1(boxed));
                return;
            }
            catch (Exception e)
            {
                LogInputOnce($"set_HeadlightsMode failed: {e.Message}; trying ToggleHeadlights");
                _inputPath = Path.BoolSetter;
            }
        }
        if (_inputPath == Path.BoolSetter)
        {
            try
            {
                if (_toggleOld == null)
                {
                    _inputPath = Path.Dead;
                    LogInputOnce("VehicleInput BoolSetter without ToggleHeadlights");
                    return;
                }
                // Best-effort: toggle until ReadVehicleInput matches, max 3 (Off/Low/High cycle unknown).
                for (var i = 0; i < 3; i++)
                {
                    if (ReadVehicleInput(vi) == mode) break;
                    _toggleOld.Invoke(vi, null);
                }
            }
            catch (Exception e)
            {
                _inputPath = Path.Dead;
                LogInputOnce($"VehicleInput headlights: {e.Message}");
            }
        }
    }

    public static VehicleLightLogic.HeadlightMode ReadVehicleInput(VehicleInput vi)
    {
        if (vi == null || vi.WasCollected) return VehicleLightLogic.HeadlightMode.Off;
        EnsureInput(vi);
        if (_inputPath == Path.ModeEnum && _getInputMode != null)
        {
            try
            {
                var v = _getInputMode.Invoke(vi, null);
                if (v != null) return FromGameInt(ToInt(v));
            }
            catch { }
        }
        // Typed HeadlightsOn interop stub MissingMethod on Steam 26326013 — do not call it.
        try
        {
            var getOn = vi.GetIl2CppType().GetMethod("get_HeadlightsOn", BF);
            if (getOn == null) return VehicleLightLogic.HeadlightMode.Off;
            var boxed = getOn.Invoke(vi, null);
            if (boxed != null && Convert.ToBoolean(boxed))
                return VehicleLightLogic.HeadlightMode.Low;
        }
        catch { }
        return VehicleLightLogic.HeadlightMode.Off;
    }

    /// <summary>
    /// Mode for a FusionPlayer when OTC3 sample is missing: networked mode → VehicleInput → ButtonDown Low/Off.
    /// Never invents High from the bool button alone.
    /// </summary>
    public static VehicleLightLogic.HeadlightMode ResolveModeFromPlayer(FusionPlayer fp)
    {
        if (fp == null || fp.WasCollected) return VehicleLightLogic.HeadlightMode.Off;
        if (TryReadNetworkedMode(fp, out var net)) return net;
        try
        {
            var vi = fp.GetComponentInChildren<VehicleInput>() ?? fp.vehicleBase?.GetComponentInChildren<VehicleInput>();
            if (vi != null)
            {
                var m = ReadVehicleInput(vi);
                // On a remote observer VehicleInput may be absent/stale; still better than bool when present.
                return m;
            }
        }
        catch { }
        try
        {
            return fp.ButtonDown(NetworkInputData.VehicleButtons.Headlights)
                ? VehicleLightLogic.HeadlightMode.Low
                : VehicleLightLogic.HeadlightMode.Off;
        }
        catch { return VehicleLightLogic.HeadlightMode.Off; }
    }

    static string _netTypeName;

    public static bool TryReadNetworkedMode(FusionPlayer fp, out VehicleLightLogic.HeadlightMode mode)
    {
        mode = VehicleLightLogic.HeadlightMode.Off;
        if (fp == null) return false;
        try
        {
            EnsureNetMethod(fp);
            if (_getNetMode == null || string.IsNullOrEmpty(_netTypeName)) return false;
            var comps = fp.GetComponentsInChildren<Component>(true);
            if (comps == null) return false;
            for (var i = 0; i < comps.Count; i++)
            {
                var c = comps[i];
                if (c == null || c.WasCollected) continue;
                string tn = null;
                try { tn = c.GetIl2CppType().Name; } catch { continue; }
                if (tn != _netTypeName) continue;
                var v = _getNetMode.Invoke(c, null);
                if (v == null) return false;
                mode = FromGameInt(ToInt(v));
                return true;
            }
            return false;
        }
        catch (Exception e)
        {
            if (!_netLogged) { _netLogged = true; Plugin.L.LogWarning($"[Lights] GetNetworkedHeadlightsMode: {e.Message}"); }
            return false;
        }
    }

    /// <summary>Owner sampling: LightsController mode, else VehicleInput, else ButtonDown→Low.</summary>
    public static VehicleLightLogic.HeadlightMode SampleOwnerMode(FusionPlayer fp)
    {
        if (fp == null || fp.WasCollected) return VehicleLightLogic.HeadlightMode.Off;
        try
        {
            var lc = fp.GetComponentInChildren<LightsController>() ?? fp.vehicleBase?.GetComponentInChildren<LightsController>();
            if (lc != null) return ReadHeadlights(lc);
        }
        catch { }
        try
        {
            var vi = fp.GetComponentInChildren<VehicleInput>() ?? fp.vehicleBase?.GetComponentInChildren<VehicleInput>();
            if (vi != null) return ReadVehicleInput(vi);
        }
        catch { }
        return ResolveModeFromPlayer(fp);
    }

    static void EnsureLights(LightsController lc)
    {
        if (_lightsPath != Path.Unknown) return;
        try
        {
            var t = lc.GetIl2CppType();
            var prop = t.GetProperty("CurrentHeadlightsMode", BF);
            if (prop != null)
            {
                _getMode = prop.GetGetMethod(true) ?? prop.GetGetMethod(false) ?? _getMode;
                _lightsPropSet = prop.GetSetMethod(true) ?? prop.GetSetMethod(false);
            }
            _setMode = t.GetMethod("set_CurrentHeadlightsMode", BF) ?? _lightsPropSet;
            _getMode = t.GetMethod("get_CurrentHeadlightsMode", BF) ?? _getMode;
            if (_lightsPropSet == null) _lightsPropSet = _setMode;
            if (_setMode != null || _lightsPropSet != null)
            {
                _lightsPath = Path.ModeEnum;
                DiscoverEnum(_setMode ?? _lightsPropSet);
                Plugin.L.LogInfo("[Lights] API: CurrentHeadlightsMode");
                return;
            }
        }
        catch (Exception e) { LogLightsOnce($"probe mode API: {e.Message}"); }
        _lightsPath = Path.BoolSetter;
        Plugin.L.LogInfo("[Lights] API: AreHeadlightsOn bool");
    }

    static void EnsureInput(VehicleInput vi)
    {
        if (_inputPath != Path.Unknown) return;
        try
        {
            var t = vi.GetIl2CppType();
            var prop = t.GetProperty("HeadlightsMode", BF);
            if (prop != null)
            {
                _getInputMode = prop.GetGetMethod(true) ?? prop.GetGetMethod(false);
                _setInputMode = prop.GetSetMethod(true) ?? prop.GetSetMethod(false);
            }
            if (_setInputMode == null)
                _setInputMode = t.GetMethod("set_HeadlightsMode", BF) ?? t.GetMethod("SetHeadlightsMode", BF);
            if (_getInputMode == null)
                _getInputMode = t.GetMethod("get_HeadlightsMode", BF);
            _toggleOld = t.GetMethod("ToggleHeadlights", BF);
            var hasOld = t.GetMethod("get_HeadlightsOn", BF) != null || t.GetProperty("HeadlightsOn", BF) != null;
            if (_setInputMode != null)
            {
                _inputPath = Path.ModeEnum;
                if (!_mapReady) DiscoverEnum(_setInputMode);
                Plugin.L.LogInfo("[Lights] VehicleInput: HeadlightsMode (prop/method)");
                return;
            }
            if (!hasOld)
            {
                _inputPath = Path.Dead;
                LogInputOnce("VehicleInput has neither HeadlightsMode nor HeadlightsOn");
                return;
            }
        }
        catch (Exception e) { LogInputOnce($"probe input API: {e.Message}"); }
        _inputPath = Path.BoolSetter;
        Plugin.L.LogInfo("[Lights] VehicleInput: HeadlightsOn bool");
    }

    static void EnsureNetMethod(FusionPlayer fp)
    {
        if (_getNetMode != null || _netLogged) return;
        try
        {
            // Walk components for GetNetworkedHeadlightsMode (added ~26326013).
            var comps = fp.GetComponentsInChildren<Component>(true);
            if (comps == null) return;
            for (var i = 0; i < comps.Count; i++)
            {
                var c = comps[i];
                if (c == null || c.WasCollected) continue;
                MethodInfo m = null;
                try { m = c.GetIl2CppType().GetMethod("GetNetworkedHeadlightsMode", BF); }
                catch { continue; }
                if (m == null) continue;
                _getNetMode = m;
                _netTypeName = c.GetIl2CppType().Name;
                if (!_mapReady && m.ReturnType != null && m.ReturnType.IsEnum) DiscoverEnumType(m.ReturnType);
                Plugin.L.LogInfo($"[Lights] networked mode via {_netTypeName}.GetNetworkedHeadlightsMode");
                return;
            }
        }
        catch (Exception e)
        {
            _netLogged = true;
            Plugin.L.LogWarning($"[Lights] net mode probe: {e.Message}");
        }
    }

    static void DiscoverEnum(MethodInfo setterOrSimilar)
    {
        try
        {
            Il2CppSystem.Type et = null;
            var ps = setterOrSimilar.GetParameters();
            if (ps != null && ps.Length > 0 && ps[0].ParameterType != null && ps[0].ParameterType.IsEnum)
                et = ps[0].ParameterType;
            else if (setterOrSimilar.ReturnType != null && setterOrSimilar.ReturnType.IsEnum)
                et = setterOrSimilar.ReturnType;
            if (et != null) DiscoverEnumType(et);
        }
        catch (Exception e) { LogLightsOnce($"enum discover: {e.Message}"); }
    }

    static void DiscoverEnumType(Il2CppSystem.Type et)
    {
        if (et == null || !et.IsEnum) return;
        _modeEnumType = et;
        try
        {
            var names = Il2CppSystem.Enum.GetNames(et);
            var values = Il2CppSystem.Enum.GetValues(et);
            var sb = new StringBuilder();
            sb.Append("[Lights] HeadlightsMode enum live: ");
            // reset map
            for (var i = 0; i < IntToMode.Length; i++) IntToMode[i] = VehicleLightLogic.HeadlightMode.Off;
            ModeToInt[0] = 0; ModeToInt[1] = 1; ModeToInt[2] = 2;
            var n = names != null ? names.Length : 0;
            for (var i = 0; i < n; i++)
            {
                var name = names[i];
                var raw = 0;
                try
                {
                    var boxed = values.GetValue(i);
                    raw = ToInt(boxed);
                }
                catch { raw = i; }
                var ours = MapName(name);
                if (raw >= 0 && raw < IntToMode.Length) IntToMode[raw] = ours;
                ModeToInt[(int)ours] = raw;
                if (i > 0) sb.Append(", ");
                sb.Append(name).Append('=').Append(raw).Append("→").Append(ours);
            }
            _mapReady = true;
            if (!_enumLogged) { _enumLogged = true; Plugin.L.LogWarning(sb.ToString()); }
        }
        catch (Exception e)
        {
            if (!_enumLogged) { _enumLogged = true; Plugin.L.LogWarning($"[Lights] Enum.GetNames failed: {e.Message}"); }
        }
    }

    static VehicleLightLogic.HeadlightMode MapName(string name)
    {
        if (string.IsNullOrEmpty(name)) return VehicleLightLogic.HeadlightMode.Off;
        switch (name.Trim().ToLowerInvariant())
        {
            case "low": case "lowbeam": case "on": case "dipped": return VehicleLightLogic.HeadlightMode.Low;
            case "high": case "highbeam": case "full": case "fullbeam": return VehicleLightLogic.HeadlightMode.High;
            default: return VehicleLightLogic.HeadlightMode.Off;
        }
    }

    static VehicleLightLogic.HeadlightMode FromGameInt(int v)
    {
        if (v >= 0 && v < IntToMode.Length) return IntToMode[v];
        return VehicleLightLogic.HeadlightMode.Off;
    }

    const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    static Il2CppSystem.Object BoxMode(MethodInfo setter, int value)
    {
        var ps = setter.GetParameters();
        if (ps == null || ps.Length < 1) return value;
        var pt = ps[0].ParameterType;
        if (pt != null && pt.IsEnum) return Il2CppSystem.Enum.ToObject(pt, value);
        return value;
    }

    static Il2CppSystem.Object[] Args1(Il2CppSystem.Object a) => new Il2CppSystem.Object[] { a };

    static int ToInt(Il2CppSystem.Object v)
    {
        if (v == null) return 0;
        try { return Convert.ToInt32(v); } catch { }
        try
        {
            // Il2Cpp boxed enum often ToString()s as name — map by name.
            var s = v.ToString();
            if (VehicleLightLogic.TryParseMode(s, out var m)) return ModeToInt[(int)m];
            return MapName(s) switch
            {
                VehicleLightLogic.HeadlightMode.Low => ModeToInt[1],
                VehicleLightLogic.HeadlightMode.High => ModeToInt[2],
                _ => 0,
            };
        }
        catch { return 0; }
    }

    static void LogLightsOnce(string m)
    {
        if (_lightsLogged) return;
        _lightsLogged = true;
        Plugin.L.LogWarning("[Lights] " + m);
    }

    static void LogInputOnce(string m)
    {
        if (_inputLogged) return;
        _inputLogged = true;
        Plugin.L.LogWarning("[Lights] " + m);
    }
}
