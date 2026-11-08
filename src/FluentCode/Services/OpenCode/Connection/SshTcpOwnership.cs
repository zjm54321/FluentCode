using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCode.Services.OpenCode.Connection;

internal interface ISshTcpOwnership
{
    int? PeerOwner(IPEndPoint forward, IPEndPoint client);
}

internal sealed class SshLoopbackTransport(IPEndPoint endpoint, int pid, Func<bool> alive, ISshTcpOwnership? ownership = null)
    : OpenCodeSocketTransport(endpoint)
{
    private readonly ISshTcpOwnership _ownership = ownership ?? new WindowsSshTcpOwnership();
    protected override bool HasAuthority => alive(); // Checks the originally held process handle, not just a reusable PID.
    protected override async ValueTask<Stream> OpenVerifiedStreamAsync(CancellationToken ct)
    {
        var socket = new Socket(Endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            if (!alive()) throw new SshOpenCodeException(SshFailureKind.TransportOwner);
            await socket.ConnectAsync(Endpoint, ct).ConfigureAwait(false);
            var local = (IPEndPoint)socket.LocalEndPoint!;
            // The peer's final ACK can race the first table snapshot. Wait only for a missing established row,
            // never for a mismatched owner. No application byte is written during this bounded check.
            for (int attempt = 0; attempt < 50; attempt++)
            {
                if (!alive()) throw new SshOpenCodeException(SshFailureKind.TransportOwner);
                int? owner = _ownership.PeerOwner(Endpoint, local);
                if (owner == pid)
                {
                    if (!alive()) throw new SshOpenCodeException(SshFailureKind.TransportOwner);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                if (owner is not null) throw new SshOpenCodeException(SshFailureKind.TransportOwner);
                await Task.Delay(20, ct).ConfigureAwait(false);
            }
            throw new SshOpenCodeException(SshFailureKind.TransportOwner);
        }
        catch { socket.Dispose(); throw; }
    }
}

internal sealed class WindowsSshTcpOwnership : ISshTcpOwnership
{
    public int? PeerOwner(IPEndPoint forward, IPEndPoint client)
    {
        uint family = forward.AddressFamily == AddressFamily.InterNetwork ? 2u : 23u;
        uint size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 4, 0); // TCP_TABLE_OWNER_PID_CONNECTIONS
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (size is < 4 or > 16 * 1024 * 1024) throw new SshOpenCodeException(SshFailureKind.TransportOwner);
            IntPtr table = Marshal.AllocHGlobal(checked((int)size));
            try
            {
                uint result = GetExtendedTcpTable(table, ref size, false, family, 4, 0);
                if (result == 122) continue;
                if (result != 0) throw new SshOpenCodeException(SshFailureKind.TransportOwner);
                byte[] bytes = new byte[size];
                Marshal.Copy(table, bytes, 0, bytes.Length);
                return FindPeerOwner(bytes, forward, client);
            }
            finally { Marshal.FreeHGlobal(table); }
        }
        throw new SshOpenCodeException(SshFailureKind.TransportOwner);
    }

    internal static int? FindPeerOwner(ReadOnlySpan<byte> table, IPEndPoint forward, IPEndPoint client)
    {
        if (table.Length < 4) throw new SshOpenCodeException(SshFailureKind.TransportOwner);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(table);
        bool ipv4 = forward.AddressFamily == AddressFamily.InterNetwork;
        int length = ipv4 ? 24 : 56;
        if (count > (table.Length - 4) / length) throw new SshOpenCodeException(SshFailureKind.TransportOwner);
        for (int i = 0; i < count; i++)
        {
            var row = table.Slice(4 + i * length, length);
            uint state = BinaryPrimitives.ReadUInt32LittleEndian(row[(ipv4 ? 0 : 48)..]);
            if (state != 5) continue; // MIB_TCP_STATE_ESTAB
            var localAddress = ipv4 ? new IPAddress(row.Slice(4, 4)) : new IPAddress(row[..16], BinaryPrimitives.ReadUInt32LittleEndian(row[16..]));
            var remoteAddress = ipv4 ? new IPAddress(row.Slice(12, 4)) : new IPAddress(row.Slice(24, 16), BinaryPrimitives.ReadUInt32LittleEndian(row[40..]));
            int localPort = BinaryPrimitives.ReadUInt16BigEndian(row[(ipv4 ? 8 : 20)..]);
            int remotePort = BinaryPrimitives.ReadUInt16BigEndian(row[(ipv4 ? 16 : 44)..]);
            if (localAddress.Equals(forward.Address) && localPort == forward.Port && remoteAddress.Equals(client.Address) && remotePort == client.Port)
                return checked((int)BinaryPrimitives.ReadUInt32LittleEndian(row[(ipv4 ? 20 : 52)..]));
        }
        return null;
    }
    [DllImport("iphlpapi.dll", SetLastError = true)] private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, uint family, int tableClass, uint reserved);
}
