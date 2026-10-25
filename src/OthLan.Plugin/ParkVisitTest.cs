using UnityEngine;

namespace OthLan;

#if SELFTEST
/// <summary>
/// Self-test only (--othlan-park-visit T, host): at T the own car is teleported 20 m from the nearest parked car;
/// checks that the parked car wakes, is pushed by a physics impulse, and parks again after the own car is teleported
/// back to where it came from.
/// </summary>
internal static class ParkVisitTest
{
    public static float At = -1;
    static int _step; static float _next; static uint _id; static Rigidbody _target;
    static Vector3 _homePos, _targetPos0; static Quaternion _homeRot; static int _parks0;

    public static void Tick(float t)
    {
        if (At < 0 || t < At || t < _next || LanState.Active != LanRole.Host) return;
        if (!CarRegistry.TryGetOwn(out var own) || own.Rb == null || own.Nrb == null) return;
        switch (_step)
        {
            case 0:
                _target = ParkedCars.NearestParked(own.Rb.position, out _id);
                if (_target == null) { Autopilot.Check("park", false, $"t={t:0}s no parked car to visit ({ParkedCars.Describe()})"); At = -1; return; }
                _homePos = own.Rb.position; _homeRot = own.Rb.rotation; _targetPos0 = _target.position;
                var side = Vector3.Cross(Vector3.up, (_targetPos0 - _homePos).normalized);
                if (side.sqrMagnitude < 0.01f) side = Vector3.right;
                Teleport(own, Ground(_targetPos0 + side.normalized * 20f), own.Rb.rotation);
                Plugin.L.LogWarning($"[Autopilot] t={t:0.0}s park visit: own car teleported 20 m from {_target.gameObject.name}#{_id} ({Vector3.Distance(_homePos, _targetPos0):0} m away)");
                Step(t, 2f); break;
            case 1:
                Autopilot.Check("park", !ParkedCars.IsParked(_id) && !_target.isKinematic, $"t={t:0}s visited car woke (parked={ParkedCars.IsParked(_id)} kinematic={_target.isKinematic})");
                _target.AddForce((_targetPos0 - own.Rb.position).normalized * 4f + Vector3.up * 1f, ForceMode.VelocityChange);
                Step(t, 3f); break;
            case 2:
                var moved = Vector3.Distance(_target.position, _targetPos0);
                Autopilot.Check("park", moved > 0.5f, $"t={t:0}s woken car moved {moved:0.00} m after a push");
                _parks0 = ParkedCars.Parks;
                Teleport(own, _homePos, _homeRot);
                Plugin.L.LogWarning($"[Autopilot] t={t:0.0}s park visit: own car teleported back");
                Step(t, 12f); break;
            case 3:
                Autopilot.Check("park", ParkedCars.IsParked(_id) && ParkedCars.Parks > _parks0, $"t={t:0}s left car parked again ({ParkedCars.Describe()})");
                At = -1; break;
        }
    }

    static void Step(float t, float wait) { _step++; _next = t + wait; }

    static void Teleport(CarEntry own, Vector3 pos, Quaternion rot)
    {
        own.Rb.linearVelocity = Vector3.zero; own.Rb.angularVelocity = Vector3.zero;
        own.Nrb.Teleport(new Il2CppSystem.Nullable<Vector3>(pos), new Il2CppSystem.Nullable<Quaternion>(rot));
    }

    static Vector3 Ground(Vector3 p) =>
        Physics.Raycast(p + Vector3.up * 50f, Vector3.down, out var hit, 200f) ? hit.point + Vector3.up * 1.5f : p + Vector3.up * 2f;
}
#endif
