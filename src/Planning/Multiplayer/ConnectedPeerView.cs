using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;

namespace CoopSolver;

internal static class ConnectedPeerView
{
    // The stable getter returns List<NetClientData>; beta returns IReadOnlyList.
    // Both expose the same peer records and use the same enumerable contract.
    private static readonly Func<NetHostGameService, IEnumerable<NetClientData>> ReadPeers =
        (typeof(NetHostGameService).GetProperty("ConnectedPeers")?.GetMethod ??
            throw new MissingMethodException(typeof(NetHostGameService).FullName, "get_ConnectedPeers"))
        .CreateDelegate<Func<NetHostGameService, IEnumerable<NetClientData>>>();

    internal static IEnumerable<NetClientData> Read(NetHostGameService host) => ReadPeers(host);
}
