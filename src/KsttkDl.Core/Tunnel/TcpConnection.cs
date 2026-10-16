using System.Buffers.Binary;

namespace KsttkDl.Core.Tunnel;

/// <summary> one outgoing tcp connection inside the tunnel, exposed as a Stream. it keeps a large, window-scaled receive buffer so a relay several hundred milliseconds away can still fill the link, reorders early segments and reports them with sack so the server resends only the holes, acknowledges every second segment (or after a 10 ms tick), and resends from the oldest unacknowledged byte when the retransmission timer fires </summary>
internal sealed class TcpConnection : Stream
{
    private const byte Fin = 0x01;
    private const byte Syn = 0x02;
    private const byte Rst = 0x04;
    private const byte Psh = 0x08;
    private const byte Ack = 0x10;

    private const int ReceiveBuffer = 4 << 20;
    private const byte WindowShift = 7;
    private const int MaxBackoff = 7;

    private enum State
    {
        SynSent,
        Established,
        Closed,
    }

    private readonly TcpStack _stack;
    private readonly object _lock = new();
    private readonly TaskCompletionSource _established = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private State _state = State.SynSent;

    // send side: _out holds every byte from _sndUna on, sent or not
    private uint _iss;
    private uint _sndUna;
    private uint _sndNxt;
    private long _sndWnd;
    private int _peerShift;
    private bool _scaling;
    private bool _sack;
    private int _peerMss = TcpStack.Mss;
    private byte[] _out = new byte[16 * 1024];
    private int _outLength;
    private bool _finQueued;
    private bool _finSent;
    private uint _finSeq;

    // receive side
    private uint _rcvNxt;
    private readonly Queue<byte[]> _in = new();
    private int _inOffset;
    private int _buffered;
    private readonly Dictionary<uint, byte[]> _early = [];
    private uint? _remoteFinSeq;
    private bool _remoteClosed;
    private int _ackPending;
    private int _lastAdvertised;
    private TaskCompletionSource? _readWaiter;

    // timers, in Environment.TickCount64 milliseconds
    private long _rtoDeadline;
    private int _backoff;
    private double _srtt;
    private int _rto = 1000;
    private uint _timedSeq;
    private long _timedAt;
    private long _lingerDeadline;
    private bool _disposed;
    private Exception? _error;

    public TcpConnection(TcpStack stack, ushort localPort, uint remoteIp, ushort remotePort)
    {
        _stack = stack;
        Key = (localPort, remoteIp, remotePort);
    }

    public (ushort LocalPort, uint RemoteIp, ushort RemotePort) Key { get; }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _iss = (uint)Random.Shared.NextInt64(0, uint.MaxValue);
            _sndUna = _iss;
            _sndNxt = _iss + 1;
            SendSyn();
        }

        await _established.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
    }

    private void SendSyn()
    {
        // mss, nop + window scale, nop + nop + sack permitted
        byte[] options = [2, 4, TcpStack.Mss >> 8, TcpStack.Mss & 0xff, 1, 3, 3, WindowShift, 1, 1, 4, 2];
        _stack.SendSegment(this, _iss, 0, Syn, 65535, options, []);
        _timedAt = Environment.TickCount64;
        _rtoDeadline = _timedAt + (_rto << _backoff);
    }

    public void Receive(ReadOnlySpan<byte> segment)
    {
        lock (_lock)
        {
            if (_state == State.Closed)
            {
                return;
            }

            var seq = BinaryPrimitives.ReadUInt32BigEndian(segment[4..]);
            var ack = BinaryPrimitives.ReadUInt32BigEndian(segment[8..]);
            var headerLength = (segment[12] >> 4) * 4;
            var flags = segment[13];
            var window = BinaryPrimitives.ReadUInt16BigEndian(segment[14..]);
            if (headerLength < 20 || headerLength > segment.Length)
            {
                return;
            }

            if ((flags & Rst) != 0)
            {
                AbortLocked(new IOException("The connection was reset by the server."), sendReset: false);
                return;
            }

            if (_state == State.SynSent)
            {
                if ((flags & (Syn | Ack)) == (Syn | Ack) && ack == _iss + 1)
                {
                    ReadSynOptions(segment[20..headerLength]);
                    _rcvNxt = seq + 1;
                    _sndUna = ack;
                    _sndWnd = window;
                    _state = State.Established;
                    Sample(Environment.TickCount64 - _timedAt);
                    _rtoDeadline = 0;
                    _backoff = 0;
                    SendAck();
                    _established.TrySetResult();
                }

                return;
            }

            if ((flags & Ack) != 0)
            {
                ProcessAck(ack, window);
                if (_state == State.Closed)
                {
                    return;
                }
            }

            if ((flags & Syn) != 0)
            {
                // our ack of the syn-ack was lost
                SendAck();
                return;
            }

            var data = segment[headerLength..];
            if (data.Length > 0 || (flags & Fin) != 0)
            {
                ProcessData(seq, data, (flags & Fin) != 0);
            }

            TrySend();
            FinishIfDone();
        }
    }

    private void ReadSynOptions(ReadOnlySpan<byte> options)
    {
        for (var i = 0; i < options.Length;)
        {
            var kind = options[i];
            if (kind == 0)
            {
                break;
            }

            if (kind == 1)
            {
                i++;
                continue;
            }

            if (i + 1 >= options.Length || options[i + 1] < 2)
            {
                break;
            }

            var length = options[i + 1];
            if (kind == 2 && length == 4 && i + 4 <= options.Length)
            {
                _peerMss = Math.Min(TcpStack.Mss, (int)BinaryPrimitives.ReadUInt16BigEndian(options[(i + 2)..]));
            }
            else if (kind == 3 && length == 3 && i + 3 <= options.Length)
            {
                _scaling = true;
                _peerShift = Math.Min(14, (int)options[i + 2]);
            }
            else if (kind == 4 && length == 2)
            {
                _sack = true;
            }

            i += length;
        }
    }

    private void ProcessAck(uint ack, ushort window)
    {
        var acked = (int)(ack - _sndUna);
        var inFlight = (int)(_sndNxt - _sndUna);
        if (acked < 0 || acked > inFlight)
        {
            return;
        }

        _sndWnd = (long)window << (_scaling ? _peerShift : 0);
        if (acked == 0)
        {
            return;
        }

        // the fin takes a sequence number but no byte in _out
        var dataAcked = Math.Min(acked, _outLength);
        Buffer.BlockCopy(_out, dataAcked, _out, 0, _outLength - dataAcked);
        _outLength -= dataAcked;
        _sndUna = ack;

        if (_timedSeq != 0 && (int)(ack - _timedSeq) >= 0)
        {
            Sample(Environment.TickCount64 - _timedAt);
            _timedSeq = 0;
        }

        _backoff = 0;
        _rtoDeadline = _sndNxt != _sndUna ? Environment.TickCount64 + _rto : 0;
    }

    private void Sample(long rtt)
    {
        _srtt = _srtt == 0 ? rtt : 0.875 * _srtt + 0.125 * rtt;
        _rto = (int)Math.Clamp(2 * _srtt + 100, 300, 5000);
    }

    private void ProcessData(uint seq, ReadOnlySpan<byte> data, bool fin)
    {
        if (_disposed && data.Length > 0)
        {
            // nobody will read this any more
            AbortLocked(new IOException("Closed."), sendReset: true);
            return;
        }

        var offset = (int)(seq - _rcvNxt);
        if (fin)
        {
            _remoteFinSeq = seq + (uint)data.Length;
        }

        var ackNow = false;
        if (offset > 0)
        {
            // early segment: keep it and send a duplicate ack
            if (data.Length > 0 && offset < ReceiveBuffer)
            {
                _early.TryAdd(seq, data.ToArray());
            }

            ackNow = true;
        }
        else if (offset + data.Length > 0)
        {
            Deliver(data[-offset..]);
            DrainEarly();
        }
        else if (data.Length > 0)
        {
            // a resend of data already delivered
            ackNow = true;
        }

        if (_remoteFinSeq == _rcvNxt && !_remoteClosed)
        {
            _rcvNxt++;
            _remoteClosed = true;
            ackNow = true;
            WakeReader();
        }

        if (ackNow || ++_ackPending >= 2)
        {
            SendAck();
        }
    }

    private void Deliver(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        _in.Enqueue(data.ToArray());
        _buffered += data.Length;
        _rcvNxt += (uint)data.Length;
        WakeReader();
    }

    private void DrainEarly()
    {
        var progressed = true;
        while (progressed && _early.Count > 0)
        {
            progressed = false;
            foreach (var (seq, data) in _early)
            {
                var offset = (int)(seq - _rcvNxt);
                if (offset > 0)
                {
                    continue;
                }

                _early.Remove(seq);
                if (offset + data.Length > 0)
                {
                    Deliver(data.AsSpan(-offset));
                }

                progressed = true;
                break;
            }
        }
    }

    // early segments already sit inside the advertised window, so they do not shrink it; a window that moved would also stop the server from counting our duplicate acks
    private int WindowBytes() => Math.Max(0, ReceiveBuffer - _buffered);

    private void SendAck()
    {
        var bytes = WindowBytes();
        var field = Math.Min(65535, _scaling ? bytes >> WindowShift : bytes);
        _stack.SendSegment(this, _sndNxt, _rcvNxt, Ack, (ushort)field, SackOptions(), []);
        _lastAdvertised = _scaling ? field << WindowShift : field;
        _ackPending = 0;
    }

    /// <summary>up to three sack blocks describing the early data held beyond the first hole.</summary>
    private byte[] SackOptions()
    {
        if (!_sack || _early.Count == 0)
        {
            return [];
        }

        var ranges = new List<(uint Left, uint Right)>();
        foreach (var (seq, data) in _early.OrderBy(e => (int)(e.Key - _rcvNxt)))
        {
            var right = seq + (uint)data.Length;
            if (ranges.Count > 0 && (int)(seq - ranges[^1].Right) <= 0)
            {
                if ((int)(right - ranges[^1].Right) > 0)
                {
                    ranges[^1] = (ranges[^1].Left, right);
                }
            }
            else
            {
                ranges.Add((seq, right));
            }
        }

        // the newest blocks matter most to the sender; with no timestamps, three fit easily
        var blocks = ranges.TakeLast(3).Reverse().ToList();
        var options = new byte[4 + 8 * blocks.Count];
        options[0] = 1;
        options[1] = 1;
        options[2] = 5;
        options[3] = (byte)(2 + 8 * blocks.Count);
        for (var i = 0; i < blocks.Count; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(options.AsSpan(4 + 8 * i), blocks[i].Left);
            BinaryPrimitives.WriteUInt32BigEndian(options.AsSpan(8 + 8 * i), blocks[i].Right);
        }

        return options;
    }

    private void TrySend()
    {
        if (_state != State.Established)
        {
            return;
        }

        while (true)
        {
            var inFlight = (int)(_sndNxt - _sndUna);
            var unsent = _outLength - inFlight;
            if (unsent <= 0)
            {
                break;
            }

            // a closed window still gets a one-byte probe once nothing is in flight
            var room = (int)Math.Min(int.MaxValue, _sndWnd - inFlight);
            if (room <= 0 && inFlight == 0)
            {
                room = 1;
            }

            if (room <= 0)
            {
                break;
            }

            var count = Math.Min(unsent, Math.Min(room, _peerMss));
            var window = (ushort)Math.Min(65535, _scaling ? WindowBytes() >> WindowShift : WindowBytes());
            _stack.SendSegment(this, _sndNxt, _rcvNxt, Ack | Psh, window, [], _out.AsSpan(inFlight, count));
            _ackPending = 0;

            if (_timedSeq == 0)
            {
                _timedSeq = _sndNxt + (uint)count;
                _timedAt = Environment.TickCount64;
            }

            _sndNxt += (uint)count;
            if (_rtoDeadline == 0)
            {
                _rtoDeadline = Environment.TickCount64 + _rto;
            }
        }

        if (_finQueued && !_finSent && (int)(_sndNxt - _sndUna) == _outLength)
        {
            _finSeq = _sndNxt;
            _stack.SendSegment(this, _sndNxt, _rcvNxt, Fin | Ack, 0, [], []);
            _sndNxt++;
            _finSent = true;
            if (_rtoDeadline == 0)
            {
                _rtoDeadline = Environment.TickCount64 + _rto;
            }
        }
    }

    public void Tick(long now)
    {
        lock (_lock)
        {
            if (_state == State.Closed)
            {
                return;
            }

            if (_ackPending > 0)
            {
                SendAck();
            }

            if (_lingerDeadline != 0 && now >= _lingerDeadline)
            {
                AbortLocked(new IOException("Closed."), sendReset: true);
                return;
            }

            if (_rtoDeadline == 0 || now < _rtoDeadline)
            {
                return;
            }

            if (++_backoff > MaxBackoff)
            {
                AbortLocked(new IOException("The connection timed out."), sendReset: _state != State.SynSent);
                return;
            }

            if (_state == State.SynSent)
            {
                SendSyn();
                return;
            }

            // go back to the oldest unacknowledged byte; Karn's rule: no rtt sample from resent data
            _sndNxt = _sndUna;
            _finSent = false;
            _timedSeq = 0;
            _rtoDeadline = 0;
            TrySend();
            _timedSeq = 0;
            _rtoDeadline = now + Math.Min(60_000, _rto << _backoff);
        }
    }

    private void FinishIfDone()
    {
        if (_finSent && _sndUna == _finSeq + 1 && _remoteClosed)
        {
            CloseLocked();
        }
    }

    private void CloseLocked()
    {
        _state = State.Closed;
        _stack.Remove(this);
        WakeReader();
    }

    public void Abort(Exception error, bool sendReset)
    {
        lock (_lock)
        {
            AbortLocked(error, sendReset);
        }
    }

    private void AbortLocked(Exception error, bool sendReset)
    {
        if (_state == State.Closed)
        {
            return;
        }

        if (sendReset && _state == State.Established)
        {
            _stack.SendSegment(this, _sndNxt, _rcvNxt, Rst | Ack, 0, [], []);
        }

        _error ??= error;
        _established.TrySetException(error);
        CloseLocked();
    }

    private void WakeReader()
    {
        var waiter = _readWaiter;
        _readWaiter = null;
        waiter?.TrySetResult();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0)
        {
            return 0;
        }

        while (true)
        {
            Task waiter;
            lock (_lock)
            {
                if (_in.Count > 0)
                {
                    return TakeLocked(buffer.Span);
                }

                if (_remoteClosed)
                {
                    return 0;
                }

                if (_error is not null)
                {
                    throw new IOException(_error.Message, _error);
                }

                if (_state == State.Closed)
                {
                    return 0;
                }

                _readWaiter ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                waiter = _readWaiter.Task;
            }

            await waiter.WaitAsync(cancellationToken);
        }
    }

    private int TakeLocked(Span<byte> buffer)
    {
        var copied = 0;
        while (copied < buffer.Length && _in.Count > 0)
        {
            var head = _in.Peek();
            var count = Math.Min(buffer.Length - copied, head.Length - _inOffset);
            head.AsSpan(_inOffset, count).CopyTo(buffer[copied..]);
            copied += count;
            _inOffset += count;
            if (_inOffset == head.Length)
            {
                _in.Dequeue();
                _inOffset = 0;
            }
        }

        _buffered -= copied;

        // reopen the window once the reader has made real room, so the server is not left waiting on a stale value
        if (_state == State.Established && WindowBytes() - _lastAdvertised >= ReceiveBuffer / 4)
        {
            SendAck();
        }

        return copied;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_error is not null)
            {
                throw new IOException(_error.Message, _error);
            }

            if (_state != State.Established || _finQueued)
            {
                throw new IOException("The connection is closed.");
            }

            if (_outLength + buffer.Length > _out.Length)
            {
                Array.Resize(ref _out, Math.Max(_out.Length * 2, _outLength + buffer.Length));
            }

            buffer.Span.CopyTo(_out.AsSpan(_outLength));
            _outLength += buffer.Length;
            TrySend();
        }

        return ValueTask.CompletedTask;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override bool CanRead => true;

    public override bool CanWrite => true;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_state == State.Established)
            {
                // a graceful close; anything the server still sends gets a reset, and a peer that never answers is dropped after the linger time
                _finQueued = true;
                TrySend();
                _lingerDeadline = Environment.TickCount64 + 30_000;
                FinishIfDone();
            }
            else if (_state == State.SynSent)
            {
                AbortLocked(new IOException("Closed."), sendReset: false);
            }
        }

        base.Dispose(disposing);
    }
}
