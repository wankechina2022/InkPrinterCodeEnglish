namespace InkPrinterCode.Model
{
    /// <summary>
    /// [2026-09-10] 下拉框通用绑定项 —— 给 ComboBox 做 ValueMember / DisplayMember 绑定用
    ///
    /// 【用法】cbo.DataSource = EnumHelper.GetPrintStatusItems(true);
    ///         cbo.ValueMember = "Value";
    ///         cbo.DisplayMember = "Text";
    ///
    /// 【为什么不用 KeyValuePair】KeyValuePair 的成员是只读属性，且名字（Key/Value）在界面绑定时
    ///   语义不清；自定义一个两字段小类更直白，也方便以后扩展（比如加个 Tag 字段）。
    ///
    /// 【边界】Text 默认空串而非 null，避免绑定到界面时出现空引用；
    ///   重写 ToString 让未绑定 DisplayMember 的场合也能正常显示文字，而不是显示类名。
    /// </summary>
    public class ComboItem
    {
        /// <summary>实际值（一般是枚举的 int 值；-1 约定为"全部"）</summary>
        public int Value { get; set; } = 0;

        /// <summary>界面显示文字</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>
        /// 无参构造 —— 供数据绑定框架反射创建实例时使用
        /// </summary>
        public ComboItem()
        {
        }

        /// <summary>
        /// 常用构造
        /// </summary>
        /// <param name="value">实际值</param>
        /// <param name="text">显示文字，传 null 时按空串处理</param>
        public ComboItem(int value, string text)
        {
            Value = value;
            if (text == null)
            {
                Text = string.Empty;
            }
            else
            {
                Text = text;
            }
        }

        /// <summary>
        /// 返回显示文字，避免未设置 DisplayMember 时界面显示成类型全名
        /// </summary>
        public override string ToString()
        {
            return Text;
        }
    }
}
