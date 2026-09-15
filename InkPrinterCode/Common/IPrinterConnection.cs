namespace InkPrinterCode.Common
{
    /// <summary>
    /// [2026-09-10] Inkjet printer connection abstraction interface
    ///
    /// [Design purpose (the layering it enforces)]
    ///   TCP and serial each get their own implementation class (TcpPrinterConnection / SerialPrinterConnection),
    ///   while command assembly and reply parsing are kept separate in CodeNetProtocol —— the connection layer only
    ///   handles "bytes in and out" and knows nothing about the protocol, and the protocol layer only handles
    ///   "framing and deframing" and knows nothing about the connection. The two sides never mix.
    ///
    /// [Implementation conventions]
    ///   1. An implementation class must carry its own read/write lock: Write and Read may be called from different
    ///      threads (the business layer's _ioLock serializes the "send → wait for reply" transaction, while the
    ///      receive thread reads the stream independently), and the connection-layer lock only protects the stream
    ///      object itself from concurrent access;
    ///   2. A failed Open must throw (the connection failure reason has to reach the UI), whereas Close/Dispose must
    ///      not throw;
    ///   3. Read returns the number of bytes actually read this time: 0 when it times out with no data, and throws
    ///      IOException when the peer closes the connection;
    ///   4. All implementation classes are thread-safe only down to the "stream object" level; transaction-level
    ///      serialization is the responsibility of PrintServiceBLL's _ioLock (the two levels of locking have separate
    ///      responsibilities; see design document 3.1).
    /// </summary>
    public interface IPrinterConnection : IDisposable
    {
        /// <summary>Whether the connection is usable (a heartbeat timeout determination is the real basis for being online; this property only reflects the object's state)</summary>
        bool IsConnected { get; }

        /// <summary>
        /// Establish the connection (TCP: an optional port-700 pre-reset; serial: open the COM port).
        /// Throws on failure; the caller (PrintServiceBLL) turns it into a UI notification and a log entry.
        /// </summary>
        void Open();

        /// <summary>Close the connection and release the underlying resources. Safe to call repeatedly; must not throw</summary>
        void Close();

        /// <summary>Write out the raw bytes. Throws on failure (the data did not go out → the caller treats it as a "write exception")</summary>
        void Write(byte[] data);

        /// <summary>
        /// Read the raw bytes
        /// </summary>
        /// <param name="buffer">The receiving buffer</param>
        /// <param name="timeoutMs">Maximum time to wait for data (milliseconds)</param>
        /// <returns>The number of bytes actually read (0 = timed out with no data); throws on a connection error</returns>
        int Read(byte[] buffer, int timeoutMs);
    }
}
