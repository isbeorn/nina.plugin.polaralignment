using System;
using System.IO.Ports;

namespace NINA.Plugins.PolarAlignment {

    /// <summary>
    /// The wire seam: everything the alignment systems do with a serial port goes through
    /// this interface. <see cref="SerialPortLink"/> implements it over a real SerialPort.
    /// </summary>
    public interface ISerialLink : IDisposable {
        bool IsOpen { get; }

        void WriteLine(string text);

        string ReadLine();

        /// <summary>
        /// Attempts to bring a dead link back, e.g. after a USB re-enumeration dropped
        /// the port mid-session. Returns true when the link is usable again. The default
        /// just reports the current state.
        /// </summary>
        bool TryReopen() => IsOpen;
    }

    internal sealed class SerialPortLink : ISerialLink {
        private readonly SerialPort port;

        public SerialPortLink(SerialPort port) {
            this.port = port;
        }

        public bool IsOpen => port.IsOpen;

        public void WriteLine(string text) => port.WriteLine(text);

        public string ReadLine() => port.ReadLine();

        public bool TryReopen() {
            if (port.IsOpen) {
                return true;
            }
            try {
                // Same SerialPort instance, same name and settings: after the device
                // re-enumerates on the same COM port (the common USB dropout), a plain
                // Open is all that is needed. A device that is still gone throws and
                // the caller keeps its bounded retry schedule.
                port.Open();
                try { port.DiscardInBuffer(); } catch { }
                return port.IsOpen;
            } catch {
                return false;
            }
        }

        public void Dispose() => port.Dispose();
    }
}
