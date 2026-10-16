using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace KsttkDl.Core.Tunnel;

/// <summary> OpenVPN's control channel without tls-auth. each packet is opcode/key id, our 8-byte session id, the ids we acknowledge (plus the peer's session id when there are any), then a message id and a slice of the tls byte stream. the outer link is tcp, so nothing is ever retransmitted here, but the server only takes a few unacknowledged packets at once, and it resends anything we fail to acknowledge </summary>
internal sealed class ControlChannel
{
    public const byte OpHardResetClientV2 = 7;
    public const byte OpHardResetServerV2 = 8;
    public const byte OpControlV1 = 4;
    public const byte OpAckV1 = 5;
    public const byte OpSoftResetV1 = 3;

    // a control packet has to fit the server's tls buffer; OpenVPN's default control mtu is 1250 with headers included
    private const int MaxPayload = 1000;
    private const int SendWindow = 4;

    private readonly Action<byte[]> _send;
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
    private readonly SemaphoreSlim _window = new(SendWindow, SendWindow);
    private readonly HashSet<uint> _unacked = [];
    private readonly Dictionary<uint, byte[]> _early = [];
    private readonly TaskCompletionSource _resetDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _lock = new();

    private uint _nextSendId;
    private uint _nextReceiveId;

    public ControlChannel(Action<byte[]> send)
    {
        _send = send;
        Stream = new ControlStream(this);
    }

    public byte[] LocalSessionId { get; } = RandomNumberGenerator.GetBytes(8);

    public byte[] RemoteSessionId { get; private set; } = [];

    /// <summary>the tls byte stream carried by the channel.</summary>
    public Stream Stream { get; }

    public Task ResetDone => _resetDone.Task;

    public void SendHardReset()
    {
        // the reset holds a window slot until the server acknowledges it, like any other message
        _window.Wait();
        SendMessage(OpHardResetClientV2, []);
    }

    public void Fail(Exception error)
    {
        _incoming.Writer.TryComplete(error);
        _resetDone.TrySetException(error);
    }

    /// <summary>handles one control-type packet from the link.</summary>
    public void Receive(byte opcode, ReadOnlySpan<byte> packet)
    {
        // packet[0] is opcode/key id
        var at = 1 + 8;
        var remoteSession = packet[1..at].ToArray();

        int ackCount = packet[at++];
        lock (_lock)
        {
            for (var i = 0; i < ackCount; i++)
            {
                var id = BinaryPrimitives.ReadUInt32BigEndian(packet[(at + 4 * i)..]);
                if (_unacked.Remove(id))
                {
                    _window.Release();
                }
            }
        }

        at += 4 * ackCount;
        if (ackCount > 0)
        {
            at += 8;
        }

        if (opcode == OpAckV1)
        {
            return;
        }

        var messageId = BinaryPrimitives.ReadUInt32BigEndian(packet[at..]);
        var payload = packet[(at + 4)..].ToArray();

        // acknowledge every copy, so a resend caused by a slow ack stops
        SendAck(messageId, remoteSession);

        lock (_lock)
        {
            if (messageId < _nextReceiveId || _early.ContainsKey(messageId))
            {
                return;
            }

            _early[messageId] = payload;
            while (_early.Remove(_nextReceiveId, out var next))
            {
                _nextReceiveId++;
                Deliver(opcode, remoteSession, next);
            }
        }
    }

    private void Deliver(byte opcode, byte[] remoteSession, byte[] payload)
    {
        switch (opcode)
        {
            case OpHardResetServerV2:
                RemoteSessionId = remoteSession;
                _resetDone.TrySetResult();
                break;
            case OpControlV1 when payload.Length > 0:
                _incoming.Writer.TryWrite(payload);
                break;
            case OpSoftResetV1:
                // renegotiation is not implemented; the tunnel is rebuilt instead when the server gives up on us
                break;
        }
    }

    private void SendAck(uint messageId, byte[] remoteSession)
    {
        var packet = new byte[1 + 8 + 1 + 4 + 8];
        packet[0] = OpAckV1 << 3;
        LocalSessionId.CopyTo(packet, 1);
        packet[9] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(10), messageId);
        remoteSession.CopyTo(packet, 14);
        _send(packet);
    }

    private void SendMessage(byte opcode, ReadOnlySpan<byte> payload)
    {
        uint id;
        lock (_lock)
        {
            id = _nextSendId++;
            _unacked.Add(id);
        }

        var packet = new byte[1 + 8 + 1 + 4 + payload.Length];
        packet[0] = (byte)(opcode << 3);
        LocalSessionId.CopyTo(packet, 1);
        packet[9] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(10), id);
        payload.CopyTo(packet.AsSpan(14));
        _send(packet);
    }

    private async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        for (var at = 0; at < data.Length; at += MaxPayload)
        {
            if (!await _window.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken))
            {
                throw new IOException("The server stopped acknowledging control packets.");
            }

            SendMessage(OpControlV1, data.Span.Slice(at, Math.Min(MaxPayload, data.Length - at)));
        }
    }

    /// <summary>presents the channel as a byte stream for SslStream.</summary>
    private sealed class ControlStream(ControlChannel owner) : Stream
    {
        private byte[] _pending = [];
        private int _pendingAt;

        public override bool CanRead => true;

        public override bool CanWrite => true;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_pendingAt >= _pending.Length)
            {
                if (!await owner._incoming.Reader.WaitToReadAsync(cancellationToken))
                {
                    return 0;
                }

                _pending = await owner._incoming.Reader.ReadAsync(cancellationToken);
                _pendingAt = 0;
            }

            var count = Math.Min(buffer.Length, _pending.Length - _pendingAt);
            _pending.AsMemory(_pendingAt, count).CopyTo(buffer);
            _pendingAt += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(owner.WriteAsync(buffer, cancellationToken));

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            owner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) =>
            owner.WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
