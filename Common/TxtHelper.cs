using System.Text;

namespace InkPrinterCode.Common
{
    /// <summary>
    /// [2026-09-10] 文本文件读取帮助类 —— 导入 txt 码数据专用
    ///
    /// 【文件格式（万总确认）】一行一个码，分隔符为回车换行，无表头，不含其他字段。
    ///
    /// 【机制】
    ///   - 用 StreamReader 逐行读，不用 File.ReadAllLines —— 后者会同时驻留"整个文件字符串"和
    ///     "行数组"两份数据，10 万行文件峰值内存翻倍且没有必要。
    ///   - FileShare.ReadWrite 打开：允许别的程序（比如用户正开着记事本看这个文件）同时持有文件，
    ///     不会因为文件被占用直接读失败。
    ///   - 每 2000 行检查一次取消信号，保证用户点"取消"后能较快停下来，
    ///     又不至于每行都检查 token 拖慢速度。
    ///
    /// 【编码】按 UTF-8 读并开启 BOM 自动识别（detectEncodingFromByteOrderMarks=true）：
    ///   带 BOM 的 UTF-8/UTF-16 文件自动按实际编码读；无 BOM 的按 UTF-8 读。
    ///   码值确认不含中文（纯数字字母），因此不需要 GBK 支持，也就不用额外引入 CodePages 包。
    ///
    /// 【边界】
    ///   - 路径为空 / 文件不存在 → 抛异常由 BLL 统一转成友好提示（这属于调用方传参错误，不能静默）；
    ///   - 空行照样计入总行数并加入结果集（保留行号对应关系），是否算无效由 ValidateHelper 判定；
    ///   - 用户取消 → 抛 OperationCanceledException，由 BLL 捕获后回滚并标记 Canceled。
    /// </summary>
    public static class TxtHelper
    {
        /// <summary>取消检查的行间隔</summary>
        private const int CANCEL_CHECK_STEP = 2000;

        /// <summary>
        /// 读取 txt 的所有行
        /// </summary>
        /// <param name="filePath">文件完整路径</param>
        /// <param name="lines">承接结果的集合（由调用方创建，方法内只往里 Add）</param>
        /// <param name="token">取消信号</param>
        /// <returns>读到的总行数（等于 lines 新增的条数）</returns>
        public static int ReadAllLines(string filePath, List<string> lines, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("txt 文件路径不能为空", nameof(filePath));
            }
            if (lines == null)
            {
                throw new ArgumentNullException(nameof(lines), "承接结果的集合不能为 null");
            }
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("待导入的 txt 文件不存在：" + filePath, filePath);
            }

            int totalRows = 0;
            FileStream? stream = null;
            StreamReader? reader = null;

            try
            {
                stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                reader = new StreamReader(stream, new UTF8Encoding(false), true);

                while (true)
                {
                    string? line = reader.ReadLine();
                    if (line == null)
                    {
                        break;
                    }

                    totalRows++;
                    lines.Add(line);

                    if (totalRows % CANCEL_CHECK_STEP == 0)
                    {
                        token.ThrowIfCancellationRequested();
                    }
                }
            }
            finally
            {
                // 各自释放各自的：StreamReader 会顺带关闭底层流，这里再显式关一次也是幂等的，
                // 保证 reader 创建失败时 stream 依然能被释放。
                if (reader != null)
                {
                    reader.Dispose();
                }
                if (stream != null)
                {
                    stream.Dispose();
                }
            }

            return totalRows;
        }
    }
}
