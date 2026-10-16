using System.Security.Cryptography;
using System.Text;

namespace KsttkDl.Core.Tunnel;

/// <summary> data channel keys from OpenVPN's key method 2. both sides contribute randoms over the tls channel, the client also a pre-master secret, and the TLS 1.0 PRF (P_MD5 xor P_SHA1) stretches them into two key slots of 64 cipher bytes and 64 hmac bytes each </summary>
public sealed class OpenVpnKeys
{
    private const int SlotLength = 64;

    public required byte[] EncryptCipher { get; init; }

    public required byte[] EncryptHmac { get; init; }

    public required byte[] DecryptCipher { get; init; }

    public required byte[] DecryptHmac { get; init; }

    public static OpenVpnKeys Derive(
        byte[] preMaster,
        byte[] clientRandom1, byte[] serverRandom1,
        byte[] clientRandom2, byte[] serverRandom2,
        byte[] clientSessionId, byte[] serverSessionId)
    {
        var master = Prf(preMaster, "OpenVPN master secret", 48, clientRandom1, serverRandom1);
        var block = Prf(master, "OpenVPN key expansion", 4 * SlotLength,
            clientRandom2, serverRandom2, clientSessionId, serverSessionId);

        // slot 0 carries client-to-server traffic, slot 1 the reverse
        return new OpenVpnKeys
        {
            EncryptCipher = block[..SlotLength],
            EncryptHmac = block[SlotLength..(2 * SlotLength)],
            DecryptCipher = block[(2 * SlotLength)..(3 * SlotLength)],
            DecryptHmac = block[(3 * SlotLength)..],
        };
    }

    private static byte[] Prf(ReadOnlySpan<byte> secret, string label, int length, params byte[][] seeds)
    {
        var seed = new List<byte>(Encoding.ASCII.GetBytes(label));
        foreach (var part in seeds)
        {
            seed.AddRange(part);
        }

        var seedBytes = seed.ToArray();

        // the halves overlap by one byte when the secret length is odd
        var half = (secret.Length + 1) / 2;
        var md5 = PHash(HashAlgorithmName.MD5, secret[..half], seedBytes, length);
        var sha1 = PHash(HashAlgorithmName.SHA1, secret[(secret.Length - half)..], seedBytes, length);

        for (var i = 0; i < length; i++)
        {
            md5[i] ^= sha1[i];
        }

        return md5;
    }

    private static byte[] PHash(HashAlgorithmName algorithm, ReadOnlySpan<byte> secret, byte[] seed, int length)
    {
        using var hmac = IncrementalHash.CreateHMAC(algorithm, secret);
        var output = new byte[length];
        var a = seed;
        var written = 0;

        while (written < length)
        {
            hmac.AppendData(a);
            a = hmac.GetHashAndReset();

            hmac.AppendData(a);
            hmac.AppendData(seed);
            var chunk = hmac.GetHashAndReset();

            var take = Math.Min(chunk.Length, length - written);
            chunk.AsSpan(0, take).CopyTo(output.AsSpan(written));
            written += take;
        }

        return output;
    }
}
