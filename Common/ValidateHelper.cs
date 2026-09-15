namespace InkPrinterCode.Common
{
    /// <summary>
    /// [2026-09-10] 校验帮助类 —— 集中放导入环节的码值清洗与合法性判断
    ///
    /// 【码值规则（万总 2026-09-10 确认）】
    ///   1. 清洗只做 Trim（去掉首尾空白，含空格、Tab、回车残留）；
    ///   2. Trim 后为空 → 空行，计入无效并跳过；
    ///   3. 含中文字符 → 直接忽略，计入无效并跳过（现场码值不会有中文，出现即视为脏数据）；
    ///   4. 不限制长度、不限制字符集 —— 除上面两条外一律放行。
    ///
    /// 【中文判定范围】
    ///   \u4E00-\u9FFF  CJK 统一汉字（覆盖常用汉字）
    ///   \u3400-\u4DBF  CJK 扩展 A（生僻字）
    ///   \u3000-\u303F  中文标点（含全角空格、书名号、顿号等）
    ///   \uFF00-\uFFEF  全角字符（全角括号、全角数字字母等）
    ///   这样既能挡住汉字，也能挡住"看起来像半角其实是全角"的脏数据。
    ///
    /// 【边界】所有方法接受 null 输入并做兜底，绝不抛空引用异常。
    /// </summary>
    public static class ValidateHelper
    {
        /// <summary>
        /// 清洗原始行 —— 只做 Trim
        /// </summary>
        /// <param name="rawLine">源文件中读到的原始内容，可为 null</param>
        /// <returns>Trim 后的字符串，null 输入返回空串</returns>
        public static string NormalizeCode(string? rawLine)
        {
            if (rawLine == null)
            {
                return string.Empty;
            }
            return rawLine.Trim();
        }

        /// <summary>
        /// 判断字符串是否包含中文 / 全角字符
        /// 【实现】传统 for 循环逐字符比较 Unicode 区间，10 万行也只是一次线性扫描，开销可忽略。
        /// </summary>
        /// <param name="value">待判断字符串，可为 null</param>
        /// <returns>包含返回 true；null 或空串返回 false</returns>
        public static bool ContainsChinese(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];

                if (c >= '\u4E00' && c <= '\u9FFF')
                {
                    return true;
                }
                if (c >= '\u3400' && c <= '\u4DBF')
                {
                    return true;
                }
                if (c >= '\u3000' && c <= '\u303F')
                {
                    return true;
                }
                if (c >= '\uFF00' && c <= '\uFFEF')
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 判断清洗后的码值是否有效
        /// </summary>
        /// <param name="trimmedValue">已经过 NormalizeCode 处理的码值</param>
        /// <param name="invalidReason">无效原因："空行" 或 "含中文字符"；有效时为空串</param>
        /// <returns>有效返回 true</returns>
        public static bool IsValidCode(string? trimmedValue, out string invalidReason)
        {
            invalidReason = string.Empty;

            if (string.IsNullOrEmpty(trimmedValue))
            {
                invalidReason = "空行";
                return false;
            }

            if (ContainsChinese(trimmedValue))
            {
                invalidReason = "含中文字符";
                return false;
            }

            return true;
        }

        /// <summary>
        /// 判断文件是否存在且可访问
        /// 【用途】导入前的存在性检查（开发规约：文件拷贝/删除/导入/导出前必须检查对象是否存在）。
        /// </summary>
        /// <param name="filePath">文件完整路径</param>
        /// <returns>路径非空且文件确实存在时返回 true</returns>
        public static bool IsFileExists(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return false;
            }

            try
            {
                return File.Exists(filePath);
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Warn("检查文件是否存在时出错：" + filePath + "，" + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 确保目录存在，不存在则创建
        /// 【用途】写库文件、导出 Excel、写日志前统一调用（开发规约：写文件前目录不存在自动创建）。
        /// </summary>
        /// <param name="folderPath">目录完整路径</param>
        /// <returns>目录已存在或创建成功返回 true；路径为空或创建失败返回 false</returns>
        public static bool EnsureFolderExists(string? folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return false;
            }

            try
            {
                if (Directory.Exists(folderPath))
                {
                    return true;
                }
                Directory.CreateDirectory(folderPath);
                return true;
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Error("创建目录失败：" + folderPath, ex);
                return false;
            }
        }
    }
}
