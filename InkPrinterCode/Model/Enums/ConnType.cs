namespace InkPrinterCode.Model.Enums
{
    /// <summary>
    /// [2026-09-10] Inkjet printer connection type
    ///
    /// [Note] Maps one-to-one to the ConnType field of the PrinterConfig table (stored as an integer in the DB).
    ///   Tcp    = network connection (the CodeNet protocol uses a fixed port 7000; supported on A200+ and above);
    ///   Serial = RS232 serial connection (A200/A300 only support serial).
    ///   The configuration for each connection type occupies its own row in the PrinterConfig table, and the
    ///   IsEnabled flag decides which one is currently enabled; switching only changes the flag and the parameters
    ///   never overwrite each other (requirement: switching back and forth must not lose configuration).
    /// </summary>
    public enum ConnType
    {
        /// <summary>TCP network connection (default, 0)</summary>
        Tcp = 0,

        /// <summary>RS232 serial connection (1)</summary>
        Serial = 1
    }
}
