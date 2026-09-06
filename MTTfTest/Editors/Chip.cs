namespace DevExpress.UITemplates.Collection.Editors {
    using System.ComponentModel;

    [TypeConverter(typeof(Converter))]
    public class Chip : ItemBase {
        public Chip()
            : base(null, string.Empty, null) { }
        public Chip(object value, [Localizable(true)] string caption)
            : base(value, caption, null) { }
        public Chip(object value, [Localizable(true)] string caption, object tag)
            : base(value, caption, tag) { }
        protected override ItemBase Clone() {
            return new Chip(Value, Caption, Tag);
        }
        #region internals
        public class Collection : Collection<Chip> {
            protected override Chip CreateItem(object value, string caption) {
                return new Chip(value, caption);
            }
        }
        public class Selection : Selection<Chip> { }
        public class Converter : Converter<Chip> { }
        #endregion internals
    }
}
