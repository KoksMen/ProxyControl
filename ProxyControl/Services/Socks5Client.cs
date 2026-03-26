using ProxyControl.Models;
using System;
using System.Collections.Generic;
using System.Linq;
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
        private const byte SocksVersion = 0x05;
        private const byte CmdConnect = 0x01;
        private const byte CmdUdpAssociate = 0x03;
        private const byte AuthNoAuth = 0x00;
        private const byte AuthUserPass = 0x02;
        private const byte AuthNoAcceptable = 0xFF;

        // Connect to SOCKS5 proxy and negotiate handshake
        public static async Task ConnectAsync(TcpClient client, ProxyItem proxy, string targetHost, int targetPort, CancellationToken token)
        {
            if (!client.Connected)
                await client.ConnectAsync(proxy.IpAddress, proxy.Port, token);

            ConfigureSocket(client);
            var stream = client.GetStream();

            // 1. Greeting (auth negotiation)
            byte[] greeting = BuildGreeting(proxy.Username, proxy.Password);
            await stream.WriteAsync(greeting, 0, greeting.Length, token);

            byte[] response = new byte[2];
            await ReadExactAsync(stream, response, 2, token);

            if (response[0] != SocksVersion) throw new Exception("Invalid SOCKS5 version");
            byte authMethod = response[1];

            if (authMethod == AuthUserPass) // Username/Password
            {
                await AuthenticateAsync(stream, proxy.Username, proxy.Password, token);
            }
            else if (authMethod == AuthNoAcceptable)
            {
                throw new Exception("No acceptable authentication method");
            }
            else if (authMethod != AuthNoAuth)
            {
                throw new Exception($"Unsupported SOCKS5 auth method: {authMethod:X2}");
            }

            // 2. Connect Request
            byte[] request = BuildRequest(CmdConnect, targetHost, targetPort);
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
                ConfigureSocket(client);
                var stream = client.GetStream();

                // 1. Greeting
                byte[] greeting = BuildGreeting(proxy.Username, proxy.Password);
                await stream.WriteAsync(greeting, 0, greeting.Length, token);

                byte[] response = new byte[2];
                await ReadExactAsync(stream, response, 2, token);

                if (response[0] != SocksVersion) throw new Exception("Invalid SOCKS5 version");

                if (response[1] == AuthUserPass)
                {
                    await AuthenticateAsync(stream, proxy.Username, proxy.Password, token);
                }
                else if (response[1] == AuthNoAcceptable)
                {
                    throw new Exception("SOCKS5 No acceptable auth");
                }
                else if (response[1] != AuthNoAuth)
                {
                    throw new Exception($"Unsupported SOCKS5 auth method: {response[1]:X2}");
                }

                // 2. UDP Associate Request (0.0.0.0:0 lets proxy choose relay)
                byte[] request = BuildRequest(CmdUdpAssociate, "0.0.0.0", 0);
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

                // IMPORTANT: Keep TCP control connection open while UDP is active.
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
            List<byte> header = new List<byte>(32);
            header.Add(0x00); // RSV
            header.Add(0x00); // RSV
            header.Add(0x00); // FRAG (No fragmentation)

            if (IPAddress.TryParse(targetHost, out IPAddress? ip))
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    header.Add(0x01); // IPv4
                    header.AddRange(ip.GetAddressBytes());
                }
                else
                {
                    header.Add(0x04); // IPv6
                    header.AddRange(ip.GetAddressBytes());
                }
            }
            else
            {
                if (targetHost.Length > byte.MaxValue)
                    throw new ArgumentException("SOCKS5 domain name is too long.", nameof(targetHost));

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
            // Need at least RSV(2) + FRAG(1) + ATYP(1) + PORT(2)
            if (packet.Length < 6) return Array.Empty<byte>();
            if (packet[0] != 0x00 || packet[1] != 0x00) return Array.Empty<byte>();
            if (packet[2] != 0x00) return Array.Empty<byte>(); // Fragmentation unsupported

            int offset = 3;
            byte atyp = packet[offset++];

            if (atyp == 0x01) // IPv4
            {
                if (packet.Length < offset + 4 + 2) return Array.Empty<byte>();
                offset += 4;
            }
            else if (atyp == 0x03) // Domain
            {
                if (packet.Length < offset + 1) return Array.Empty<byte>();
                int len = packet[offset];
                offset++;
                if (packet.Length < offset + len + 2) return Array.Empty<byte>();
                offset += len;
            }
            else if (atyp == 0x04) // IPv6
            {
                if (packet.Length < offset + 16 + 2) return Array.Empty<byte>();
                offset += 16;
            }
            else return Array.Empty<byte>();

            // Port
            offset += 2;

            if (offset > packet.Length) return Array.Empty<byte>();
            if (offset == packet.Length) return Array.Empty<byte>();

            int dataLen = packet.Length - offset;
            byte[] data = new byte[dataLen];
            Buffer.BlockCopy(packet, offset, data, 0, dataLen);
            return data;
        }

        private static async Task AuthenticateAsync(NetworkStream stream, string? user, string? pass, CancellationToken token)
        {
            user ??= "";
            pass ??= "";

            if (user.Length > byte.MaxValue || pass.Length > byte.MaxValue)
                throw new Exception("SOCKS5 username/password length exceeds 255 bytes.");

            var userBytes = Encoding.ASCII.GetBytes(user);
            var passBytes = Encoding.ASCII.GetBytes(pass);

            // Version 0x01, UL(1), USER, PL(1), PASS
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
            List<byte> bytes = new List<byte>(32) { SocksVersion, cmd, 0x00 };

            if (IPAddress.TryParse(host, out IPAddress? ip))
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
                if (host.Length > byte.MaxValue)
                    throw new ArgumentException("SOCKS5 domain name is too long.", nameof(host));

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
            else
            {
                throw new Exception("Unknown ATYP in SOCKS5 response");
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

                string domain = Encoding.ASCII.GetString(domBuf);
                try
                {
                    var ips = await Dns.GetHostAddressesAsync(domain, token);
                    ip = ips.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                         ?? ips.FirstOrDefault()
                         ?? IPAddress.Any;
                }
                catch
                {
                    ip = IPAddress.Any;
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

        private static byte[] BuildGreeting(string? user, string? pass)
        {
            bool hasCredentials = !string.IsNullOrWhiteSpace(user) || !string.IsNullOrWhiteSpace(pass);
            return hasCredentials
                ? new byte[] { SocksVersion, 0x02, AuthNoAuth, AuthUserPass }
                : new byte[] { SocksVersion, 0x01, AuthNoAuth };
        }

        private static void ConfigureSocket(TcpClient client)
        {
            try
            {
                client.NoDelay = true;
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            }
            catch
            {
                // Best-effort tuning only.
            }
        }
    }
}
