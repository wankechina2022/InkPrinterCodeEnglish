using System.Data;
using System.Globalization;

namespace InkPrinterCode.Common
{
    /// <summary>
    /// [2026-09-10] Common extension methods —— safe type conversion + conversion between the database time format and DateTime
    ///
    /// [Design trade-off] The reflective ToList&lt;T&gt;() from the standard's example was not provided.
    ///   Reason: this project's entities have enum properties (CodeData.PrintStatus), and using reflection's
    ///   Convert.ChangeType to convert to an enum throws, which actually makes the handling more convoluted. The DAL
    ///   always hand-writes the DataRow → entity mapping; there are few fields, it is easy to read, and there is no
    ///   fear of a column-name change silently breaking it.
    ///
    /// [Time format convention] Every time column in the database is stored as TEXT with the fixed format
    ///   "yyyy-MM-dd HH:mm:ss".
    ///   Writing to the database: DateTime.ToDbTimeString()
    ///   Reading out:           string.ToDateTimeOrDefault()
    ///   The whole project recognizes only this one format; no place is allowed to write its own ToString("..."),
    ///   which would avoid format drift breaking time-range filtering.
    /// </summary>
    public static class Extensions
    {
        /// <summary>The unified format for database time columns</summary>
        public const string DB_TIME_FORMAT = "yyyy-MM-dd HH:mm:ss";

        // ============================================================
        // Safe type conversion (for reading DataRow values; DBNull is always handled)
        // ============================================================

        /// <summary>
        /// Convert to int; returns the default value on failure
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
        /// Convert to long; return the default value on failure
        /// [Purpose] SQLite INTEGER primary keys read back as long; all Id-type fields go through this method for consistency.
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
        /// Convert to decimal; returns the default value on failure
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
        /// Convert to string; null / DBNull returns the default value (empty string by default)
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
        /// Truncate a string, replacing the over-long part with an ellipsis
        /// [Purpose] Avoids flooding the log when recording over-long code values / inkjet printer reply payloads.
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
        // Conversion between the database time format and DateTime
        // ============================================================

        /// <summary>
        /// DateTime → database time string (yyyy-MM-dd HH:mm:ss)
        /// </summary>
        public static string ToDbTimeString(this DateTime value)
        {
            return value.ToString(DB_TIME_FORMAT, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Database time string → DateTime
        ///
        /// [Dual-path note]
        ///   It first tries exact parsing with the fixed format "yyyy-MM-dd HH:mm:ss" (normally stored data is all in
        ///   this format);
        ///   If exact parsing fails, it falls back to loose parsing (DateTime.TryParse) for compatibility with
        ///   historical or manually edited data;
        ///   If both fail it returns defaultValue (DateTime.MinValue by default) and does not throw.
        /// </summary>
        /// <param name="value">Time string, may be null</param>
        /// <param name="defaultValue">The value returned when parsing fails; DateTime.MinValue by default</param>
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
        /// Get the current time as a database-format string (equivalent to DateTime.Now.ToDbTimeString())
        /// </summary>
        public static string NowDbTimeString()
        {
            return DateTime.Now.ToDbTimeString();
        }
    }
}
