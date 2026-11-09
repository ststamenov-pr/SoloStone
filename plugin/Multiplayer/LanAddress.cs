// SPDX-License-Identifier: AGPL-3.0-or-later
// Multiplayer never leaves the local network: every address it listens on or connects to must pass IsLocal.
// There is no server of ours, no relay and no matchmaking. A lobby is one player's game listening on their LAN,
// and the join code is nothing more than that player's local address written in a friendlier alphabet.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SoloStone.Multiplayer
{
    public static class LanAddress
    {
        public const int DefaultPort = 47625;

        /// <summary>
        /// True for addresses that cannot be the public internet: loopback, RFC 1918 private ranges, link-local,
        /// the carrier-grade NAT block that private mesh VPNs hand out (100.64.0.0/10), and IPv6 local ranges.
        /// </summary>
        public static bool IsLocal(IPAddress address)
        {
            if (address == null) return false;
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (IPAddress.IsLoopback(address)) return true;
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] b = address.GetAddressBytes();
                if (b[0] == 10) return true;
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
                if (b[0] == 192 && b[1] == 168) return true;
                if (b[0] == 169 && b[1] == 254) return true;
                if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;
                return false;
            }
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                byte[] b = address.GetAddressBytes();
                if ((b[0] & 0xFE) == 0xFC) return true; // unique local fc00::/7
                if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) return true; // link-local fe80::/10
            }
            return false;
        }

        /// <summary>This machine's local IPv4 addresses, most likely LAN address first.</summary>
        public static List<IPAddress> Mine()
        {
            var found = new List<IPAddress>();
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation info in nic.GetIPProperties().UnicastAddresses)
                        if (info.Address.AddressFamily == AddressFamily.InterNetwork && IsLocal(info.Address) && !found.Contains(info.Address))
                            found.Add(info.Address);
                }
            }
            catch (Exception) { /* no usable interface list: fall through to loopback */ }
            found.Sort((x, y) => Rank(x).CompareTo(Rank(y)));
            if (found.Count == 0) found.Add(IPAddress.Loopback);
            return found;
        }

        private static int Rank(IPAddress a)
        {
            byte[] b = a.GetAddressBytes();
            return b[0] == 192 ? 0 : b[0] == 10 ? 1 : b[0] == 172 ? 2 : b[0] == 100 ? 3 : 4;
        }
    }

    /// <summary>A lobby code: the host's local IPv4 address and port, ten characters, e.g. "K7QF2-M0A9X".</summary>
    public static class JoinCode
    {
        private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // no I, L, O, U

        public static string Encode(IPAddress address, int port)
        {
            if (address == null || address.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException("A join code needs an IPv4 address.");
            if (!LanAddress.IsLocal(address)) throw new ArgumentException("A join code can only name a local network address.");
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            byte[] b = address.GetAddressBytes();
            ulong value = ((ulong)b[0] << 40) | ((ulong)b[1] << 32) | ((ulong)b[2] << 24) | ((ulong)b[3] << 16) | (uint)port;
            // 48 bits of address and port, then 2 check bits, in ten 5-bit characters.
            value = (value << 2) | Check(value);
            var chars = new char[11];
            for (int i = 9, at = 10; i >= 0; i--)
            {
                chars[at--] = Alphabet[(int)(value & 31)];
                value >>= 5;
                if (i == 5) chars[at--] = '-';
            }
            return new string(chars);
        }

        public static bool TryDecode(string code, out IPEndPoint endPoint)
        {
            endPoint = null;
            if (code == null) return false;
            ulong value = 0;
            int count = 0;
            foreach (char raw in code.ToUpperInvariant())
            {
                if (raw == '-' || raw == ' ') continue;
                char c = raw == 'O' ? '0' : raw == 'I' || raw == 'L' ? '1' : raw == 'U' ? 'V' : raw;
                int digit = Alphabet.IndexOf(c);
                if (digit < 0 || ++count > 10) return false;
                value = (value << 5) | (uint)digit;
            }
            if (count != 10) return false;
            ulong check = value & 3;
            value >>= 2;
            if (Check(value) != check) return false;
            int port = (int)(value & 0xFFFF);
            var address = new IPAddress(new[] { (byte)(value >> 40), (byte)(value >> 32), (byte)(value >> 24), (byte)(value >> 16) });
            if (port == 0 || !LanAddress.IsLocal(address)) return false;
            endPoint = new IPEndPoint(address, port);
            return true;
        }

        private static ulong Check(ulong value)
        {
            ulong x = value ^ (value >> 7) ^ (value >> 19) ^ (value >> 31) ^ (value >> 43);
            return (x ^ (x >> 2) ^ (x >> 4)) & 3;
        }
    }
}
