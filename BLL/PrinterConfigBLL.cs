using System.Net;
using InkPrinterCode.Common;
using InkPrinterCode.DAL;
using InkPrinterCode.Model;
using InkPrinterCode.Model.Enums;

namespace InkPrinterCode.BLL
{
    /// <summary>
    /// [2026-09-10] 喷码机连接配置业务类（配置窗体 ↔ PrinterConfig 表 的中间层）
    ///
    /// 【职责】
    ///   1. 加载：确保两行存在 → 读出 TCP/串口两行配置给窗体回显；
    ///   2. 校验：TCP（IP 格式、端口 1~65535、复位端口 0~65535）+ 串口（串口号非空、波特率合法）；
    ///   3. 保存：单事务写库（两行参数 + 启用标志互斥切换）→ 日志。
    ///
    /// 【校验原则】与 SystemConfigBLL 一致 —— 任何一项不合法整批拒绝保存，绝不半存半丢。
    /// </summary>
    public static class PrinterConfigBLL
    {
        /// <summary>允许的波特率清单（下拉框数据源）</summary>
        public static readonly int[] BaudRates = new int[] { 9600, 19200, 38400, 57600, 115200 };

        /// <summary>加载结果：两行配置 + 当前启用的连接类型</summary>
        public class LoadResult
        {
            public bool Success = false;
            public string Message = string.Empty;
            public PrinterConfig TcpConfig = new PrinterConfig();
            public PrinterConfig SerialConfig = new PrinterConfig();
            public ConnType EnabledType = ConnType.Tcp;
        }

        // ============================================================
        // 1. 加载
        // ============================================================

        /// <summary>
        /// 加载配置（确保两行存在后全量读出；TCP/串口分别放好，任一行为空用默认实体兜底）
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
                LogHelper.Instance.Error("读取喷码机配置失败", ex);
                result.Message = "读取喷码机配置失败：" + ex.Message;
                return result;
            }
        }

        // ============================================================
        // 2. 校验
        // ============================================================

        /// <summary>校验 TCP 配置（IP 格式、端口区间、复位端口区间）</summary>
        /// <returns>合法返回空串；非法返回具体原因</returns>
        public static string ValidateTcp(PrinterConfig tcpConfig)
        {
            if (tcpConfig == null)
            {
                return "TCP 配置对象为空";
            }

            string ip = (tcpConfig.TcpIp ?? string.Empty).Trim();
            if (ip.Length == 0)
            {
                return "TCP 连接 IP 不能为空";
            }

            IPAddress parsedIp;
            if (!IPAddress.TryParse(ip, out parsedIp))
            {
                return "TCP 连接 IP 格式不正确（示例：192.168.1.100），当前输入：" + ip;
            }

            if (tcpConfig.TcpPort < 1 || tcpConfig.TcpPort > 65535)
            {
                return "TCP 端口必须在 1 ~ 65535 之间，当前输入：" + tcpConfig.TcpPort.ToString();
            }

            if (tcpConfig.ResetPort < 0 || tcpConfig.ResetPort > 65535)
            {
                return "预复位端口必须在 0 ~ 65535 之间（0=禁用），当前输入：" + tcpConfig.ResetPort.ToString();
            }

            return string.Empty;
        }

        /// <summary>校验串口配置（串口号非空、波特率在允许清单内）</summary>
        /// <returns>合法返回空串；非法返回具体原因</returns>
        public static string ValidateSerial(PrinterConfig serialConfig)
        {
            if (serialConfig == null)
            {
                return "串口配置对象为空";
            }

            string portName = (serialConfig.SerialPortName ?? string.Empty).Trim();
            if (portName.Length == 0)
            {
                return "串口号不能为空";
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
                return "波特率必须是 " + string.Join(" / ", BaudRates) + " 之一，当前输入：" + serialConfig.SerialBaudRate.ToString();
            }

            return string.Empty;
        }

        // ============================================================
        // 3. 保存
        // ============================================================

        /// <summary>
        /// 保存配置（校验 TCP + 串口两行 → 单事务写库 → 日志）
        /// 【说明】无论启用哪一种，两行的参数都会一并保存 —— 切换时配置内容不丢（万总要求）。
        /// </summary>
        /// <param name="tcpConfig">TCP 行配置</param>
        /// <param name="serialConfig">串口行配置</param>
        /// <param name="enabledType">保存后启用的连接类型</param>
        /// <returns>成功返回空串；失败返回具体原因</returns>
        public static string Save(PrinterConfig tcpConfig, PrinterConfig serialConfig, ConnType enabledType)
        {
            // ---------- 校验（两行都验，不只验启用行 —— 避免以后切换时才暴雷） ----------
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

            // ---------- 保存（两行参数规范化后写库） ----------
            tcpConfig.TcpIp = tcpConfig.TcpIp.Trim();
            tcpConfig.TcpPort = tcpConfig.TcpPort;
            tcpConfig.ResetPort = tcpConfig.ResetPort;
            serialConfig.SerialPortName = serialConfig.SerialPortName.Trim().ToUpperInvariant();

            try
            {
                PrinterConfigDAL.Save(tcpConfig, serialConfig, enabledType);

                LogHelper.Instance.Info("喷码机配置保存成功：启用 "
                                        + (enabledType == ConnType.Tcp ? "TCP（" + tcpConfig.TcpIp + ":" + tcpConfig.TcpPort.ToString() + "）"
                                                                       : "串口（" + serialConfig.SerialPortName + " @" + serialConfig.SerialBaudRate.ToString() + "）"));
                return string.Empty;
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Error("保存喷码机配置失败", ex);
                return "保存失败：" + ex.Message;
            }
        }
    }
}
