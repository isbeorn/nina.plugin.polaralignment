using System;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace NINA.Plugins.PolarAlignment.OAPA {

    /// <summary>
    /// The OAPA line protocol over WiFi. The controller (firmware 1.3.0+) listens on TCP 2323
    /// and answers exactly as it does on USB, so nothing past this link can tell the two apart:
    /// same lines, same "?" frame, same one-reply-per-command discipline.
    /// </summary>
    internal sealed class OapaTcpLink : ISerialLink {
        public const int DefaultPort = 2323;

        // Same budgets as the serial scan: a controller on the local network answers "?" in
        // ~15 ms, so a second means it is not there.
        private const int ConnectTimeoutMs = 3000;
        private const int ReadTimeoutMs = 1000;
        private const int WriteTimeoutMs = 1000;

        private readonly string host;
        private readonly int port;
        private readonly byte[] buffer = new byte[512];
        private int bufferStart;
        private int bufferEnd;
        private TcpClient client;
        private NetworkStream stream;

        private OapaTcpLink(string host, int port) {
            this.host = host;
            this.port = port;
        }

        public string Address => $"{host}:{port}";

        /// <summary>Opens the link to "host" or "host:port"; throws when nothing answers the connect.</summary>
        public static OapaTcpLink Connect(string address) {
            var (host, port) = ParseAddress(address);
            var link = new OapaTcpLink(host, port);
            link.Open();
            return link;
        }

        internal static (string host, int port) ParseAddress(string address) {
            var text = address?.Trim() ?? "";
            if (text.Length == 0) {
                throw new ArgumentException("No WiFi address set for the OAPA controller");
            }
            var colon = text.LastIndexOf(':');
            if (colon < 0) {
                return (text, DefaultPort);
            }
            if (colon == 0 || !int.TryParse(text.Substring(colon + 1), out var port) || port < 1 || port > 65535) {
                throw new ArgumentException($"'{text}' is not a valid address; expected host or host:port");
            }
            return (text.Substring(0, colon), port);
        }

        public bool IsOpen => stream != null && client?.Connected == true;

        public void WriteLine(string text) {
            if (stream == null) {
                throw new IOException($"OAPA WiFi link to {Address} is closed");
            }
            var bytes = Encoding.ASCII.GetBytes(text + "\n");
            try {
                stream.Write(bytes, 0, bytes.Length);
            } catch (IOException) {
                Close();
                throw;
            }
        }

        /// <summary>
        /// One line without its '\n', as <see cref="System.IO.Ports.SerialPort.ReadLine"/> returns it with
        /// NewLine "\n" (a trailing '\r' stays). A silent controller throws <see cref="TimeoutException"/>,
        /// which is what the callers already handle for a serial port.
        /// </summary>
        public string ReadLine() {
            while (true) {
                for (var i = bufferStart; i < bufferEnd; i++) {
                    if (buffer[i] == (byte)'\n') {
                        var line = Encoding.ASCII.GetString(buffer, bufferStart, i - bufferStart);
                        bufferStart = i + 1;
                        return line;
                    }
                }
                Fill();
            }
        }

        private void Fill() {
            if (stream == null) {
                throw new IOException($"OAPA WiFi link to {Address} is closed");
            }
            if (bufferStart > 0) {
                Array.Copy(buffer, bufferStart, buffer, 0, bufferEnd - bufferStart);
                bufferEnd -= bufferStart;
                bufferStart = 0;
            }
            if (bufferEnd == buffer.Length) {
                throw new IOException("OAPA controller sent a line longer than the link buffer");
            }
            int read;
            try {
                read = stream.Read(buffer, bufferEnd, buffer.Length - bufferEnd);
            } catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut }) {
                throw new TimeoutException($"No reply from the OAPA controller at {Address}", ex);
            } catch (IOException) {
                Close();
                throw;
            }
            if (read == 0) {
                // The controller closed the connection: another client took it over, or it rebooted.
                Close();
                throw new IOException($"OAPA controller at {Address} closed the connection");
            }
            bufferEnd += read;
        }

        public bool TryReopen() {
            if (IsOpen) {
                return true;
            }
            try {
                Open();
                return true;
            } catch {
                return false;
            }
        }

        private void Open() {
            Close();
            var tcp = new TcpClient { NoDelay = true, ReceiveTimeout = ReadTimeoutMs, SendTimeout = WriteTimeoutMs };
            try {
                if (!tcp.ConnectAsync(host, port).Wait(ConnectTimeoutMs)) {
                    throw new TimeoutException($"No OAPA controller answering at {Address}");
                }
            } catch (AggregateException ex) when (ex.InnerException != null) {
                tcp.Dispose();
                throw new IOException($"Cannot reach the OAPA controller at {Address}: {ex.InnerException.Message}", ex.InnerException);
            } catch {
                tcp.Dispose();
                throw;
            }
            client = tcp;
            stream = tcp.GetStream();
            stream.ReadTimeout = ReadTimeoutMs;
            stream.WriteTimeout = WriteTimeoutMs;
            bufferStart = bufferEnd = 0;
        }

        private void Close() {
            stream?.Dispose();
            client?.Dispose();
            stream = null;
            client = null;
            bufferStart = bufferEnd = 0;
        }

        public void Dispose() => Close();
    }
}
