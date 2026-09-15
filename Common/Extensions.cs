using System.Data;
using System.Globalization;

namespace InkPrinterCode.Common
{
    /// <summary>
    /// [2026-09-10] 常用扩展方法 —— 安全类型转换 + 数据库时间格式互转
    ///
    /// 【设计取舍】没有提供规约示例里的反射版 ToList&lt;T&gt;()。
    ///   原因：本项目实体里有枚举属性（CodeData.PrintStatus），反射用 Convert.ChangeType 转枚举会抛异常，
    ///   处理起来反而更绕。DAL 一律手写 DataRow → 实体的映射，字段少、看得清、也不怕列名改动悄悄失效。
    ///
    /// 【时间格式约定】数据库所有时间列都存 TEXT，格式固定 "yyyy-MM-dd HH:mm:ss"。
    ///   写库：DateTime.ToDbTimeString()
    ///   读出：string.ToDateTimeOrDefault()
    ///   全项目只认这一种格式，不允许各处自己 ToString("...")，避免格式漂移导致时间区间筛选失效。
    /// </summary>
    public static class Extensions
    {
        /// <summary>数据库时间列的统一格式</summary>
        public const string DB_TIME_FORMAT = "yyyy-MM-dd HH:mm:ss";

        // ============================================================
        // 安全类型转换（面向 DataRow 取值，DBNull 全部兜底）
        // ============================================================

        /// <summary>
        /// 转 int，失败返回默认值
        /// </summary>
        public static int ToInt(this object? value, int defaultValue = 0)
        {
            if (value == null || value == DBNull.Value)
            {
                return defaultValue;
            }

            string text = value.ToString() ?? string.Empty;
            int result = 0;
            if (int.TryParse(text, out result))
            {
                return result;
            }
            return defaultValue;
        }

        /// <summary>
        /// 转 long，失败返回默认值
        /// 【用途】SQLite 的 INTEGER 主键读出来是 long，Id 类字段统一走这个方法。
        /// </summary>
        public static long ToLong(this object? value, long defaultValue = 0)
        {
            if (value == null || value == DBNull.Value)
            {
                return defaultValue;
            }

            string text = value.ToString() ?? string.Empty;
            long result = 0;
            if (long.TryParse(text, out result))
            {
                return result;
            }
            return defaultValue;
        }

        /// <summary>
        /// 转 decimal，失败返回默认值
        /// </summary>
        public static decimal ToDecimal(this object? value, decimal defaultValue = 0m)
        {
            if (value == null || value == DBNull.Value)
            {
                return defaultValue;
            }

            string text = value.ToString() ?? string.Empty;
            decimal result = 0m;
            if (decimal.TryParse(text, out result))
            {
                return result;
            }
            return defaultValue;
        }

        /// <summary>
        /// 转字符串，null / DBNull 返回默认值（默认空串）
        /// </summary>
        public static string ToSafeString(this object? value, string defaultValue = "")
        {
            if (value == null || value == DBNull.Value)
            {
                return defaultValue;
            }

            string? text = value.ToString();
            if (text == null)
            {
                return defaultValue;
            }
            return text;
        }

        /// <summary>
        /// 字符串截断，超长部分用省略号代替
        /// 【用途】日志里记录超长码值 / 喷码机返回报文时避免刷屏。
        /// </summary>
        public static string Truncate(this string? value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }
            if (maxLength <= 0)
            {
                return string.Empty;
            }
            if (value.Length <= maxLength)
            {
                return value;
            }
            return value.Substring(0, maxLength) + "...";
        }

        // ============================================================
        // 数据库时间格式互转
        // ============================================================

        /// <summary>
        /// DateTime → 数据库时间字符串（yyyy-MM-dd HH:mm:ss）
        /// </summary>
        public static string ToDbTimeString(this DateTime value)
        {
            return value.ToString(DB_TIME_FORMAT, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 数据库时间字符串 → DateTime
        ///
        /// 【双路径说明】
        ///   优先按固定格式 "yyyy-MM-dd HH:mm:ss" 精确解析（正常入库数据都是这个格式）；
        ///   精确解析失败时再退一步用宽松解析（DateTime.TryParse），兼容历史或手工改过的数据；
        ///   两者都失败返回 defaultValue（默认 DateTime.MinValue），不抛异常。
        /// </summary>
        /// <param name="value">时间字符串，可为 null</param>
        /// <param name="defaultValue">解析失败时的返回值，默认 DateTime.MinValue</param>
        public static DateTime ToDateTimeOrDefault(this string? value, DateTime? defaultValue = null)
        {
            DateTime fallback = DateTime.MinValue;
            if (defaultValue.HasValue)
            {
                fallback = defaultValue.Value;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            string text = value.Trim();

            DateTime exactResult;
            bool exactOk = DateTime.TryParseExact(text, DB_TIME_FORMAT,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out exactResult);
            if (exactOk)
            {
                return exactResult;
            }

            DateTime looseResult;
            bool looseOk = DateTime.TryParse(text, out looseResult);
            if (looseOk)
            {
                return looseResult;
            }

            return fallback;
        }

        /// <summary>
        /// 取当前时间的数据库格式字符串（等价 DateTime.Now.ToDbTimeString()）
        /// </summary>
        public static string NowDbTimeString()
        {
            return DateTime.Now.ToDbTimeString();
        }
    }
}
