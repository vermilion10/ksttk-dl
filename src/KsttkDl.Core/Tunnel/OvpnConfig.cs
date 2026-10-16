namespace KsttkDl.Core.Tunnel;

/// <summary> the handful of .ovpn directives the client acts on. everything else in a VPN Gate config (dev, nobind, persist-*, verb, ...) only matters to a system tunnel and is ignored </summary>
public sealed record OvpnConfig
{
    public required string Host { get; init; }

    public required int Port { get; init; }

    /// <summary>"tcp" or "udp".</summary>
    public required string Protocol { get; init; }

    public string Cipher { get; init; } = "AES-128-CBC";

    public string Auth { get; init; } = "SHA1";

    public string CaPem { get; init; } = "";

    public string CertPem { get; init; } = "";

    public string KeyPem { get; init; } = "";

    public static bool TryParse(string text, out OvpnConfig config)
    {
        try
        {
            config = Parse(text);
            return true;
        }
        catch (FormatException)
        {
            config = null!;
            return false;
        }
    }

    public static OvpnConfig Parse(string text)
    {
        string? host = null;
        var port = 1194;
        var protocol = "udp";
        var cipher = "AES-128-CBC";
        var auth = "SHA1";
        var blocks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var lines = text.Replace("\r", "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            // inline files: <ca> ... </ca>
            if (line.StartsWith('<') && line.EndsWith('>') && !line.StartsWith("</", StringComparison.Ordinal))
            {
                var name = line[1..^1];
                var body = new List<string>();
                for (i++; i < lines.Length && lines[i].Trim() != $"</{name}>"; i++)
                {
                    body.Add(lines[i]);
                }

                blocks[name] = string.Join('\n', body).Trim();
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            switch (parts[0].ToLowerInvariant())
            {
                case "remote" when parts.Length >= 2:
                    host = parts[1];
                    if (parts.Length >= 3 && int.TryParse(parts[2], out var p))
                    {
                        port = p;
                    }

                    if (parts.Length >= 4)
                    {
                        protocol = NormalizeProtocol(parts[3]);
                    }

                    break;
                case "proto" when parts.Length >= 2:
                    protocol = NormalizeProtocol(parts[1]);
                    break;
                case "port" when parts.Length >= 2 && int.TryParse(parts[1], out var listed):
                    port = listed;
                    break;
                case "cipher" when parts.Length >= 2:
                    cipher = parts[1].ToUpperInvariant();
                    break;
                case "auth" when parts.Length >= 2:
                    auth = parts[1].ToUpperInvariant();
                    break;
            }
        }

        if (host is null)
        {
            throw new FormatException("The config has no 'remote' line.");
        }

        return new OvpnConfig
        {
            Host = host,
            Port = port,
            Protocol = protocol,
            Cipher = cipher,
            Auth = auth,
            CaPem = blocks.GetValueOrDefault("ca", ""),
            CertPem = blocks.GetValueOrDefault("cert", ""),
            KeyPem = blocks.GetValueOrDefault("key", ""),
        };
    }

    // tcp-client, tcp4, udp6 and so on all reduce to the transport
    private static string NormalizeProtocol(string value) =>
        value.StartsWith("tcp", StringComparison.OrdinalIgnoreCase) ? "tcp" : "udp";
}
