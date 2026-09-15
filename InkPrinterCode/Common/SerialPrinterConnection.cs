using System.IO.Ports;

namespace InkPrinterCode.Common
{
    /// <summary>
    /// [2026-09-10] Inkjet printer serial-port connection implementation
    ///
    /// [Serial parameter convention] 8 data bits / 1 stop bit / no parity (8N1) —— the PDF does not give a parity
    ///   specification, so 8N1 is used per RS232 convention (8N1 is the default and parity is
    ///   not being made a dropdown for now).
    ///
    /// [Thread model]
    ///   - _ioLock protects the SerialPort: the receive thread's Read and the business thread's Write do not access the
    ///     port object concurrently;
    ///   - ReadTimeout is set before each read (passed in by the caller), because the timeout requirements of a business
    ///     transaction and of receive polling differ.
    ///
    /// [Dependency] The System.IO.Ports NuGet package (.NET 8 does not include SerialPort in the shared framework; 8.0.0
    /// has been added in the csproj).
    /// </summary>
    public class SerialPrinterConnection : IPrinterConnection
    {
        private readonly string _portName = string.Empty;
        private readonly int _baudRate = 9600;

        private SerialPort? _port = null;

        /// <summary>Port read/write lock (only protects the port object; it does not serialize transactions)</summary>
        private readonly object _ioLock = new object();

        /// <summary>
        /// Construct a serial connection
        /// </summary>
        /// <param name="portName">Serial port name (e.g. COM3)</param>
        /// <param name="baudRate">Baud rate (9600/19200/38400/57600/115200)</param>
        public SerialPrinterConnection(string portName, int baudRate)
        {
            _portName = (portName ?? string.Empty).Trim().ToUpperInvariant();
            _baudRate = baudRate;
        }

        /// <summary>Whether the connection is usable</summary>
        public bool IsConnected
        {
            get
            {
                SerialPort? port = _port;
                return port != null && port.IsOpen;
            }
        }

        /// <summary>
        /// Open the serial port
        /// [Failure behaviour] Throws on failure (port already in use / not present are the most common causes; the message must be able to reach the UI).
        /// </summary>
        public void Open()
        {
            if (_portName.Length == 0)
            {
                throw new ArgumentException("The serial port name is empty. Please select a serial port in 'Printer Config' first.");
            }

            SerialPort port = new SerialPort(
                _portName,
                _baudRate,
                Parity.None,
                8,
                StopBits.One);

            // Write timeout is 5 seconds; the read timeout is set by the caller of Read as needed
            // [2026-09-10] Default read timeout changed 200 -> 50 ms (kept in line with the value passed in by the receive thread; the caller's argument always wins)
            port.WriteTimeout = 5000;
            port.ReadTimeout = 50;

            try
            {
                port.Open();
                _port = port;
            }
            catch
            {
                port.Dispose();
                throw;
            }
        }

        /// <summary>Write out the raw bytes</summary>
        public void Write(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return;
            }

            lock (_ioLock)
            {
                SerialPort? port = _port;
                if (port == null || !port.IsOpen)
                {
                    throw new InvalidOperationException("The serial port is not open; cannot send data.");
                }

                port.Write(data, 0, data.Length);
            }
        }

        /// <summary>
        /// Read raw bytes
        /// [Implementation] SerialPort.Read blocks: it waits for any byte or until the timeout elapses.
        ///   A timeout (TimeoutException) means no data is available and 0 is returned; hardware errors such as the port being unplugged are rethrown.
        /// </summary>
        public int Read(byte[] buffer, int timeoutMs)
        {
            lock (_ioLock)
            {
                SerialPort? port = _port;
                if (port == null || !port.IsOpen)
                {
                    throw new InvalidOperationException("The serial port is not open; cannot read data.");
                }

                port.ReadTimeout = timeoutMs < 1 ? 1 : timeoutMs;

                try
                {
                    return port.Read(buffer, 0, buffer.Length);
                }
                catch (TimeoutException)
                {
                    // A timeout with no data is normal (there is no response most of the time between heartbeats), so it is not treated as an error
                    return 0;
                }
            }
        }

        /// <summary>Close the serial port. Safe to call repeatedly; never throws</summary>
        public void Close()
        {
            try
            {
                SerialPort? port = _port;
                _port = null;

                if (port != null)
                {
                    if (port.IsOpen)
                    {
                        port.Close();
                    }
                    port.Dispose();
                }
            }
            catch (Exception ex)
            {
                // Close may throw when the port has been unplugged on site -- the release flow must always run to completion and must not break the stop flow
                System.Diagnostics.Debug.WriteLine("Exception while closing serial port (ignored): " + ex.Message);
            }
        }

        /// <summary>Release resources (same as Close)</summary>
        public void Dispose()
        {
            Close();
            GC.SuppressFinalize(this);
        }
    }
}
