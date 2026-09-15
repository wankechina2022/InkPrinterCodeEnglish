using System.Text;

namespace InkPrinterCode.Common
{
    /// <summary>
    /// [2026-09-10] CodeNet 协议组帧与应答解析（阶段二第 2 轮新增，万总指定的独立文件）
    ///
    /// 【职责边界（万总确认的结构）】
    ///   本类只做两件事：①把指令/码值组装成字节帧；②把收到的字节流解析成帧。
    ///   不持有连接、不写数据库、不做调度 —— 连接在 TcpPrinterConnection / SerialPrinterConnection，
    ///   调度状态机在 PrintServiceBLL。
    ///
    /// 【协议依据】《1-A系列CodeNet常用指令介绍.pdf》（TSG 2016.10）—— 指令流程与应答判定一律以 PDF 为准；
    ///   参考代码 DominoPrinter.cs 仅验证了组帧格式（详见方案文档 1.8 逐字节对照）。
    ///
    /// 【组帧实证结论（方案 v4）】长度字段固定 4 位十进制前补零（PDF 口径）——
    ///   与参考代码在 ≥10 位码（现场主力区间）上字节级一致，短码则避免了参考代码 "007" 的错位隐患。
    ///
    /// 【三条反馈流的字节特征（解析分流依据，万总强调查询指令绝不能与打印指令混淆）】
    ///   ACK 流        ：单字节 0x06（成功）/ 0x15 开头（拒收）—— 仅记日志，不作状态判定（取码占用制）
    ///   打印完成事件流：单字节 0x32 —— 触发发码
    ///   状态帧流      ：1B 31 43 SSS B HHMM 04（12 字节）—— 心跳探活 + 状态码
    ///   计数帧流      ：1B 54 31 + 10 位数字 + 04（14 字节）—— 备用对账
    /// </summary>
    public static class CodeNetProtocol
    {
        // ============================================================
        // 协议常量
        // ============================================================

        /// <summary>帧头 0x1B（ESC）</summary>
        public const byte ESC = 0x1B;

        /// <summary>帧尾 0x04（EOT）</summary>
        public const byte EOT = 0x04;

        /// <summary>应答：接收成功 0x06（ACK）</summary>
        public const byte ACK = 0x06;

        /// <summary>应答：拒收/格式错 0x15（NAK）开头</summary>
        public const byte NAK = 0x15;

        /// <summary>打印完成事件：单字节 0x32（由「打印信号设置」指令约定）</summary>
        public const byte PRINT_DONE = 0x32;

        /// <summary>状态帧长度：1B 31 43 SSS B HHMM 04 = 12 字节</summary>
        public const int STATUS_FRAME_LENGTH = 12;

        /// <summary>计数帧长度：1B 54 31 + 10 位数字 + 04 = 14 字节</summary>
        public const int COUNT_FRAME_LENGTH = 14;

        // ============================================================
        // 组帧（发送方向）
        // ============================================================

        /// <summary>
        /// 组「打印信号设置」帧：1B 49 31 32 04（ESC 'I' '1' '2' EOT）
        /// 【作用】设定喷头 1、打印完成后回传 0x32 —— 不发这条，喷码机不会推 0x32，发码机制不成立
        ///   （参考代码正是缺了这条，见方案 1.8）。
        /// </summary>
        public static byte[] BuildSignalSetupFrame()
        {
            return new byte[] { 0x1B, 0x49, 0x31, 0x32, 0x04 };
        }

        /// <summary>
        /// 组「清缓存队列」帧：1B 4F 45 + "0000" + 队列号 + 04
        /// 【队列号】0 = TCP/IP 队列；1 = RS232 队列；2 = 历史队列（PDF 3.1，三条都要清）。
        /// </summary>
        /// <param name="queueIndex">队列号 0/1/2，越界按 0 处理</param>
        public static byte[] BuildClearQueueFrame(int queueIndex)
        {
            if (queueIndex < 0 || queueIndex > 2)
            {
                queueIndex = 0;
            }

            string payload = "0000" + queueIndex.ToString();
            return BuildOeFrame(payload);
        }

        /// <summary>
        /// 组「发缓存数据」帧：1B 4F 45 + 4 位补零长度 + 码值 ASCII + 04
        /// 【例】发 AB12345 → 1B 4F 45 30303037 41423132333435 04（长度 7 → "0007"）。
        /// 【前提】喷码机已设好动态文本模板，否则指令无效（回 0x15）。
        /// </summary>
        /// <param name="codeValue">码值（只发文本，不做一维码/二维码封装）</param>
        public static byte[] BuildCacheDataFrame(string codeValue)
        {
            string text = codeValue ?? string.Empty;

            // 4 位十进制前补零长度（PDF 口径，见类注释的实证结论）
            string lengthText = text.Length.ToString("D4");
            string payload = lengthText + text;

            return BuildOeFrame(payload);
        }

        /// <summary>组「查询机器状态」帧：1B 31 43 3F 04 —— 心跳指令（PDF 4.5）</summary>
        public static byte[] BuildQueryStatusFrame()
        {
            return new byte[] { 0x1B, 0x31, 0x43, 0x3F, 0x04 };
        }

        /// <summary>组「查询喷头打印个数」帧：1B 54 31 3F 04（PDF 4.6，备用对账）</summary>
        public static byte[] BuildQueryCountFrame()
        {
            return new byte[] { 0x1B, 0x54, 0x31, 0x3F, 0x04 };
        }

        /// <summary>组「清零喷头计数」帧：1B 54 31 30 04（PDF 4.6，备用）</summary>
        public static byte[] BuildResetCountFrame()
        {
            return new byte[] { 0x1B, 0x54, 0x31, 0x30, 0x04 };
        }

        /// <summary>
        /// 组 TCP 预复位帧：07 01 01 2C（4 字节）
        /// 【来源】参考代码 Close() 里的现场经验 —— 连接数据端口前先向 700 端口复位一次更稳定。
        /// </summary>
        public static byte[] BuildTcpResetFrame()
        {
            return new byte[] { 0x07, 0x01, 0x01, 0x2C };
        }

        /// <summary>
        /// 组 OE 类帧的公共骨架：1B 4F 45 + payload ASCII + 04
        /// （发缓存数据与清缓存队列共用，对应参考代码 FormatString 的组帧方式）
        /// </summary>
        private static byte[] BuildOeFrame(string payload)
        {
            byte[] payloadBytes = Encoding.ASCII.GetBytes(payload);

            byte[] frame = new byte[3 + payloadBytes.Length + 1];
            frame[0] = ESC;
            frame[1] = 0x4F;    // 'O'
            frame[2] = 0x45;    // 'E'
            Array.Copy(payloadBytes, 0, frame, 3, payloadBytes.Length);
            frame[frame.Length - 1] = EOT;

            return frame;
        }

        // ============================================================
        // 解帧（接收方向）
        // ============================================================

        /// <summary>
        /// 尝试把缓冲区头部解析成「状态帧」（心跳应答）：1B 31 43 SSS B HHMM 04
        /// 【调用约定】缓冲区首字节已是 0x1B；缓冲区不足帧长时返回 false（等待更多字节）。
        /// </summary>
        /// <param name="buffer">接收缓冲区（不修改内容，由调用方决定消费几个字节）</param>
        /// <param name="statusCode">输出：3 位状态码 SSS（000=就绪正常，1xx 警告，2xx 打印禁止，100 故障）</param>
        /// <param name="frameText">输出：帧的可读描述（如 "1C 000 B 1432"），解析失败为空串</param>
        /// <returns>true = 是完整状态帧且解析成功（调用方应消费 STATUS_FRAME_LENGTH 字节）</returns>
        public static bool TryParseStatusFrame(List<byte> buffer, out int statusCode, out string frameText)
        {
            statusCode = -1;
            frameText = string.Empty;

            if (buffer == null || buffer.Count < STATUS_FRAME_LENGTH)
            {
                return false;
            }

            // 帧头特征：1B 31 43（ESC '1' 'C'）；帧尾 04
            if (buffer[0] != ESC || buffer[1] != 0x31 || buffer[2] != 0x43 || buffer[11] != EOT)
            {
                return false;
            }

            // SSS 三位必须是 ASCII 数字，否则当垃圾帧处理
            int hundreds = buffer[3] - 0x30;
            int tens = buffer[4] - 0x30;
            int ones = buffer[5] - 0x30;

            if (hundreds < 0 || hundreds > 9 || tens < 0 || tens > 9 || ones < 0 || ones > 9)
            {
                return false;
            }

            statusCode = hundreds * 100 + tens * 10 + ones;
            frameText = "1C " + buffer[3].ToString() + buffer[4].ToString() + buffer[5].ToString()
                        + " " + ((char)buffer[6]).ToString()
                        + " " + ((char)buffer[7]).ToString() + ((char)buffer[8]).ToString()
                        + ((char)buffer[9]).ToString() + ((char)buffer[10]).ToString();

            return true;
        }

        /// <summary>
        /// 尝试把缓冲区头部解析成「计数帧」：1B 54 31 + 10 位数字 + 04
        /// 【用途】喷头打印个数查询的应答，阶段二仅记日志做备用对账，不做业务判定。
        /// </summary>
        /// <param name="buffer">接收缓冲区</param>
        /// <param name="count">输出：打印个数</param>
        /// <returns>true = 是完整计数帧且解析成功（调用方应消费 COUNT_FRAME_LENGTH 字节）</returns>
        public static bool TryParseCountFrame(List<byte> buffer, out long count)
        {
            count = -1;

            if (buffer == null || buffer.Count < COUNT_FRAME_LENGTH)
            {
                return false;
            }

            // 帧头特征：1B 54 31（ESC 'T' '1'）；帧尾 04
            if (buffer[0] != ESC || buffer[1] != 0x54 || buffer[2] != 0x31 || buffer[13] != EOT)
            {
                return false;
            }

            // 10 位数字逐位累加（不用 int.Parse 整段转，避免个别位非数字时抛异常）
            long value = 0;
            for (int i = 3; i <= 12; i++)
            {
                int digit = buffer[i] - 0x30;
                if (digit < 0 || digit > 9)
                {
                    return false;
                }
                value = value * 10 + digit;
            }

            count = value;
            return true;
        }

        // ============================================================
        // 日志辅助
        // ============================================================

        /// <summary>
        /// 字节数组转十六进制文本（未知字节原样入日志用）
        /// 【例】{0x1B, 0x0D} → "1B 0D"
        /// </summary>
        public static string ToHexString(byte[] data, int length)
        {
            if (data == null || length <= 0)
            {
                return string.Empty;
            }

            if (length > data.Length)
            {
                length = data.Length;
            }

            StringBuilder builder = new StringBuilder(length * 3);

            for (int i = 0; i < length; i++)
            {
                if (i > 0)
                {
                    builder.Append(' ');
                }
                builder.Append(data[i].ToString("X2"));
            }

            return builder.ToString();
        }

        /// <summary>单字节转十六进制文本（粘包场景逐字节记日志用）</summary>
        public static string ToHexByte(byte value)
        {
            return value.ToString("X2");
        }
    }
}
