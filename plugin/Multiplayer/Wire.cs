// SPDX-License-Identifier: AGPL-3.0-or-later
// The wire format: one message per line, fields separated by tabs. Small enough to read in a packet capture,
// and it needs no serializer the game's runtime might lack.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SoloStone.Multiplayer
{
    public static class Wire
    {
        public const int MaxLine = 64 * 1024;
        public const int Version = 1;

        public static string Encode(IList<string> fields)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < fields.Count; i++)
            {
                if (i > 0) sb.Append('\t');
                foreach (char c in fields[i] ?? "")
                {
                    if (c == '\\') sb.Append("\\\\");
                    else if (c == '\t') sb.Append("\\t");
                    else if (c == '\n') sb.Append("\\n");
                    else if (c == '\r') continue;
                    else sb.Append(c);
                }
            }
            return sb.ToString();
        }

        public static string[] Decode(string line)
        {
            var fields = new List<string>();
            var sb = new StringBuilder();
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '\t') { fields.Add(sb.ToString()); sb.Length = 0; }
                else if (c == '\\' && i + 1 < line.Length)
                {
                    char n = line[++i];
                    sb.Append(n == 't' ? '\t' : n == 'n' ? '\n' : n);
                }
                else sb.Append(c);
            }
            fields.Add(sb.ToString());
            return fields.ToArray();
        }

        /// <summary>Names and chat lines: no control or invisible characters, single spaces, a length limit.</summary>
        public static string Clean(string text, int max)
        {
            if (text == null) return "";
            var sb = new StringBuilder();
            bool space = false;
            foreach (char c in text)
            {
                bool invisible = char.IsControl(c) || c == (char)0x00AD || (c >= (char)0x200B && c <= (char)0x200F)
                    || (c >= (char)0x2028 && c <= (char)0x202F) || (c >= (char)0x2060 && c <= (char)0x206F) || c == (char)0xFEFF;
                if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
                if (invisible) continue;
                if (space)
                {
                    if (sb.Length + 2 > max) break;
                    sb.Append(' ');
                    space = false;
                }
                sb.Append(c);
                if (sb.Length >= max) break;
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// One TCP connection. Reading happens on a background thread and lands in <see cref="Inbox"/>; the owner
    /// drains it from its own thread (in the game, the main thread), so nothing here ever calls back into game code.
    /// </summary>
    public sealed class Link : IDisposable
    {
        private readonly TcpClient client;
        private readonly NetworkStream stream;
        private readonly object writeLock = new object();
        private volatile bool closed;
        public readonly ConcurrentQueue<string[]> Inbox = new ConcurrentQueue<string[]>();
        public IPAddress Remote { get; }
        public bool Closed => closed;

        public Link(TcpClient client)
        {
            this.client = client;
            client.NoDelay = true;
            stream = client.GetStream();
            Remote = ((IPEndPoint)client.Client.RemoteEndPoint).Address;
            var reader = new Thread(ReadLoop) { IsBackground = true, Name = "SoloStone LAN link" };
            reader.Start();
        }

        private void ReadLoop()
        {
            try
            {
                var line = new MemoryStream();
                var buffer = new byte[4096];
                while (!closed)
                {
                    int n = stream.Read(buffer, 0, buffer.Length);
                    if (n <= 0) break;
                    for (int i = 0; i < n; i++)
                    {
                        if (buffer[i] == (byte)'\n')
                        {
                            Inbox.Enqueue(Wire.Decode(Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length)));
                            line.SetLength(0);
                        }
                        else
                        {
                            line.WriteByte(buffer[i]);
                            if (line.Length > Wire.MaxLine) throw new IOException("Line too long.");
                        }
                    }
                }
            }
            catch (Exception) { /* a dropped connection ends the loop */ }
            closed = true;
        }

        public void Send(params string[] fields)
        {
            if (closed) return;
            byte[] bytes = Encoding.UTF8.GetBytes(Wire.Encode(fields) + "\n");
            try { lock (writeLock) stream.Write(bytes, 0, bytes.Length); }
            catch (Exception) { closed = true; }
        }

        public void Dispose()
        {
            closed = true;
            try { client.Close(); } catch (Exception) { }
        }
    }
}
