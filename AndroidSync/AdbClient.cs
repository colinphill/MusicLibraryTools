using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AndroidSync
{

    interface IShellReceiver
    {
        void Start();
        void End();
        void WriteStdout(byte[] buffer, int length);
        void WriteStderr(byte[] buffer, int length);
    }

    interface ISyncProgress
    {
        void Start();
        void End();
        void SetProgress(long transferred);
    }

    class ShellReceiver : IShellReceiver
    {
        private Encoding Encoding;
        private MemoryStream stdoutstream_ = new MemoryStream();
        private MemoryStream stderrstream_ = new MemoryStream();
        private string[] stdoutlines_ = new string[0];
        private string[] stderrlines_ = new string[0];
        private DecoderFallback fallback_ = new AndroidDecoderFixFallback();

        public string[] StdoutLines => stdoutlines_;
        public string[] StderrLines => stderrlines_;

        public ShellReceiver()
        {
            Encoding = AdbClient.Encoding;
        }

        public ShellReceiver(Encoding encoding)
        {
            Encoding = encoding;
        }

        public void Start()
        {
            stdoutstream_.SetLength(0);
            stderrstream_.SetLength(0);
            stdoutlines_ = new string[0];
            stderrlines_ = new string[0];
        }

        public void End()
        {
            stdoutstream_.Flush();
            stderrstream_.Flush();
            stdoutlines_ = Encoding.GetString(stdoutstream_.ToArray()).Split(new string[] { "\r\n", "\n" }, StringSplitOptions.None);
            stderrlines_ = Encoding.GetString(stderrstream_.ToArray()).Split(new string[] { "\r\n", "\n" }, StringSplitOptions.None);
        }

        public void WriteStderr(byte[] buffer, int length)
        {
            stderrstream_.Write(buffer, 0, length);
        }

        public void WriteStdout(byte[] buffer, int length)
        {
            stdoutstream_.Write(buffer, 0, length);
        }
    }

    class AdbConnection : Socket
    {
        private Encoding Encoding;

        public AdbConnection(Encoding encoding) : base(SocketType.Stream, ProtocolType.Tcp)
        {
            Encoding = encoding;
        }

        public Task<int> ReceiveAsync(byte[] buffer, int offset = 0, int count = -1)
        {
            if (count == -1)
                count = buffer.Length - offset;
            return Task.Run(() =>
            {
                int total = 0;
                int tbd = count;
                while (tbd > 0)
                {
                    int done = Receive(buffer, offset, tbd, SocketFlags.None);
                    if (done < 0)
                        throw new Exception("Socket Error");
                    tbd -= done;
                    total += done;
                    offset += done;
                    if (done == 0)
                        return total;
                }
                return total;
            });
        }

        public Task<int> SendAsync(byte[] buffer, int offset = 0, int count = -1)
        {
            if (count == -1)
                count = buffer.Length - offset;
            return Task.Run(() =>
            {
                return Send(buffer, offset, count, SocketFlags.None);
            });
        }

        public async Task SendRequestAsync(string req)
        {
            string r = Encoding.GetByteCount(req).ToString("X4") + req;
            await SendAsync(Encoding.GetBytes(r));
        }

        public async Task SendSyncRequestAsync(string verb, byte[] buffer, int offset = 0, int count = -1)
        {
            if (count == -1)
                count = buffer.Length;
            byte[] tempbuf = new byte[8];
            Encoding.GetBytes(verb, 0, verb.Length, tempbuf, 0);
            Array.Copy(BitConverter.GetBytes(count), 0, tempbuf, 4, 4);
            if (buffer != null)
            {
                Array.Resize(ref tempbuf, count + 8);
                Array.Copy(buffer, 0, tempbuf, 8, count);
            }
            await SendAsync(tempbuf);
        }

        public async Task<string> ReceiveResponseAsync()
        {
            byte[] buffer = new byte[4];
            int received = await ReceiveAsync(buffer);
            string resp = Encoding.GetString(buffer, 0, received);
            if (resp != "OKAY")
            {
                string message = await ReceiveStringAsync();
                throw new Exception("ADB Exception:" + resp + " Message:" + message);
            }
            return resp;
        }

        public async Task<string> ReceiveStringAsync()
        {
            byte[] buffer = new byte[4];
            int received = await ReceiveAsync(buffer);
            if (received != 4)
                throw new Exception("Invalid Data Received");
            int length = int.Parse(Encoding.GetString(buffer), System.Globalization.NumberStyles.HexNumber);
            byte[] sbuffer = new byte[length];
            received = await ReceiveAsync(sbuffer);
            if (received != length)
                throw new Exception("Invalid Data Received");
            return Encoding.GetString(sbuffer);
        }

    }

    class AndroidDecoderFixFallbackBuffer : DecoderFallbackBuffer
    {
        private Queue<char> replacements_ = new Queue<char>();

        public override int Remaining => replacements_.Count;

        public override bool Fallback(byte[] bytesUnknown, int index)
        {
            if (bytesUnknown.Length == 1)
            {
                switch (bytesUnknown[0])
                {
                    case 0xa0:
                        replacements_.Enqueue('\xa0');
                        return true;

                    default:
                        break;
                }
            }
            return false;
        }

        public override char GetNextChar()
        {
            if (replacements_.Count > 0)
                return replacements_.Dequeue();
            return '\x00';
        }

        public override bool MovePrevious()
        {
            return false;
        }
    }

    class AndroidDecoderFixFallback : DecoderFallback
    {
        public override int MaxCharCount => 1;

        public override DecoderFallbackBuffer CreateFallbackBuffer()
        {
            return new AndroidDecoderFixFallbackBuffer();
        }
    }

    class AdbClient
    {
        public static readonly Encoding Encoding = System.Text.Encoding.GetEncoding("utf-8", new EncoderExceptionFallback(), new AndroidDecoderFixFallback());
        private readonly int MaxBufferSize = 64 * 1024;
        private IPEndPoint endpoint_ = new IPEndPoint(IPAddress.Loopback, 5037);
        
        public AdbClient()
        {

        }

        public AdbClient(IPEndPoint endpoint)
        {
            endpoint_ = endpoint;
        }

        public async Task<int> GetHostVersionAsync()
        {
            using (var conn = new AdbConnection(Encoding))
            {
                await conn.ConnectAsync(IPAddress.Loopback, 5037);
                await conn.SendRequestAsync("host:version");
                await conn.ReceiveResponseAsync();
                return int.Parse(await conn.ReceiveStringAsync(), System.Globalization.NumberStyles.HexNumber);
            }
        }

        private readonly static DateTime epoch_ = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public async Task PushAsync(Stream source, string device, string dest, int permissions, DateTime stamp, ISyncProgress progress = null)
        {
            if (progress != null)
                progress.Start();
            using (var conn = new AdbConnection(Encoding))
            {
                await conn.ConnectAsync(IPAddress.Loopback, 5037);
                await conn.SendRequestAsync(device == null ? "host:transport-any" : $"host:transport:{device}");
                await conn.ReceiveResponseAsync();
                await conn.SendRequestAsync("sync:");
                await conn.ReceiveResponseAsync();
                await conn.SendSyncRequestAsync("SEND", Encoding.GetBytes($"{dest},{permissions}"));
                byte[] buffer = new byte[MaxBufferSize];
                long done = 0;
                for(; ;)
                {
                    int read = await source.ReadAsync(buffer, 0, MaxBufferSize - 64);

                    if (read > 0)
                        await conn.SendSyncRequestAsync("DATA", buffer, 0, read);

                    done += read;
                    if (progress != null)
                        progress.SetProgress(done);

                    if (read < (MaxBufferSize - 64))
                        break;
                }
                await conn.SendSyncRequestAsync("DONE", null, 0, (int)((stamp - epoch_).TotalSeconds));
                await conn.ReceiveResponseAsync();
            }
            if (progress != null)
                progress.End();
        }

        public async Task PullAsync(Stream dest, string device, string source, ISyncProgress progress = null)
        {
            if (progress != null)
                progress.Start();
            using (var conn = new AdbConnection(Encoding))
            {
                await conn.ConnectAsync(IPAddress.Loopback, 5037);
                await conn.SendRequestAsync(device == null ? "host:transport-any" : $"host:transport:{device}");
                await conn.ReceiveResponseAsync();
                await conn.SendRequestAsync("sync:");
                await conn.ReceiveResponseAsync();
                await conn.SendSyncRequestAsync("RECV", Encoding.GetBytes(source));
                byte[] buffer = new byte[MaxBufferSize];
                long done = 0;
                for (; ; )
                {
                    int received = await conn.ReceiveAsync(buffer, 0, 4);
                    if (received != 4)
                        throw new Exception("ADB Exception: Mismatched Length");
                    string res = Encoding.GetString(buffer, 0, 4);
                    if (res == "DATA")
                    {
                        received = await conn.ReceiveAsync(buffer, 0, 4);
                        if (received != 4)
                            throw new Exception("ADB Exception: Mismatched Length");
                        int len = BitConverter.ToInt32(buffer, 0);
                        received = await conn.ReceiveAsync(buffer, 0, len);
                        if (received != len)
                            throw new Exception("ADB Exception: Mismatched Length");
                        dest.Write(buffer, 0, len);
                        done += len;
                    }
                    else if (res == "DONE")
                    {
                        break;

                    }
                    else
                        throw new Exception($"ADB Exception: Unrecognized Response: {res}");

                    if (progress != null)
                        progress.SetProgress(done);
                }
            }
            if (progress != null)
                progress.End();
        }

        public async Task<int> ShellExecuteAsync(string command, string device = null, IShellReceiver receiver = null)
        {
            using (var conn = new AdbConnection(Encoding))
            {
                await conn.ConnectAsync(IPAddress.Loopback, 5037);
                await conn.SendRequestAsync(device == null ? "host:transport-any" : $"host:transport:{device}");
                await conn.ReceiveResponseAsync();
                await conn.SendRequestAsync("shell,v2,raw:" + command);
                await conn.ReceiveResponseAsync();
                byte[] buffer = new byte[MaxBufferSize];
                byte[] packetheader = new byte[5];
                int phoffset = 0, poffset = 0, plen = 0;
                byte ptype = 0;
                byte[] packet = new byte[0];
                int retcode = 0;
                if (receiver != null)
                    receiver.Start();

                for (; ; )
                {
                    int received = await conn.ReceiveAsync(buffer);
                    int offset = 0;
                    while (offset < received)
                    {
                        if (phoffset < 5)
                        {
                            int tbd = 5 - phoffset;
                            if ((received - offset) < tbd)
                                tbd = received - offset;
                            Array.Copy(buffer, offset, packetheader, phoffset, tbd);
                            phoffset += tbd;
                            offset += tbd;
                            if (phoffset == 5)
                            {
                                ptype = packetheader[0];
                                plen = BitConverter.ToInt32(packetheader, 1);
                                poffset = 0;
                                Array.Resize(ref packet, plen);
                            }
                        }
                        else if (plen > 0)
                        {
                            int tbd = plen;
                            if ((received - offset) < plen)
                                tbd = received - offset;
                            Array.Copy(buffer, offset, packet, poffset, tbd);
                            poffset += tbd;
                            offset += tbd;
                            plen -= tbd;
                            if (plen == 0)
                            {
                                if ((receiver != null) && (ptype == 1)) // Stdout
                                    receiver.WriteStdout(packet, packet.Length);
                                else if ((receiver != null) && (ptype == 2)) // Stderr
                                    receiver.WriteStderr(packet, packet.Length);
                                else if (ptype == 3)
                                    retcode = packet[0];
                                phoffset = 0;
                            }
                        }
                    }

                    if (received < buffer.Length)
                        break;
                }
                if (receiver != null)
                    receiver.End();

                return retcode;
            }

        }

    }
}
