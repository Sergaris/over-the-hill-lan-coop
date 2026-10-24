using System;

namespace OthLan;

/// <summary>
/// 0.2.0 T3.3: pure filter for host OTC3 relay (endpoint must match the slot's peer).
/// </summary>
public static class RelayLogic
{
    public enum Decision { Accept, DropWrongSlot, DropUnknownPeer, DropBadPacket }

    /// <param name="packetSlot">slot field from OTC3 header</param>
    /// <param name="fromMatchesPeer">true if sender endpoint equals HostPings.EndpointOf(packetSlot)</param>
    /// <param name="peerKnown">true if EndpointOf(packetSlot) is non-null</param>
    public static Decision Filter(byte packetSlot, bool peerKnown, bool fromMatchesPeer)
    {
        if (packetSlot == CarSyncLogic.HostSlot) return Decision.DropWrongSlot; // host does not receive its own stream over UDP
        if (!peerKnown) return Decision.DropUnknownPeer;
        if (!fromMatchesPeer) return Decision.DropWrongSlot;
        return Decision.Accept;
    }
}
