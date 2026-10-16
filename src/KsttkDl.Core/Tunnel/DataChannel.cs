using System.Buffers.Binary;
using System.Security.Cryptography;

namespace KsttkDl.Core.Tunnel;

/// <summary> seals and opens OpenVPN data packets. two modes cover VPN Gate and stock servers: AES-CBC with an HMAC over iv and ciphertext (the packet id travels inside the ciphertext), and AES-GCM with the packet id as associated data and a nonce of packet id plus 8 implicit bytes taken from the hmac key slot </summary>
public sealed class DataChannel : IDisposable
{
    private const byte OpData1 = 6;
    private const byte OpData2 = 9;
    private const int GcmTagLength = 16;
    private const int CbcIvLength = 16;

    private readonly bool _gcm;
    private readonly byte _keyId;
    private readonly int? _peerId;

    private readonly Aes? _encryptCbc;
    private readonly Aes? _decryptCbc;
    private readonly byte[] _encryptHmacKey = [];
    private readonly byte[] _decryptHmacKey = [];
    private readonly HashAlgorithmName _hmacAlgorithm;
    private readonly int _hmacLength;

    private readonly AesGcm? _encryptGcm;
    private readonly AesGcm? _decryptGcm;
    private readonly byte[] _encryptImplicitIv = [];
    private readonly byte[] _decryptImplicitIv = [];

    private readonly object _sendLock = new();
    private uint _sendPacketId;

    public DataChannel(OpenVpnKeys keys, string cipher, string auth, byte keyId, int? peerId)
    {
        _keyId = keyId;
        _peerId = peerId;

        var keyLength = cipher switch
        {
            "AES-128-CBC" or "AES-128-GCM" => 16,
            "AES-192-CBC" or "AES-192-GCM" => 24,
            "AES-256-CBC" or "AES-256-GCM" => 32,
            _ => throw new NotSupportedException($"Cipher {cipher} is not supported."),
        };

        _gcm = cipher.EndsWith("-GCM", StringComparison.Ordinal);

        if (_gcm)
        {
            _encryptGcm = new AesGcm(keys.EncryptCipher.AsSpan(0, keyLength), GcmTagLength);
            _decryptGcm = new AesGcm(keys.DecryptCipher.AsSpan(0, keyLength), GcmTagLength);
            _encryptImplicitIv = keys.EncryptHmac[..8];
            _decryptImplicitIv = keys.DecryptHmac[..8];
            return;
        }

        (_hmacAlgorithm, _hmacLength) = auth switch
        {
            "SHA1" => (HashAlgorithmName.SHA1, 20),
            "SHA256" => (HashAlgorithmName.SHA256, 32),
            "SHA512" => (HashAlgorithmName.SHA512, 64),
            _ => throw new NotSupportedException($"HMAC {auth} is not supported."),
        };

        _encryptCbc = Aes.Create();
        _encryptCbc.Key = keys.EncryptCipher[..keyLength];
        _decryptCbc = Aes.Create();
        _decryptCbc.Key = keys.DecryptCipher[..keyLength];
        _encryptHmacKey = keys.EncryptHmac[.._hmacLength];
        _decryptHmacKey = keys.DecryptHmac[.._hmacLength];
    }

    public byte[] Seal(ReadOnlySpan<byte> payload)
    {
        uint packetId;
        lock (_sendLock)
        {
            packetId = ++_sendPacketId;
        }

        var header = Header();
        return _gcm ? SealGcm(header, packetId, payload) : SealCbc(header, packetId, payload);
    }

    /// <summary>returns the payload, or null when the packet fails authentication.</summary>
    public byte[]? Open(ReadOnlySpan<byte> packet)
    {
        var headerLength = (packet[0] >> 3) == OpData2 ? 4 : 1;
        return _gcm ? OpenGcm(packet, headerLength) : OpenCbc(packet[headerLength..]);
    }

    private byte[] Header()
    {
        if (_peerId is not { } peerId)
        {
            return [(byte)((OpData1 << 3) | _keyId)];
        }

        return [(byte)((OpData2 << 3) | _keyId), (byte)(peerId >> 16), (byte)(peerId >> 8), (byte)peerId];
    }

    private byte[] SealCbc(byte[] header, uint packetId, ReadOnlySpan<byte> payload)
    {
        var plain = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(plain, packetId);
        payload.CopyTo(plain.AsSpan(4));

        var iv = RandomNumberGenerator.GetBytes(CbcIvLength);
        var cipherText = _encryptCbc!.EncryptCbc(plain, iv);

        var packet = new byte[header.Length + _hmacLength + CbcIvLength + cipherText.Length];
        header.CopyTo(packet, 0);
        var ivAt = header.Length + _hmacLength;
        iv.CopyTo(packet, ivAt);
        cipherText.CopyTo(packet, ivAt + CbcIvLength);

        var mac = HmacOf(_encryptHmacKey, packet.AsSpan(ivAt));
        mac.CopyTo(packet, header.Length);
        return packet;
    }

    private byte[]? OpenCbc(ReadOnlySpan<byte> body)
    {
        if (body.Length < _hmacLength + CbcIvLength + 16)
        {
            return null;
        }

        var mac = HmacOf(_decryptHmacKey, body[_hmacLength..]);
        if (!CryptographicOperations.FixedTimeEquals(mac, body[.._hmacLength]))
        {
            return null;
        }

        var iv = body.Slice(_hmacLength, CbcIvLength);
        byte[] plain;
        try
        {
            plain = _decryptCbc!.DecryptCbc(body[(_hmacLength + CbcIvLength)..], iv);
        }
        catch (CryptographicException)
        {
            return null;
        }

        return plain.Length < 4 ? null : plain[4..];
    }

    private byte[] HmacOf(byte[] key, ReadOnlySpan<byte> data) => _hmacAlgorithm.Name switch
    {
        "SHA1" => HMACSHA1.HashData(key, data),
        "SHA256" => HMACSHA256.HashData(key, data),
        _ => HMACSHA512.HashData(key, data),
    };

    private byte[] SealGcm(byte[] header, uint packetId, ReadOnlySpan<byte> payload)
    {
        // layout: header | packet id | tag | ciphertext
        var packet = new byte[header.Length + 4 + GcmTagLength + payload.Length];
        header.CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(header.Length), packetId);

        var nonce = Nonce(packet.AsSpan(header.Length, 4), _encryptImplicitIv);
        var associated = packet.AsSpan(0, header.Length + 4);
        var aad = header.Length == 1 ? associated[1..] : associated;

        _encryptGcm!.Encrypt(nonce, payload,
            packet.AsSpan(header.Length + 4 + GcmTagLength),
            packet.AsSpan(header.Length + 4, GcmTagLength),
            aad);
        return packet;
    }

    private byte[]? OpenGcm(ReadOnlySpan<byte> packet, int headerLength)
    {
        if (packet.Length < headerLength + 4 + GcmTagLength)
        {
            return null;
        }

        var nonce = Nonce(packet.Slice(headerLength, 4), _decryptImplicitIv);

        // P_DATA_V1 authenticates only the packet id, P_DATA_V2 also the opcode and peer id in front of it
        var aad = headerLength == 1 ? packet.Slice(1, 4) : packet[..(headerLength + 4)];
        var cipherText = packet[(headerLength + 4 + GcmTagLength)..];
        var plain = new byte[cipherText.Length];

        try
        {
            _decryptGcm!.Decrypt(nonce, cipherText, packet.Slice(headerLength + 4, GcmTagLength), plain, aad);
        }
        catch (CryptographicException)
        {
            return null;
        }

        return plain;
    }

    private static byte[] Nonce(ReadOnlySpan<byte> packetId, byte[] implicitIv)
    {
        var nonce = new byte[12];
        packetId.CopyTo(nonce);
        implicitIv.CopyTo(nonce, 4);
        return nonce;
    }

    public void Dispose()
    {
        _encryptCbc?.Dispose();
        _decryptCbc?.Dispose();
        _encryptGcm?.Dispose();
        _decryptGcm?.Dispose();
    }
}
