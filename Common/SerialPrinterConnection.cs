using System.IO.Ports;

namespace InkPrinterCode.Common
{
    /// <summary>
    /// [2026-09-10] 喷码机串口连接实现（阶段二第 2 轮新增）
    ///
    /// 【串口参数约定】8 数据位 / 1 停止位 / 无校验（8N1）—— PDF 未给出校验位规格，
    ///   按 RS232 惯例取 8N1（万总确认默认 8N1，校验位暂不做下拉）。
    ///
    /// 【线程模型】
    ///   - _ioLock 保护 SerialPort：接收线程 Read 与业务线程 Write 不并发访问口对象；
    ///   - ReadTimeout 每次读之前设置（调用方传入），因为业务事务与接收轮询的超时要求不同。
    ///
    /// 【依赖】System.IO.Ports NuGet 包（.NET 8 里 SerialPort 不在共享框架内，已在 csproj 引入 8.0.0）。
    /// </summary>
    public class SerialPrinterConnection : IPrinterConnection
    {
        private readonly string _portName = string.Empty;
        private readonly int _baudRate = 9600;

        private SerialPort? _port = null;

        /// <summary>口读写锁（只保护口对象，不负责事务串行）</summary>
        private readonly object _ioLock = new object();

        /// <summary>
        /// 构造串口连接
        /// </summary>
        /// <param name="portName">串口号（如 COM3）</param>
        /// <param name="baudRate">波特率（9600/19200/38400/57600/115200）</param>
        public SerialPrinterConnection(string portName, int baudRate)
        {
            _portName = (portName ?? string.Empty).Trim().ToUpperInvariant();
            _baudRate = baudRate;
        }

        /// <summary>连接是否可用</summary>
        public bool IsConnected
        {
            get
            {
                SerialPort? port = _port;
                return port != null && port.IsOpen;
            }
        }

        /// <summary>
        /// 打开串口
        /// 【失败行为】抛异常（串口被占用 / 不存在是最常见失败原因，信息要能传到 UI）。
        /// </summary>
        public void Open()
        {
            if (_portName.Length == 0)
            {
                throw new ArgumentException("串口号为空，请先在「喷码机配置」中选择串口。");
            }

            SerialPort port = new SerialPort(
                _portName,
                _baudRate,
                Parity.None,
                8,
                StopBits.One);

            // 发送超时 5 秒；读取超时由 Read 调用方按需设置
            // [2026-09-10] 默认读超时 200→50ms（与接收线程传入值保持一致，实际以调用方传参为准）
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

        /// <summary>写出原始字节</summary>
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
                    throw new InvalidOperationException("串口未打开，无法发送数据。");
                }

                port.Write(data, 0, data.Length);
            }
        }

        /// <summary>
        /// 读取原始字节
        /// 【实现】SerialPort.Read 是阻塞式：等到任意字节或超时。
        ///   超时（TimeoutException）= 暂无数据，返回 0；口被拔掉等硬件异常向上抛。
        /// </summary>
        public int Read(byte[] buffer, int timeoutMs)
        {
            lock (_ioLock)
            {
                SerialPort? port = _port;
                if (port == null || !port.IsOpen)
                {
                    throw new InvalidOperationException("串口未打开，无法读取数据。");
                }

                port.ReadTimeout = timeoutMs < 1 ? 1 : timeoutMs;

                try
                {
                    return port.Read(buffer, 0, buffer.Length);
                }
                catch (TimeoutException)
                {
                    // 超时无数据是常态（心跳间隔内大部分时间无回报），不算错误
                    return 0;
                }
            }
        }

        /// <summary>关闭串口。重复调用安全，不抛异常</summary>
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
                // 串口被现场拔掉时 Close 可能抛异常 —— 释放流程必须走完，不能影响停止流程
                System.Diagnostics.Debug.WriteLine("关闭串口异常（已忽略）：" + ex.Message);
            }
        }

        /// <summary>释放资源（等同 Close）</summary>
        public void Dispose()
        {
            Close();
            GC.SuppressFinalize(this);
        }
    }
}
