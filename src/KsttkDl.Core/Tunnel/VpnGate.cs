using System.Text;

namespace KsttkDl.Core.Tunnel;

/// <summary>one relay from the public VPN Gate list, with its OpenVPN config already decoded.</summary>
public sealed record VpnGateServer(
    string HostName,
    string Ip,
    long Score,
    long PingMs,
    long SpeedBps,
    string Country,
    string Config);

/// <summary> reads the VPN Gate relay list. the api serves csv wrapped in a <c>*vpn_servers</c> line and a closing <c>*</c>, with a <c>#HostName,...</c> header; the last column is the relay's .ovpn file in base64 </summary>
public static class VpnGate
{
    public const string ApiUrl = "https://www.vpngate.net/api/iphone/";

    private const int ColumnCount = 15;

    public static async Task<IReadOnlyList<VpnGateServer>> FetchAsync(HttpClient http, CancellationToken cancellationToken)
    {
        var text = await http.GetStringAsync(ApiUrl, cancellationToken);
        return Parse(text);
    }

    public static IReadOnlyList<VpnGateServer> Parse(string csv)
    {
        var servers = new List<VpnGateServer>();

        foreach (var line in csv.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0 || trimmed[0] is '#' or '*')
            {
                continue;
            }

            var fields = SplitCsvLine(trimmed);
            if (fields.Count != ColumnCount)
            {
                continue;
            }

            string config;
            try
            {
                config = Encoding.UTF8.GetString(Convert.FromBase64String(fields[14]));
            }
            catch (FormatException)
            {
                continue;
            }

            servers.Add(new VpnGateServer(
                fields[0], fields[1], ParseLong(fields[2]), ParseLong(fields[3]), ParseLong(fields[4]), fields[6], config));
        }

        return servers;
    }

    /// <summary> relays in <paramref name="country"/> that speak OpenVPN over tcp, volunteer relays first and then by score. the project's own public-vpn-* relays rank high on score but hold each session to a few hundred kbit/s and drop what goes over it, while volunteer relays carry several Mbit/s cleanly </summary>
    public static IReadOnlyList<VpnGateServer> TcpRelaysIn(IEnumerable<VpnGateServer> servers, string country) =>
        servers
            .Where(s => s.Country.Equals(country, StringComparison.OrdinalIgnoreCase))
            .Where(s => OvpnConfig.TryParse(s.Config, out var config) && config.Protocol == "tcp")
            .OrderBy(s => s.HostName.StartsWith("public-vpn-", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(s => s.Score)
            .ToList();

    private static long ParseLong(string value) => long.TryParse(value.Trim(), out var n) ? n : 0;

    // the operator and message columns are free text, so quoted fields with commas are possible
    private static List<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }
}
