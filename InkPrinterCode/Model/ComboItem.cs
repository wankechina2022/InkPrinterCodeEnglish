namespace InkPrinterCode.Model
{
    /// <summary>
    /// [2026-09-10] Generic dropdown binding item -- used for a ComboBox's ValueMember / DisplayMember binding
    ///
    /// [Usage] cbo.DataSource = EnumHelper.GetPrintStatusItems(true);
    ///         cbo.ValueMember = "Value";
    ///         cbo.DisplayMember = "Text";
    ///
    /// [Why not KeyValuePair] KeyValuePair's members are read-only properties and their names (Key/Value) are
    ///   semantically unclear when binding to the UI; a small custom two-field class is more direct and easier to
    ///   extend later (for example adding a Tag field).
    ///
    /// [Boundary] Text defaults to an empty string rather than null, avoiding a null reference when binding to the
    ///   UI; ToString is overridden so that cases without DisplayMember still show text rather than the type name.
    /// </summary>
    public class ComboItem
    {
        /// <summary>Actual value (usually an enum's int value; -1 is conventionally "all")</summary>
        public int Value { get; set; } = 0;

        /// <summary>Text shown on the UI</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>
        /// Parameterless constructor -- used when the data binding framework creates an instance via reflection
        /// </summary>
        public ComboItem()
        {
        }

        /// <summary>
        /// Commonly used constructor
        /// </summary>
        /// <param name="value">Actual value</param>
        /// <param name="text">Display text; null is treated as an empty string</param>
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
        /// Return the display text, so the UI does not show the full type name when DisplayMember is not set
        /// </summary>
        public override string ToString()
        {
            return Text;
        }
    }
}
