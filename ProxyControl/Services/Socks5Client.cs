using ProxyControl.Models;
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ProxyControl.Services
{
    /// <summary>
    /// Helper class to handle SOCKS5 protocol details.
    /// </summary>
    public static class Socks5Client
    {
        // Connect to SOCKS5 proxy and negotiate handshake
        public static async Task ConnectAsync(TcpClient client, ProxyItem proxy, string targetHost, int targetPort, CancellationToken token)
        {
            if (!client.Connected)
                await client.ConnectAsync(proxy.IpAddress, proxy.Port, token);
            var stream = client.GetStream();

            // 1. Greeting (auth negotiation)
            // Send: VER(5) NMETHODS(2) METHODS(0x00=NoAuth, 0x02=UserPass)
            byte[] greeting = { 0x05, 0x02, 0x00, 0x02 };
            await stream.WriteAsync(greeting, 0, greeting.Length, token);

            byte[] response = new byte[2];
            await ReadExactAsync(stream, response, 2, token);

            if (response[0] != 0x05) throw new Exception("Invalid SOCKS5 version");
            byte authMethod = response[1];

            if (authMethod == 0x02) // Username/Password
            {
                await AuthenticateAsync(stream, proxy.Username, proxy.Password, token);
            }
            else if (authMethod == 0xFF)
            {
                throw new Exception("No acceptable authentication method");
            }
            // 0x00 = No Auth calling -> proceed

            // 2. Request details
            byte[] request = BuildRequest(0x01, targetHost, targetPort); // 0x01 = CONNECT
            await stream.WriteAsync(request, 0, request.Length, token);

            // 3. Read Reply
            byte[] replyHeader = new byte[4]; // VER, REP, RSV, ATYP
            await ReadExactAsync(stream, replyHeader, 4, token);

            if (replyHeader[1] != 0x00) // 0x00 = Succeeded
            {
                throw new Exception($"SOCKS5 Connect failed with code: {replyHeader[1]:X}");
            }

            // Skip BND.ADDR and BND.PORT
            await SkipAddressAsync(stream, replyHeader[3], token);
        }

        public static async Task<(TcpClient ControlClient, IPEndPoint UdpRelay)> UdpAssociateAsync(ProxyItem proxy, CancellationToken token)
        {
            TcpClient client = new TcpClient();
            try
            {
                await client.ConnectAsync(proxy.IpAddress, proxy.Port, token);
                var stream = client.GetStream();

                // 1. Greeting
                byte[] greeting = { 0x05, 0x02, 0x00, 0x02 };
                await stream.WriteAsync(greeting, 0, greeting.Length, token);

                byte[] response = new byte[2];
                await ReadExactAsync(stream, response, 2, token);

                if (response[0] != 0x05) throw new Exception("Invalid SOCKS5 version");

                if (response[1] == 0x02)
                {
                    await AuthenticateAsync(stream, proxy.Username, proxy.Password, token);
                }
                else if (response[1] == 0xFF)
                {
                    throw new Exception("SOCKS5 No acceptable auth");
                }

                // 2. UDP Associate Request
                // We send 0.0.0.0:0 to let proxy choose relay
                byte[] request = BuildRequest(0x03, "0.0.0.0", 0); // 0x03 = UDP ASSOCIATE
                await stream.WriteAsync(request, 0, request.Length, token);

                // 3. Reply
                byte[] replyHeader = new byte[4];
                await ReadExactAsync(stream, replyHeader, 4, token);

                if (replyHeader[1] != 0x00)
                {
                    throw new Exception($"SOCKS5 UDP Associate failed: {replyHeader[1]:X}");
                }

                // Read Relay Address
                IPEndPoint relayEndpoint = await ReadAddressAsync(stream, replyHeader[3], token);

                // IMPORTANT: The TCP connection (client) MUST prevent closing while UDP is active.
                return (client, relayEndpoint);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        // Pack UDP packet with SOCKS5 header: RSV(2) FRAG(1) ATYP(1) DST.ADDR DST.PORT DATA
        public static byte[] PackUdp(byte[] data, string targetHost, int targetPort)
        {
            // SOCKS5 UDP header needs destination address
            // RSV(2) | FRAG(1) | ATYP(1) | DST.ADDR | DST.PORT

            // Simplification: We will support IPv4 (0x01) or Domain (0x03)
            // For DNS (8.8.8.8), IPv4 is easier.
            // If targetHost is domain, use 0x03.

            List<byte> header = new List<byte>();
            header.Add(0x00); header.Add(0x00); // RSV
            header.Add(0x00); // FRAG (No fragmentation)

            if (IPAddress.TryParse(targetHost, out IPAddress ip))
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    header.Add(0x01); // IPv4
                    header.AddRange(ip.GetAddressBytes());
                }
                else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    header.Add(0x04); // IPv6
                    header.AddRange(ip.GetAddressBytes());
                }
            }
            else
            {
                header.Add(0x03); // Domain
                header.Add((byte)targetHost.Length);
                header.AddRange(Encoding.ASCII.GetBytes(targetHost));
            }

            // Port
            byte[] portBytes = BitConverter.GetBytes((ushort)targetPort);
            if (BitConverter.IsLittleEndian) Array.Reverse(portBytes);
            header.AddRange(portBytes);

            header.AddRange(data);
            return header.ToArray();
        }

        // Unpack UDP packet, return data (stripping header)
        public static byte[] UnpackUdp(byte[] packet)
        {
            // Header is at least 6 bytes (RSV(2)+FRAG(1)+ATYP(1)+...)
            if (packet.Length < 6) return Array.Empty<byte>();

            // Skip RSV(2) + FRAG(1) = 3 bytes
            int offset = 3;
            byte atyp = packet[offset];
            offset++;

            if (atyp == 0x01) // IPv4
            {
                offset += 4;
            }
            else if (atyp == 0x03) // Domain
            {
                byte len = packet[offset];
                offset += 1 + len;
            }
            else if (atyp == 0x04) // IPv6
            {
                offset += 16;
            }
            else return Array.Empty<byte>();

            // Port (2 bytes)
            offset += 2;

            if (offset >= packet.Length) return Array.Empty<byte>();

            int dataLen = packet.Length - offset;
            byte[] data = new byte[dataLen];
            Buffer.BlockCopy(packet, offset, data, 0, dataLen);
            return data;
        }

        private static async Task AuthenticateAsync(NetworkStream stream, string? user, string? pass, CancellationToken token)
        {
            user ??= "";
            pass ??= "";

            // Version 0x01
            // UL(1) User PL(1) Pass
            var userBytes = Encoding.ASCII.GetBytes(user);
            var passBytes = Encoding.ASCII.GetBytes(pass);

            byte[] authReq = new byte[1 + 1 + userBytes.Length + 1 + passBytes.Length];
            int idx = 0;
            authReq[idx++] = 0x01; // Sub-negotiation version
            authReq[idx++] = (byte)userBytes.Length;
            Buffer.BlockCopy(userBytes, 0, authReq, idx, userBytes.Length);
            idx += userBytes.Length;
            authReq[idx++] = (byte)passBytes.Length;
            Buffer.BlockCopy(passBytes, 0, authReq, idx, passBytes.Length);

            await stream.WriteAsync(authReq, 0, authReq.Length, token);

            byte[] response = new byte[2];
            await ReadExactAsync(stream, response, 2, token);

            if (response[1] != 0x00)
            {
                throw new Exception("SOCKS5 Authentication failed");
            }
        }

        private static byte[] BuildRequest(byte cmd, string host, int port)
        {
            // VER(1) CMD(1) RSV(1) ATYP(1) ...
            List<byte> bytes = new List<byte> { 0x05, cmd, 0x00 };

            if (IPAddress.TryParse(host, out IPAddress ip))
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    bytes.Add(0x01); // IPv4
                    bytes.AddRange(ip.GetAddressBytes());
                }
                else
                {
                    bytes.Add(0x04); // IPv6
                    bytes.AddRange(ip.GetAddressBytes());
                }
            }
            else
            {
                bytes.Add(0x03); // Domain
                bytes.Add((byte)host.Length);
                bytes.AddRange(Encoding.ASCII.GetBytes(host));
            }

            byte[] portBytes = BitConverter.GetBytes((ushort)port);
            if (BitConverter.IsLittleEndian) Array.Reverse(portBytes);
            bytes.AddRange(portBytes);

            return bytes.ToArray();
        }

        private static async Task SkipAddressAsync(NetworkStream stream, byte atyp, CancellationToken token)
        {
            if (atyp == 0x01) // IPv4
            {
                byte[] buf = new byte[4 + 2]; // IP + Port
                await ReadExactAsync(stream, buf, 6, token);
            }
            else if (atyp == 0x03) // Domain
            {
                byte[] lenBuf = new byte[1];
                await ReadExactAsync(stream, lenBuf, 1, token);
                int len = lenBuf[0];
                byte[] remainder = new byte[len + 2]; // Domain + Port
                await ReadExactAsync(stream, remainder, len + 2, token);
            }
            else if (atyp == 0x04) // IPv6
            {
                byte[] buf = new byte[16 + 2];
                await ReadExactAsync(stream, buf, 18, token);
            }
        }

        private static async Task<IPEndPoint> ReadAddressAsync(NetworkStream stream, byte atyp, CancellationToken token)
        {
            IPAddress ip;
            int port;

            if (atyp == 0x01) // IPv4
            {
                byte[] buf = new byte[4];
                await ReadExactAsync(stream, buf, 4, token);
                ip = new IPAddress(buf);
            }
            else if (atyp == 0x03) // Domain
            {
                byte[] lenBuf = new byte[1];
                await ReadExactAsync(stream, lenBuf, 1, token);
                int len = lenBuf[0];
                byte[] domBuf = new byte[len];
                await ReadExactAsync(stream, domBuf, len, token);
                // In UDP Associate reply, this is usually IP, but if domain, assume resolved?
                // Just try to parse or fail.
                string domain = Encoding.ASCII.GetString(domBuf);
                try
                {
                    var ips = await Dns.GetHostAddressesAsync(domain);
                    ip = ips[0];
                }
                catch
                {
                    ip = IPAddress.Any; // Fallback
                }
            }
            else if (atyp == 0x04) // IPv6
            {
                byte[] buf = new byte[16];
                await ReadExactAsync(stream, buf, 16, token);
                ip = new IPAddress(buf);
            }
            else throw new Exception("Unknown ATYP in SOCKS5 response");

            byte[] portBuf = new byte[2];
            await ReadExactAsync(stream, portBuf, 2, token);
            if (BitConverter.IsLittleEndian) Array.Reverse(portBuf);
            port = BitConverter.ToUInt16(portBuf, 0);

            return new IPEndPoint(ip, port);
        }

        private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, int length, CancellationToken token)
        {
            int total = 0;
            while (total < length)
            {
                int read = await stream.ReadAsync(buffer, total, length - total, token);
                if (read == 0) throw new Exception("Unexpected End of Stream in SOCKS5 handshake");
                total += read;
            }
        }

        public static async Task ConnectSocks4Async(TcpClient client, ProxyItem proxy, string targetHost, int targetPort, CancellationToken token)
        {
            if (!client.Connected)
                await client.ConnectAsync(proxy.IpAddress, proxy.Port, token);

            var stream = client.GetStream();

            // SOCKS4 Request
            // VN(1) | CD(1) | DSTPORT(2) | DSTIP(4) | USERID | NULL
            // VN=4, CD=1 (Connect)

            // Resolve target host if it's a domain, SOCKS4 supports only IPv4 (SOCKS4a supports domain but let's stick to SOCKS4 for now or try SOCKS4a logic if needed)
            // SOCKS4a uses 0.0.0.x IP and appends domain at end.
            // Let's implement standard SOCKS4 first which requires IP.

            IPAddress targetIp;
            if (!IPAddress.TryParse(targetHost, out targetIp))
            {
                // Try DNS resolve
                var ips = await Dns.GetHostAddressesAsync(targetHost, token);
                // Prefer IPv4
                targetIp = null;
                foreach (var ip in ips)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork)
                    {
                        targetIp = ip;
                        break;
                    }
                }
                if (targetIp == null) throw new Exception("Host resolution failed or no IPv4 address for SOCKS4");
            }

            byte[] ipBytes = targetIp.GetAddressBytes();
            byte[] portBytes = BitConverter.GetBytes((ushort)targetPort);
            if (BitConverter.IsLittleEndian) Array.Reverse(portBytes);

            string userId = proxy.Username ?? "";
            byte[] userBytes = Encoding.ASCII.GetBytes(userId);

            List<byte> request = new List<byte>();
            request.Add(0x04); // VN
            request.Add(0x01); // CD = Connect
            request.AddRange(portBytes);
            request.AddRange(ipBytes);
            request.AddRange(userBytes);
            request.Add(0x00); // Null terminator

            await stream.WriteAsync(request.ToArray(), 0, request.Count, token);

            // Read Reply
            // VN(1) | CD(1) | DSTPORT(2) | DSTIP(4)
            byte[] reply = new byte[8];
            await ReadExactAsync(stream, reply, 8, token);

            // Valid Reply: VN=0, CD=90 (Request granted)
            if (reply[1] != 90)
            {
                throw new Exception($"SOCKS4 Connect failed with code: {reply[1]}");
            }

            // Success
        }
    }
}
