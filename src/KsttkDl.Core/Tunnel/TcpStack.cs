using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;

namespace KsttkDl.Core.Tunnel;

/// <summary> the client half of ipv4 tcp, enough to carry http over the tunnel's raw packets. it opens connections, never listens, and leaves congestion control to the far end: the payload is downloads, so the server's sender is the one that matters, and the outer OpenVPN link is tcp, so loss inside the tunnel is rare </summary>
public sealed class TcpStack : IDisposable
{
    internal const int Mss = 1360;

    private readonly IPAddress _local;
    private readonly Action<byte[]> _sendIp;
    private readonly ConcurrentDictionary<(ushort LocalPort, uint RemoteIp, ushort RemotePort), TcpConnection> _connections = new();
    private readonly Timer _timer;
    private int _ipId;
    private int _nextPort = Random.Shared.Next(49152, 65000);
    private Exception? _failure;

    public TcpStack(IPAddress local, Action<byte[]> sendIp)
    {
        _local = local;
        _sendIp = sendIp;
        LocalAddress = BinaryPrimitives.ReadUInt32BigEndian(local.GetAddressBytes());
        _timer = new Timer(_ => Tick(), null, 10, 10);
    }

    internal uint LocalAddress { get; }

    public async Task<Stream> ConnectAsync(IPAddress remote, int port, CancellationToken cancellationToken)
    {
        if (_failure is not null)
        {
            throw new IOException("The tunnel is down.", _failure);
        }

        var remoteIp = BinaryPrimitives.ReadUInt32BigEndian(remote.GetAddressBytes());

        TcpConnection connection;
        while (true)
        {
            var localPort = (ushort)(49152 + Interlocked.Increment(ref _nextPort) % 16000);
            connection = new TcpConnection(this, localPort, remoteIp, (ushort)port);
            if (_connections.TryAdd(connection.Key, connection))
            {
                break;
            }
        }

        try
        {
            await connection.ConnectAsync(cancellationToken);
            return connection;
        }
        catch
        {
            connection.Abort(new IOException("Connect failed."), sendReset: true);
            throw;
        }
    }

    /// <summary>takes one ip packet from the tunnel.</summary>
    public void Receive(byte[] packet)
    {
        if (packet.Length < 40 || packet[0] >> 4 != 4 || packet[9] != 6)
        {
            return;
        }

        var ipHeader = (packet[0] & 0x0f) * 4;
        var total = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2)), packet.Length);
        if (BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(16)) != LocalAddress || total < ipHeader + 20)
        {
            return;
        }

        var segment = new ReadOnlySpan<byte>(packet, ipHeader, total - ipHeader);
        var key = (
            BinaryPrimitives.ReadUInt16BigEndian(segment[2..]),
            BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(12)),
            BinaryPrimitives.ReadUInt16BigEndian(segment));

        if (_connections.TryGetValue(key, out var connection))
        {
            connection.Receive(segment);
        }
    }

    /// <summary>fails every open connection, for when the tunnel itself is gone.</summary>
    public void Fail(Exception error)
    {
        _failure = error;
        foreach (var connection in _connections.Values)
        {
            connection.Abort(new IOException("The tunnel went down.", error), sendReset: false);
        }
    }

    internal void Remove(TcpConnection connection) => _connections.TryRemove(connection.Key, out _);

    internal void SendSegment(
        TcpConnection connection, uint seq, uint ack, byte flags, ushort window, ReadOnlySpan<byte> options, ReadOnlySpan<byte> data)
    {
        if (_failure is not null)
        {
            return;
        }

        var tcpHeader = 20 + options.Length;
        var packet = new byte[20 + tcpHeader + data.Length];

        // ipv4 header, don't-fragment set
        packet[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), (ushort)Interlocked.Increment(ref _ipId));
        packet[6] = 0x40;
        packet[8] = 64;
        packet[9] = 6;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(12), LocalAddress);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16), connection.Key.RemoteIp);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10), Checksum(packet.AsSpan(0, 20), 0));

        var tcp = packet.AsSpan(20);
        BinaryPrimitives.WriteUInt16BigEndian(tcp, connection.Key.LocalPort);
        BinaryPrimitives.WriteUInt16BigEndian(tcp[2..], connection.Key.RemotePort);
        BinaryPrimitives.WriteUInt32BigEndian(tcp[4..], seq);
        BinaryPrimitives.WriteUInt32BigEndian(tcp[8..], ack);
        tcp[12] = (byte)(tcpHeader / 4 << 4);
        tcp[13] = flags;
        BinaryPrimitives.WriteUInt16BigEndian(tcp[14..], window);
        options.CopyTo(tcp[20..]);
        data.CopyTo(tcp[tcpHeader..]);

        // pseudo-header: source, destination, protocol and tcp length
        var pseudo = (uint)(LocalAddress >> 16) + (LocalAddress & 0xffff)
                     + (connection.Key.RemoteIp >> 16) + (connection.Key.RemoteIp & 0xffff)
                     + 6u + (uint)tcp.Length;
        BinaryPrimitives.WriteUInt16BigEndian(tcp[16..], Checksum(tcp, pseudo));

        try
        {
            _sendIp(packet);
        }
        catch (IOException ex)
        {
            Fail(ex);
        }
    }

    private static ushort Checksum(ReadOnlySpan<byte> data, uint sum)
    {
        var i = 0;
        for (; i + 1 < data.Length; i += 2)
        {
            sum += (uint)(data[i] << 8 | data[i + 1]);
        }

        if (i < data.Length)
        {
            sum += (uint)(data[i] << 8);
        }

        while (sum >> 16 != 0)
        {
            sum = (sum & 0xffff) + (sum >> 16);
        }

        return (ushort)~sum;
    }

    private void Tick()
    {
        var now = Environment.TickCount64;
        foreach (var connection in _connections.Values)
        {
            connection.Tick(now);
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        Fail(new ObjectDisposedException(nameof(TcpStack)));
    }
}
