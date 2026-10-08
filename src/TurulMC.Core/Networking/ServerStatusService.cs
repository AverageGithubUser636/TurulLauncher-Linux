using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TurulMC.Core.Models;
using TurulMC.Core.Logging;

namespace TurulMC.Core.Networking;

public class ServerStatusService : IServerStatusService
{
    public async Task<ServerStatus> CheckServerStatusAsync(string host, int port)
    {
        var status = new ServerStatus { IsOnline = false };

        try
        {
            var endpoint = await ResolveMinecraftEndpointAsync(host, port);

            var sw = Stopwatch.StartNew();
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            // Connect to the SRV target, but keep the original hostname in the
            // Minecraft handshake. This matches normal Minecraft SRV behavior.
            await tcp.ConnectAsync(endpoint.Host, endpoint.Port, cts.Token);
            var stream = tcp.GetStream();

            var handshake = BuildHandshakePacket(host, endpoint.Port, 1);
            var statusRequest = new byte[] { 0x01, 0x00 };

            await stream.WriteAsync(handshake, cts.Token);
            await stream.WriteAsync(statusRequest, cts.Token);

            _ = await ReadVarIntAsync(stream, cts.Token);
            var packetId = await ReadVarIntAsync(stream, cts.Token);
            if (packetId != 0x00)
                throw new InvalidOperationException($"Váratlan Minecraft status packet id: {packetId}");

            var jsonLength = await ReadVarIntAsync(stream, cts.Token);
            if (jsonLength <= 0 || jsonLength > 1024 * 1024)
                throw new InvalidOperationException("Érvénytelen Minecraft status JSON hossz.");

            var payload = new byte[jsonLength];
            await ReadExactlyAsync(stream, payload, cts.Token);

            sw.Stop();
            status.ResponseTimeMs = sw.ElapsedMilliseconds;

            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            status.IsOnline = true;

            if (root.TryGetProperty("players", out var players))
            {
                status.OnlinePlayers =
                    players.TryGetProperty("online", out var online) &&
                    online.TryGetInt32(out var onlineCount)
                        ? onlineCount
                        : null;

                status.MaxPlayers =
                    players.TryGetProperty("max", out var max) &&
                    max.TryGetInt32(out var maxCount)
                        ? maxCount
                        : null;
            }

            if (root.TryGetProperty("version", out var version) &&
                version.TryGetProperty("name", out var versionName))
            {
                status.Version = versionName.GetString();
            }

            if (root.TryGetProperty("description", out var description))
                status.Motd = ExtractDescription(description);

            if (endpoint.FromSrv)
            {
                LauncherLogger.Debug(
                    $"Minecraft SRV resolved: {host} -> {endpoint.Host}:{endpoint.Port}");
            }
        }
        catch (Exception ex)
        {
            status.ErrorMessage = ex.Message;
            LauncherLogger.Debug(
                $"Server check failed for {host}:{port} - {ex.Message}");
        }

        return status;
    }

    private static async Task<MinecraftEndpoint> ResolveMinecraftEndpointAsync(
        string host,
        int fallbackPort)
    {
        // Minecraft only applies _minecraft._tcp SRV lookup when the user did
        // not explicitly enter a custom port. The launcher calls this service
        // with 25565 for a plain hostname, so 25565 is treated as "default".
        if (fallbackPort != 25565 ||
            IPAddress.TryParse(host, out _) ||
            string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return new MinecraftEndpoint(host, fallbackPort, false);
        }

        try
        {
            var record = await MinecraftSrvResolver.ResolveAsync(host);
            if (record is not null)
            {
                return new MinecraftEndpoint(
                    record.Target.TrimEnd('.'),
                    record.Port,
                    true);
            }
        }
        catch (Exception ex)
        {
            // SRV is optional. A resolver failure must not make a normal
            // non-SRV Minecraft server appear offline.
            LauncherLogger.Debug(
                $"Minecraft SRV lookup failed for {host}: {ex.Message}");
        }

        return new MinecraftEndpoint(host, fallbackPort, false);
    }

    private static string ExtractDescription(JsonElement description)
    {
        if (description.ValueKind == JsonValueKind.String)
            return description.GetString() ?? "";

        if (description.ValueKind == JsonValueKind.Object)
        {
            var sb = new StringBuilder();

            if (description.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String)
            {
                sb.Append(text.GetString());
            }

            if (description.TryGetProperty("extra", out var extra) &&
                extra.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in extra.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        sb.Append(item.GetString());
                    }
                    else if (item.ValueKind == JsonValueKind.Object &&
                             item.TryGetProperty("text", out var extraText) &&
                             extraText.ValueKind == JsonValueKind.String)
                    {
                        sb.Append(extraText.GetString());
                    }
                }
            }

            return sb.ToString();
        }

        return "";
    }

    private static async Task<int> ReadVarIntAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var numRead = 0;
        var result = 0;
        byte read;

        do
        {
            var one = new byte[1];
            await ReadExactlyAsync(stream, one, cancellationToken);
            read = one[0];

            var value = read & 0b01111111;
            result |= value << (7 * numRead);

            numRead++;
            if (numRead > 5)
                throw new InvalidOperationException("A Minecraft VarInt túl hosszú.");
        }
        while ((read & 0b10000000) != 0);

        return result;
    }

    private static async Task ReadExactlyAsync(
        NetworkStream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer[offset..],
                cancellationToken);

            if (read == 0)
                throw new IOException(
                    "A Minecraft szerver lezárta a kapcsolatot.");

            offset += read;
        }
    }

    private static byte[] BuildHandshakePacket(
        string host,
        int port,
        int nextState)
    {
        var hostBytes = Encoding.UTF8.GetBytes(host);
        var packetData = new List<byte>();

        packetData.Add(0x00);
        WriteVarInt(packetData, -1);

        WriteVarInt(packetData, hostBytes.Length);
        packetData.AddRange(hostBytes);

        packetData.Add((byte)(port >> 8));
        packetData.Add((byte)(port & 0xFF));

        WriteVarInt(packetData, nextState);

        var frame = new List<byte>();
        WriteVarInt(frame, packetData.Count);
        frame.AddRange(packetData);

        return frame.ToArray();
    }

    private static void WriteVarInt(List<byte> list, int value)
    {
        uint unsigned = unchecked((uint)value);

        do
        {
            var temp = (byte)(unsigned & 0x7F);
            unsigned >>= 7;

            if (unsigned != 0)
                temp |= 0x80;

            list.Add(temp);
        }
        while (unsigned != 0);
    }

    private readonly record struct MinecraftEndpoint(
        string Host,
        int Port,
        bool FromSrv);
}

internal static class MinecraftSrvResolver
{
    private const ushort QueryTypeSrv = 33;
    private const ushort QueryClassInternet = 1;

    public static async Task<SrvRecord?> ResolveAsync(string host)
    {
        var queryName = $"_minecraft._tcp.{host.TrimEnd('.')}";
        var packet = BuildQuery(queryName);

        var dnsServers = GetSystemDnsServers().ToList();

        // Fallback DNS resolvers are only used if Windows does not expose any
        // DNS server for the active adapters.
        if (dnsServers.Count == 0)
        {
            dnsServers.Add(IPAddress.Parse("1.1.1.1"));
            dnsServers.Add(IPAddress.Parse("8.8.8.8"));
        }

        Exception? lastError = null;

        foreach (var dnsServer in dnsServers.Distinct())
        {
            try
            {
                using var udp = new UdpClient(
                    dnsServer.AddressFamily);

                using var cts =
                    new CancellationTokenSource(TimeSpan.FromSeconds(2));

                var endpoint = new IPEndPoint(dnsServer, 53);

                await udp.SendAsync(
                    packet,
                    endpoint,
                    cts.Token);

                var response = await udp.ReceiveAsync(cts.Token);

                var records = ParseSrvResponse(
                    response.Buffer);

                if (records.Count == 0)
                    return null;

                // RFC 2782: prefer the lowest priority. Within the same
                // priority Minecraft launchers typically accept any target.
                // We pick the highest weight deterministically to avoid a
                // random status endpoint changing every refresh.
                return records
                    .OrderBy(x => x.Priority)
                    .ThenByDescending(x => x.Weight)
                    .First();
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        if (lastError is not null)
            throw lastError;

        return null;
    }

    private static IEnumerable<IPAddress> GetSystemDnsServers()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;

            IPInterfaceProperties properties;
            try
            {
                properties = nic.GetIPProperties();
            }
            catch
            {
                continue;
            }

            foreach (var dns in properties.DnsAddresses)
            {
                if (dns.AddressFamily is
                    AddressFamily.InterNetwork or
                    AddressFamily.InterNetworkV6)
                {
                    yield return dns;
                }
            }
        }
    }

    private static byte[] BuildQuery(string domain)
    {
        var transactionId =
            (ushort)Random.Shared.Next(
                ushort.MinValue,
                ushort.MaxValue + 1);

        var bytes = new List<byte>
        {
            (byte)(transactionId >> 8),
            (byte)(transactionId & 0xFF),

            // Standard recursive query.
            0x01, 0x00,

            // QDCOUNT = 1
            0x00, 0x01,

            // ANCOUNT, NSCOUNT, ARCOUNT = 0
            0x00, 0x00,
            0x00, 0x00,
            0x00, 0x00
        };

        foreach (var label in domain.Split(
                     '.',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var labelBytes = Encoding.ASCII.GetBytes(label);

            if (labelBytes.Length is 0 or > 63)
                throw new InvalidOperationException(
                    "Érvénytelen DNS label.");

            bytes.Add((byte)labelBytes.Length);
            bytes.AddRange(labelBytes);
        }

        bytes.Add(0x00);

        bytes.Add((byte)(QueryTypeSrv >> 8));
        bytes.Add((byte)(QueryTypeSrv & 0xFF));

        bytes.Add((byte)(QueryClassInternet >> 8));
        bytes.Add((byte)(QueryClassInternet & 0xFF));

        return bytes.ToArray();
    }

    private static List<SrvRecord> ParseSrvResponse(
        byte[] data)
    {
        if (data.Length < 12)
            throw new InvalidOperationException(
                "Túl rövid DNS válasz.");

        var flags = ReadUInt16(data, 2);
        var responseCode = flags & 0x000F;

        if (responseCode == 3)
            return new List<SrvRecord>();

        if (responseCode != 0)
            throw new InvalidOperationException(
                $"DNS válasz hiba: rcode={responseCode}");

        var questionCount = ReadUInt16(data, 4);
        var answerCount = ReadUInt16(data, 6);

        var offset = 12;

        for (var i = 0; i < questionCount; i++)
        {
            _ = ReadName(data, ref offset);

            if (offset + 4 > data.Length)
                throw new InvalidOperationException(
                    "Hibás DNS question.");

            offset += 4;
        }

        var result = new List<SrvRecord>();

        for (var i = 0; i < answerCount; i++)
        {
            _ = ReadName(data, ref offset);

            if (offset + 10 > data.Length)
                throw new InvalidOperationException(
                    "Hibás DNS resource record.");

            var type = ReadUInt16(data, offset);
            offset += 2;

            var recordClass = ReadUInt16(data, offset);
            offset += 2;

            // TTL
            offset += 4;

            var dataLength = ReadUInt16(data, offset);
            offset += 2;

            if (offset + dataLength > data.Length)
                throw new InvalidOperationException(
                    "Hibás DNS RDATA hossz.");

            var rdataEnd = offset + dataLength;

            if (type == QueryTypeSrv &&
                recordClass == QueryClassInternet &&
                dataLength >= 7)
            {
                var priority = ReadUInt16(data, offset);
                offset += 2;

                var weight = ReadUInt16(data, offset);
                offset += 2;

                var port = ReadUInt16(data, offset);
                offset += 2;

                var targetOffset = offset;
                var target = ReadName(
                    data,
                    ref targetOffset);

                // A single "." target explicitly means service unavailable.
                if (!string.IsNullOrWhiteSpace(target) &&
                    target != ".")
                {
                    result.Add(new SrvRecord(
                        priority,
                        weight,
                        port,
                        target));
                }
            }

            offset = rdataEnd;
        }

        return result;
    }

    private static string ReadName(
        byte[] data,
        ref int offset)
    {
        var labels = new List<string>();
        var position = offset;
        var jumped = false;
        var jumpGuard = 0;

        while (true)
        {
            if (position >= data.Length)
                throw new InvalidOperationException(
                    "Hibás DNS név.");

            if (++jumpGuard > 128)
                throw new InvalidOperationException(
                    "DNS compression loop.");

            var length = data[position++];

            if (length == 0)
            {
                if (!jumped)
                    offset = position;

                break;
            }

            // Compressed DNS pointer.
            if ((length & 0xC0) == 0xC0)
            {
                if (position >= data.Length)
                    throw new InvalidOperationException(
                        "Hibás DNS pointer.");

                var pointer =
                    ((length & 0x3F) << 8) |
                    data[position++];

                if (!jumped)
                {
                    offset = position;
                    jumped = true;
                }

                position = pointer;
                continue;
            }

            if ((length & 0xC0) != 0)
                throw new InvalidOperationException(
                    "Ismeretlen DNS label formátum.");

            if (position + length > data.Length)
                throw new InvalidOperationException(
                    "Hibás DNS label hossz.");

            labels.Add(
                Encoding.ASCII.GetString(
                    data,
                    position,
                    length));

            position += length;
        }

        return string.Join('.', labels);
    }

    private static ushort ReadUInt16(
        byte[] data,
        int offset)
    {
        if (offset + 2 > data.Length)
            throw new InvalidOperationException(
                "Hibás DNS integer.");

        return BinaryPrimitives.ReadUInt16BigEndian(
            data.AsSpan(offset, 2));
    }
}

internal sealed record SrvRecord(
    ushort Priority,
    ushort Weight,
    ushort Port,
    string Target);
