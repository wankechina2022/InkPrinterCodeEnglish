using System.Net;
using InkPrinterCode.Common;
using InkPrinterCode.DAL;
using InkPrinterCode.Model;
using InkPrinterCode.Model.Enums;

namespace InkPrinterCode.BLL
{
    /// <summary>
    /// [2026-09-10] Inkjet printer connection configuration business class (middle layer between the config form and the PrinterConfig table)
    ///
    /// [Responsibilities]
    ///   1. Load: ensure both rows exist → read out the TCP/serial rows for display in the form;
    ///   2. Validate: TCP (IP format, port 1~65535, reset port 0~65535) + serial (port name non-empty, baud rate legal);
    ///   3. Save: write to the database in a single transaction (both rows of parameters + mutually exclusive switching of the enable flag) → log.
    ///
    /// [Validation principle] Consistent with SystemConfigBLL — if any item is invalid the whole batch is refused, never half-saved/half-dropped.
    /// </summary>
    public static class PrinterConfigBLL
    {
        /// <summary>List of allowed baud rates (data source for the combo box)</summary>
        public static readonly int[] BaudRates = new int[] { 9600, 19200, 38400, 57600, 115200 };

        /// <summary>Load result: both rows of configuration + the currently enabled connection type</summary>
        public class LoadResult
        {
            public bool Success = false;
            public string Message = string.Empty;
            public PrinterConfig TcpConfig = new PrinterConfig();
            public PrinterConfig SerialConfig = new PrinterConfig();
            public ConnType EnabledType = ConnType.Tcp;
        }

        // ============================================================
        // 1. Load
        // ============================================================

        /// <summary>
        /// Load the configuration (ensure both rows exist, then read everything out; keep TCP/serial
        /// separately, and fall back to a default entity when either row is empty)
        /// </summary>
        public static LoadResult Load()
        {
            LoadResult result = new LoadResult();

            try
            {
                PrinterConfigDAL.EnsureRows();

                List<PrinterConfig> all = PrinterConfigDAL.GetAll();

                foreach (PrinterConfig config in all)
                {
                    if (config.ConnType == ConnType.Tcp)
                    {
                        result.TcpConfig = config;
                    }
                    else if (config.ConnType == ConnType.Serial)
                    {
                        result.SerialConfig = config;
                    }

                    if (config.IsEnabledRow)
                    {
                        result.EnabledType = config.ConnType;
                    }
                }

                result.Success = true;
                return result;
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Error("Failed to read inkjet printer configuration", ex);
                result.Message = "Failed to read inkjet printer configuration: " + ex.Message;
                return result;
            }
        }

        // ============================================================
        // 2. Validation
        // ============================================================

        /// <summary>Validate the TCP configuration (IP format, port range, reset port range)</summary>
        /// <returns>Empty string if valid; a specific reason if invalid</returns>
        public static string ValidateTcp(PrinterConfig tcpConfig)
        {
            if (tcpConfig == null)
            {
                return "The TCP configuration object is null";
            }

            string ip = (tcpConfig.TcpIp ?? string.Empty).Trim();
            if (ip.Length == 0)
            {
                return "TCP connection IP cannot be empty";
            }

            IPAddress parsedIp;
            if (!IPAddress.TryParse(ip, out parsedIp))
            {
                return "The TCP connection IP format is incorrect (example: 192.168.1.100); current input: " + ip;
            }

            if (tcpConfig.TcpPort < 1 || tcpConfig.TcpPort > 65535)
            {
                return "The TCP port must be between 1 and 65535; current input: " + tcpConfig.TcpPort.ToString();
            }

            if (tcpConfig.ResetPort < 0 || tcpConfig.ResetPort > 65535)
            {
                return "The pre-reset port must be between 0 and 65535 (0 = disabled); current input: " + tcpConfig.ResetPort.ToString();
            }

            return string.Empty;
        }

        /// <summary>Validate the serial configuration (port name non-empty, baud rate within the allowed list)</summary>
        /// <returns>Empty string if valid; a specific reason if invalid</returns>
        public static string ValidateSerial(PrinterConfig serialConfig)
        {
            if (serialConfig == null)
            {
                return "The serial configuration object is null";
            }

            string portName = (serialConfig.SerialPortName ?? string.Empty).Trim();
            if (portName.Length == 0)
            {
                return "The serial port name cannot be empty";
            }

            bool baudOk = false;
            for (int i = 0; i < BaudRates.Length; i++)
            {
                if (BaudRates[i] == serialConfig.SerialBaudRate)
                {
                    baudOk = true;
                    break;
                }
            }

            if (!baudOk)
            {
                return "The baud rate must be one of " + string.Join(" / ", BaudRates) + "; current input: " + serialConfig.SerialBaudRate.ToString();
            }

            return string.Empty;
        }

        // ============================================================
        // 3. Save
        // ============================================================

        /// <summary>
        /// Save the configuration (validate both the TCP and serial rows → write to the database in a single transaction → log)
        /// [Note] Whichever one is enabled, the parameters of both rows are saved together — nothing is lost when switching (requested by Mr. Wan).
        /// </summary>
        /// <param name="tcpConfig">TCP row configuration</param>
        /// <param name="serialConfig">Serial row configuration</param>
        /// <param name="enabledType">Connection type to enable after saving</param>
        /// <returns>Empty string on success; a specific reason on failure</returns>
        public static string Save(PrinterConfig tcpConfig, PrinterConfig serialConfig, ConnType enabledType)
        {
            // ---------- Validation (validate both rows, not just the enabled one — so problems do not blow up later when switching) ----------
            string tcpError = ValidateTcp(tcpConfig);
            if (tcpError.Length > 0)
            {
                return tcpError;
            }

            string serialError = ValidateSerial(serialConfig);
            if (serialError.Length > 0)
            {
                return serialError;
            }

            // ---------- Save (normalize both rows of parameters, then write to the database) ----------
            tcpConfig.TcpIp = tcpConfig.TcpIp.Trim();
            tcpConfig.TcpPort = tcpConfig.TcpPort;
            tcpConfig.ResetPort = tcpConfig.ResetPort;
            serialConfig.SerialPortName = serialConfig.SerialPortName.Trim().ToUpperInvariant();

            try
            {
                PrinterConfigDAL.Save(tcpConfig, serialConfig, enabledType);

                LogHelper.Instance.Info("Inkjet printer configuration saved successfully: enabled "
                                        + (enabledType == ConnType.Tcp ? "TCP (" + tcpConfig.TcpIp + ":" + tcpConfig.TcpPort.ToString() + ")"
                                                                       : "serial (" + serialConfig.SerialPortName + " @" + serialConfig.SerialBaudRate.ToString() + ")"));
                return string.Empty;
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Error("Failed to save inkjet printer configuration", ex);
                return "Save failed: " + ex.Message;
            }
        }
    }
}
