using System.Net.Sockets;

namespace InkPrinterCode.Common
{
    /// <summary>
    /// [2026-09-10] 喷码机 TCP 连接实现（阶段二第 2 轮新增）
    ///
    /// 【CodeNet 网口约定】
    ///   - 数据端口固定 7000（PDF 第 2 节）；
    ///   - 连接前可向"预复位端口"（现场经验默认 700）发 4 字节复位指令 07 01 01 2C，
    ///     残留旧缓存时更稳定 —— 此条 PDF 未提及，属现场经验，做成可配置（TcpResetEnabled / ResetPort=0 关闭），
    ///     复位失败只记调试输出、不阻断主连接（万总确认：失败不阻断）。
    ///
    /// 【线程模型】
    ///   - _ioLock 保护流对象：接收线程 Read 与业务线程 Write 不并发访问 NetworkStream；
    ///     [2026-09-10] Read 里的 Socket.Poll 等待段已移出该锁（锁外等待、锁内读），
    ///     使写方（发码 / 心跳）几乎不需要等锁；详见 Read 方法注释。
    ///   - Open / Close 只在启动、重连、停止流程中调用（单线程场景），不加锁（加了反而可能死锁）。
    ///     因此 Close 与 Read 属于无锁并发：等待期间连接被关会抛 ObjectDisposedException，
    ///     由 BLL 兜住重试（既有设计，非本次引入）。
    /// </summary>
    public class TcpPrinterConnection : IPrinterConnection
    {
        /// <summary>主连接超时（毫秒）—— 现场设备在局域网内，5 秒足够</summary>
        private const int CONNECT_TIMEOUT_MS = 5000;

        private readonly string _host = string.Empty;
        private readonly int _port = 7000;
        private readonly bool _resetEnabled = false;
        private readonly int _resetPort = 0;

        private TcpClient? _client = null;
        private NetworkStream? _stream = null;

        /// <summary>流读写锁（只保护流对象，不负责事务串行）</summary>
        private readonly object _ioLock = new object();

        /// <summary>
        /// 构造 TCP 连接
        /// </summary>
        /// <param name="host">喷码机 IP</param>
        /// <param name="port">数据端口（CodeNet 固定 7000）</param>
        /// <param name="resetEnabled">是否在主连接前做预复位</param>
        /// <param name="resetPort">预复位端口（0 = 不复位）</param>
        public TcpPrinterConnection(string host, int port, bool resetEnabled, int resetPort)
        {
            _host = (host ?? string.Empty).Trim();
            _port = port;
            _resetEnabled = resetEnabled;
            _resetPort = resetPort;
        }

        /// <summary>连接是否可用</summary>
        public bool IsConnected
        {
            get
            {
                TcpClient? client = _client;
                return client != null && client.Connected && _stream != null;
            }
        }

        /// <summary>
        /// 建立连接：先（可选）预复位，再连数据端口
        /// 【失败行为】任一步失败抛异常，且已建立的资源就地释放，不留半开连接。
        /// </summary>
        public void Open()
        {
            if (_host.Length == 0)
            {
                throw new ArgumentException("TCP 连接 IP 为空，请先在「喷码机配置」中填写。");
            }

            // ---------- 1. 预复位（现场经验，失败不阻断） ----------
            if (_resetEnabled && _resetPort > 0)
            {
                TryReset(_resetPort);
            }

            // ---------- 2. 主连接（带超时，避免现场 IP 不通时界面卡死） ----------
            TcpClient client = new TcpClient();

            try
            {
                IAsyncResult asyncResult = client.BeginConnect(_host, _port, null, null);

                if (!asyncResult.AsyncWaitHandle.WaitOne(CONNECT_TIMEOUT_MS))
                {
                    client.Close();
                    throw new TimeoutException("连接喷码机超时（" + _host + ":" + _port.ToString()
                                               + "，" + CONNECT_TIMEOUT_MS.ToString() + " 毫秒无响应）。");
                }

                client.EndConnect(asyncResult);
                client.ReceiveTimeout = 5000;
                client.SendTimeout = CONNECT_TIMEOUT_MS;

                _client = client;
                _stream = client.GetStream();
            }
            catch
            {
                // 连接失败：释放本次新建的资源，再原样抛出（此时 _client 尚未赋值，旧连接不受影响）
                client.Close();
                throw;
            }
        }

        /// <summary>
        /// 向预复位端口发 4 字节复位指令（07 01 01 2C）
        /// 【边界】复位失败仅 Debug 输出 —— 喷码机不支持复位端口时不应阻断正常连接。
        /// </summary>
        private void TryReset(int resetPort)
        {
            try
            {
                using (TcpClient resetClient = new TcpClient())
                {
                    IAsyncResult asyncResult = resetClient.BeginConnect(_host, resetPort, null, null);

                    if (!asyncResult.AsyncWaitHandle.WaitOne(2000))
                    {
                        resetClient.Close();
                        System.Diagnostics.Debug.WriteLine("TCP 预复位连接超时（端口 " + resetPort.ToString() + "），跳过复位继续主连接");
                        return;
                    }

                    resetClient.EndConnect(asyncResult);

                    using (NetworkStream resetStream = resetClient.GetStream())
                    {
                        byte[] resetFrame = CodeNetProtocol.BuildTcpResetFrame();
                        resetStream.Write(resetFrame, 0, resetFrame.Length);
                        resetStream.Flush();
                    }

                    // 稍等片刻让复位动作生效，再断开复位连接
                    // [2026-09-10] 硬编码等待统一降到 50ms
                    Thread.Sleep(50);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("TCP 预复位失败（不影响主连接）：" + ex.Message);
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
                NetworkStream? stream = _stream;
                if (stream == null)
                {
                    throw new InvalidOperationException("TCP 连接未建立，无法发送数据。");
                }

                stream.Write(data, 0, data.Length);
                stream.Flush();
            }
        }

        /// <summary>
        /// 读取原始字节。
        ///
        /// 【为什么用 Poll 而不是直接 Read】NetworkStream.Read 是阻塞的，没数据就死等；
        ///   接收线程是常驻循环，必须能周期性回来检查取消信号，否则停止流程 Join 会永久挂住。
        ///   Poll(超时) 就是"带超时的等待"：超时返回 0，表示本次无数据。
        ///   【注意】Poll 与心跳是两件事 —— 心跳（3 秒发 / 2 秒等）检测的是"对端是否存活"，
        ///   Poll 解决的是"本机这次读怎么按时返回"。拔网线时本机 socket 无任何事件，
        ///   Poll 只会一直返回"无数据"，静默失联只能靠心跳发现。两者不可互相替代。
        ///
        /// 【Poll 为什么必须在锁外（2026-09-10 万总开工，锁优化收尾）】
        ///   Poll 可能阻塞整个 timeoutMs，而连接锁由 Read / Write 共用 —— 若把等待放在锁内，
        ///   读方就长时间占着锁，写方（发码 / 心跳）每次都要白等一个 Poll 周期。
        ///   拆成三段：① 锁内取引用判空（微秒）→ ② 锁外 Poll 等待 → ③ 锁内真正读数据。
        ///   等待期间不碰流对象，所以不需要持锁；写方等锁时间降到约 0。
        ///
        /// 【第三段必须重新取字段】连接可能在本方法等待期间被 Close（Close 不加锁，
        ///   启动 / 重连 / 停止流程都会调）。所以第三段不能复用第一段的本地引用，
        ///   必须重新从字段取 _stream —— 取到 null 说明连接已关，抛异常由 BLL 兜住重试。
        ///
        /// 【断线语义不变】Poll 报可读但 Available=0 是 TCP 半关闭的标准特征 → 抛 IOException。
        /// 【超时语义不变】Poll 超时 → 返回 0，调用方按"本次无数据"处理。
        /// </summary>
        public int Read(byte[] buffer, int timeoutMs)
        {
            // ---------- 第一段（锁内）：取出 socket 引用并判空，不做任何阻塞等待 ----------
            // [2026-09-10] 必须给初值：C# 的明确赋值分析不认"在 lock 块内赋的值"，
            //   若写成 Socket socket; 再在 lock 内赋值、锁外使用，会报 CS0165（未赋值局部变量）。
            Socket? socket = null;

            lock (_ioLock)
            {
                TcpClient? client = _client;

                if (client == null || _stream == null)
                {
                    throw new InvalidOperationException("TCP 连接未建立，无法读取数据。");
                }

                socket = client.Client;
            }

            // 防御性判空（同时消除可空性告警）：上面已判空，正常不会走到这里。
            if (socket == null)
            {
                throw new InvalidOperationException("TCP 连接未建立，无法读取数据。");
            }

            // ---------- 第二段（锁外）：等待可读 ----------
            // 这段最长阻塞 timeoutMs，绝不能在锁内执行（会顶住写方）。
            // 若等待期间连接被 Close，这里会抛 ObjectDisposedException，由 BLL 的
            // ObjectDisposedException 分支处理（休眠后重试，等待重连换上新连接）。
            bool readable = socket.Poll(timeoutMs * 1000, SelectMode.SelectRead);

            if (!readable)
            {
                return 0;
            }

            // ---------- 第三段（锁内）：真正读数据 ----------
            // 重新从字段取流与连接：等待期间连接可能已被 Close（字段置 null、流被 Dispose），
            // 复用第一段的本地引用会读到已释放的对象。
            lock (_ioLock)
            {
                NetworkStream? stream = _stream;
                TcpClient? client = _client;

                if (stream == null || client == null)
                {
                    throw new InvalidOperationException("TCP 连接未建立，无法读取数据。");
                }

                // Poll 报可读但无数据可读 = 对端已关闭连接（TCP 半关闭）
                if (client.Available == 0)
                {
                    throw new IOException("喷码机已关闭 TCP 连接。");
                }

                return stream.Read(buffer, 0, buffer.Length);
            }
        }

        /// <summary>
        /// 关闭连接。重复调用安全，不抛异常。
        ///
        /// [2026-09-11] 关闭方式改造：优雅关闭 + 强制复位组合（万总开工，治"喷码机连接数耗尽"）
        /// 【背景】原实现 Close() 只发 FIN（优雅关闭），对端是否释放由喷码机固件决定 ——
        ///   现场真机反复重连后提示"达到最大连接数"，证明该固件收到 FIN 不主动释放（或
        ///   心跳超时场景下 FIN 根本没送达），旧连接在机器侧持续堆积占名额。
        /// 【三步关闭】
        ///   ① Shutdown(Both)：先礼貌通知对端"我说完了"（发 FIN）—— 保留通知语义；
        ///   ② LingerOption(true, 0)：给 socket 打 SO_LINGER 开启 + 超时 0 的标记 ——
        ///     这是 Windows 文档化行为：随后 Close 时内核发 RST 并立即销毁套接字（本机连
        ///     TIME_WAIT 都不进）；
        ///   ③ Close：按 ② 的标记发出 RST —— RST 是对端内核层强制动作，喷码机 TCP 栈
        ///     收到后无条件立即释放连接名额，不依赖固件是否处理 EOF。
        /// 【副作用】RST 丢弃本机发送缓冲中未发出的数据。四个关闭场景全部无害：
        ///   心跳超时重连（缓冲本就是废弃数据）/ 写失败重连（码已标已喷不重发）/
        ///   结束喷码（机内队列本来就要清）/ 程序退出（同前）。
        /// 【顺序约束】必须先 Shutdown / 设置 LingerState、再 Dispose 流 —— NetworkStream
        ///   的 Dispose 会连带释放 socket，之后就无法设置了。两步各自 try/catch：
        ///   连接已死时 Shutdown 抛 SocketException、已释放时抛 ObjectDisposedException，
        ///   均不得影响后续释放动作（参考万总提供的 DominoTcpClient.SafeClose 结构）。
        /// </summary>
        public void Close()
        {
            try
            {
                NetworkStream? stream = _stream;
                _stream = null;

                TcpClient? client = _client;
                _client = null;

                // ---------- ① ② ③：三步关闭（仅对真实存在的 socket 执行） ----------
                if (client != null)
                {
                    Socket? socket = client.Client;

                    if (socket != null)
                    {
                        // ① 礼貌通知：Shutdown(Both) 发 FIN；连接已死时抛异常，忽略
                        try
                        {
                            if (socket.Connected)
                            {
                                socket.Shutdown(SocketShutdown.Both);
                            }
                        }
                        catch (Exception)
                        {
                            // 已断开 / 已释放，无需通知
                        }

                        // ② 打 RST 标记：Linger(true, 0) —— 随后 Close 时内核发 RST 而非 FIN；
                        //   个别平台状态异常时可能抛异常，忽略后退化为普通 FIN 关闭（不比原来差）
                        try
                        {
                            socket.LingerState = new LingerOption(true, 0);
                        }
                        catch (Exception)
                        {
                            // 设置失败就按原优雅关闭，行为不低于改造前
                        }
                    }
                }

                // ③ Close：按 ② 的标记发出 RST（socket 已随 stream.Dispose 释放时此处自然安全）
                if (stream != null)
                {
                    stream.Dispose();
                }
                if (client != null)
                {
                    client.Close();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("关闭 TCP 连接异常（已忽略）：" + ex.Message);
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
