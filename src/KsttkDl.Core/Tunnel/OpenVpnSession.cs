using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace KsttkDl.Core.Tunnel;

/// <summary> a client OpenVPN session over tcp, carried entirely in this process. it brings up the control channel, runs tls over it, swaps key method 2 material, asks for the pushed options, and then moves raw ip packets in and out of the data channel. no tun device or admin rights are involved: whoever owns the session decides what the packets mean </summary>
public sealed class OpenVpnSession : IAsyncDisposable
{
    private const byte OpData1 = 6;
    private const byte OpData2 = 9;

    // OpenVPN's keepalive, sent as an ordinary data packet
    private static readonly byte[] PingPayload =
    [
        0x2a, 0x18, 0x7b, 0xf3, 0x64, 0x1e, 0xb4, 0xcb,
        0x07, 0xed, 0x2d, 0x0a, 0x98, 0x1f, 0xc7, 0x48,
    ];

    private readonly OvpnConfig _config;
    private readonly Action<byte[]> _onPacket;
    private readonly Action<string> _log;
    private readonly TcpClient _tcp = new() { NoDelay = true };
    private readonly object _writeLock = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private NetworkStream _link = null!;
    private ControlChannel _control = null!;
    private SslStream? _tls;
    private DataChannel? _data;
    private int _badPackets;

    private OpenVpnSession(OvpnConfig config, Action<byte[]> onPacket, Action<string> log)
    {
        _config = config;
        _onPacket = onPacket;
        _log = log;
    }

    /// <summary>the tunnel address the server assigned.</summary>
    public IPAddress LocalAddress { get; private set; } = IPAddress.None;

    public string RemoteEndPoint => $"{_config.Host}:{_config.Port}";

    /// <summary>completes when the link drops, faulted with the reason.</summary>
    public Task Closed => _closed.Task;

    public static async Task<OpenVpnSession> ConnectAsync(
        OvpnConfig config, Action<byte[]> onPacket, Action<string> log, CancellationToken cancellationToken)
    {
        if (config.Protocol != "tcp")
        {
            throw new NotSupportedException("Only OpenVPN over TCP is supported.");
        }

        var session = new OpenVpnSession(config, onPacket, log);
        try
        {
            await session.HandshakeAsync(cancellationToken);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    /// <summary>sends one ip packet through the tunnel.</summary>
    public void Send(ReadOnlySpan<byte> ipPacket)
    {
        var data = _data ?? throw new InvalidOperationException("The tunnel is not up yet.");
        WritePacket(data.Seal(ipPacket));
    }

    private async Task HandshakeAsync(CancellationToken cancellationToken)
    {
        await _tcp.ConnectAsync(_config.Host, _config.Port, cancellationToken);
        _link = _tcp.GetStream();
        _control = new ControlChannel(WritePacket);
        _ = Task.Run(ReadLoopAsync, CancellationToken.None);

        _control.SendHardReset();
        await _control.ResetDone.WaitAsync(cancellationToken);

        _tls = new SslStream(_control.Stream, leaveInnerStreamOpen: true);
        await _tls.AuthenticateAsClientAsync(TlsOptions(), cancellationToken);

        // key method 2: the client sends its pre-master and two randoms with its options, the server answers with its own two randoms
        var preMaster = RandomNumberGenerator.GetBytes(48);
        var clientRandom1 = RandomNumberGenerator.GetBytes(32);
        var clientRandom2 = RandomNumberGenerator.GetBytes(32);

        var hello = new MemoryStream();
        hello.Write([0, 0, 0, 0, 2]);
        hello.Write(preMaster);
        hello.Write(clientRandom1);
        hello.Write(clientRandom2);
        WriteString(hello, OptionsString());
        WriteString(hello, ""); // username
        WriteString(hello, ""); // password
        WriteString(hello, PeerInfo());
        await _tls.WriteAsync(hello.ToArray(), cancellationToken);

        var head = new byte[5 + 64 + 2];
        await _tls.ReadExactlyAsync(head, cancellationToken);
        if (head[4] != 2)
        {
            throw new IOException($"The server answered with key method {head[4]}.");
        }

        var serverRandom1 = head[5..37];
        var serverRandom2 = head[37..69];
        var optionsLength = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(69));
        await _tls.ReadExactlyAsync(new byte[optionsLength], cancellationToken);

        var keys = OpenVpnKeys.Derive(
            preMaster, clientRandom1, serverRandom1, clientRandom2, serverRandom2,
            _control.LocalSessionId, _control.RemoteSessionId);

        var options = ParsePush(await RequestPushAsync(cancellationToken));
        LocalAddress = options.Address;
        _data = new DataChannel(keys, options.Cipher ?? _config.Cipher, _config.Auth, keyId: 0, options.PeerId);

        _ = Task.Run(() => KeepaliveAsync(options.PingSeconds), CancellationToken.None);
    }

    private SslClientAuthenticationOptions TlsOptions()
    {
        var authorities = new X509Certificate2Collection();
        if (_config.CaPem.Length > 0)
        {
            authorities.ImportFromPem(_config.CaPem);
        }

        var options = new SslClientAuthenticationOptions
        {
            // OpenVPN does not match the certificate against a hostname; trust comes from the config's <ca> alone
            TargetHost = "",
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = (_, certificate, chain, _) =>
                certificate is not null && IsTrusted(new X509Certificate2(certificate), chain, authorities),
        };

        if (_config.CertPem.Length > 0 && _config.KeyPem.Length > 0)
        {
            using var pem = X509Certificate2.CreateFromPem(_config.CertPem, _config.KeyPem);

            // schannel cannot use an ephemeral pem key, so it goes through a pfx round trip
            var certificate = new X509Certificate2(pem.Export(X509ContentType.Pfx));
            options.ClientCertificates = [certificate];
            options.LocalCertificateSelectionCallback = (_, _, _, _, _) => certificate;
        }

        return options;
    }

    private static bool IsTrusted(X509Certificate2 leaf, X509Chain? presented, X509Certificate2Collection authorities)
    {
        if (authorities.Count == 0)
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(authorities);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        // the intermediates the server sent
        if (presented is not null)
        {
            chain.ChainPolicy.ExtraStore.AddRange(presented.ChainPolicy.ExtraStore);
            foreach (var element in presented.ChainElements)
            {
                chain.ChainPolicy.ExtraStore.Add(element.Certificate);
            }
        }

        return chain.Build(leaf);
    }

    private string OptionsString() =>
        $"V4,dev-type tun,link-mtu 1559,tun-mtu 1500,proto TCPv4_CLIENT,cipher {_config.Cipher},auth {_config.Auth}," +
        $"keysize {KeyBits(_config.Cipher)},key-method 2,tls-client";

    private string PeerInfo()
    {
        // IV_PROTO=2 offers P_DATA_V2 with a peer id; key derivation stays on the PRF since tls exporters are out of reach here
        var ciphers = new[] { "AES-128-GCM", "AES-256-GCM", _config.Cipher }.Distinct();
        return "IV_VER=2.6.12\nIV_PLAT=win\nIV_PROTO=2\nIV_NCP=2\nIV_TCPNL=1\n" +
               $"IV_CIPHERS={string.Join(':', ciphers)}\n";
    }

    private static int KeyBits(string cipher) =>
        cipher.Contains("256", StringComparison.Ordinal) ? 256 :
        cipher.Contains("192", StringComparison.Ordinal) ? 192 : 128;

    private static void WriteString(Stream stream, string value)
    {
        if (value.Length == 0)
        {
            stream.Write([0, 0]);
            return;
        }

        var bytes = Encoding.ASCII.GetBytes(value + "\0");
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)bytes.Length);
        stream.Write(length);
        stream.Write(bytes);
    }

    /// <summary> sends PUSH_REQUEST until the server answers. control messages are nul-terminated text on the tls stream, and the server may put other bytes (peer info, empty strings) in front, so the reply is searched for rather than read at a fixed offset </summary>
    private async Task<string> RequestPushAsync(CancellationToken cancellationToken)
    {
        var request = Encoding.ASCII.GetBytes("PUSH_REQUEST\0");
        var received = new StringBuilder();
        var buffer = new byte[4096];

        await _tls!.WriteAsync(request, cancellationToken);
        var read = _tls.ReadAsync(buffer, cancellationToken).AsTask();

        while (true)
        {
            var finished = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(2), cancellationToken));
            if (finished != read)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _tls.WriteAsync(request, cancellationToken);
                continue;
            }

            var count = await read;
            if (count == 0)
            {
                throw new IOException("The server closed the control channel.");
            }

            received.Append(Encoding.Latin1.GetString(buffer, 0, count));
            foreach (var message in received.ToString().Split('\0'))
            {
                var at = message.IndexOf("PUSH_REPLY", StringComparison.Ordinal);
                if (at >= 0 && received.ToString().Contains(message + "\0", StringComparison.Ordinal))
                {
                    return message[at..];
                }

                if (message.Contains("AUTH_FAILED", StringComparison.Ordinal))
                {
                    throw new IOException("The server rejected the login (AUTH_FAILED).");
                }
            }

            read = _tls.ReadAsync(buffer, cancellationToken).AsTask();
        }
    }

    private sealed record PushedOptions(IPAddress Address, string? Cipher, int? PeerId, int PingSeconds);

    private static PushedOptions ParsePush(string reply)
    {
        IPAddress? address = null;
        string? cipher = null;
        int? peerId = null;
        var ping = 10;

        foreach (var option in reply.Split(','))
        {
            var parts = option.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            switch (parts[0])
            {
                case "ifconfig" when IPAddress.TryParse(parts[1], out var local):
                    address = local;
                    break;
                case "cipher":
                    cipher = parts[1].ToUpperInvariant();
                    break;
                case "peer-id" when int.TryParse(parts[1], out var id):
                    peerId = id;
                    break;
                case "ping" when int.TryParse(parts[1], out var seconds) && seconds > 0:
                    ping = seconds;
                    break;
            }
        }

        return new PushedOptions(
            address ?? throw new IOException($"The server pushed no address: {reply}"), cipher, peerId, ping);
    }

    private async Task KeepaliveAsync(int seconds)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                Send(PingPayload);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    // tcp framing: each packet is preceded by its length as a big-endian uint16
    private void WritePacket(byte[] packet)
    {
        var framed = new byte[2 + packet.Length];
        BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)packet.Length);
        packet.CopyTo(framed, 2);

        try
        {
            lock (_writeLock)
            {
                _link.Write(framed);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            Fail(new IOException("The VPN link is down.", ex));
            throw new IOException("The VPN link is down.", ex);
        }
    }

    private async Task ReadLoopAsync()
    {
        var header = new byte[2];
        var buffer = new byte[65536];

        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _link.ReadExactlyAsync(header, _stop.Token);
                var length = BinaryPrimitives.ReadUInt16BigEndian(header);
                await _link.ReadExactlyAsync(buffer.AsMemory(0, length), _stop.Token);

                if (length == 0)
                {
                    continue;
                }

                Dispatch(buffer.AsSpan(0, length));
            }

            Fail(new OperationCanceledException("The session was closed."));
        }
        catch (Exception ex)
        {
            Fail(ex is EndOfStreamException ? new IOException("The server closed the connection.", ex) : ex);
        }
    }

    private void Dispatch(ReadOnlySpan<byte> packet)
    {
        var opcode = (byte)(packet[0] >> 3);
        if (opcode is OpData1 or OpData2)
        {
            HandleData(packet);
        }
        else
        {
            _control.Receive(opcode, packet);
        }
    }

    private void HandleData(ReadOnlySpan<byte> packet)
    {
        // the server can start sending before the push reply has been handled
        if (_data is null)
        {
            return;
        }

        var payload = _data.Open(packet);
        if (payload is null)
        {
            if (Interlocked.Increment(ref _badPackets) == 1)
            {
                _log("  dropped a data packet that failed authentication");
            }

            return;
        }

        if (payload.Length == 0 || payload.AsSpan().SequenceEqual(PingPayload))
        {
            return;
        }

        _onPacket(payload);
    }

    private void Fail(Exception error)
    {
        if (_closed.TrySetException(error))
        {
            _control?.Fail(error);
            _stop.Cancel();
            _tcp.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Fail(new OperationCanceledException("The session was closed."));
        if (_tls is not null)
        {
            await _tls.DisposeAsync();
        }

        _data?.Dispose();
        _closed.Task.Exception?.Handle(_ => true);
    }
}
