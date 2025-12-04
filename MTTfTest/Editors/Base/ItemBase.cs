namespace DevExpress.UITemplates.Collection.Editors {
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Collections.Specialized;
    using System.ComponentModel;
    using System.ComponentModel.Design.Serialization;
    using System.Globalization;
    using DevExpress.XtraEditors;

    public abstract class ItemBase : ICloneable {
        object _value;
        string _caption;
        object _tag;
        protected ItemBase(object value, string caption, object tag) {
            this._value = value;
            this._tag = tag;
            this._caption = caption ?? string.Empty;
        }
        [Editor(typeof(DevExpress.Utils.Editors.UIObjectEditor), typeof(System.Drawing.Design.UITypeEditor)), Category(CategoryName.Data)]
        public virtual object Value {
            get { return _value; }
            set {
                if(Value == value) return;
                _value = value;
                OnItemChanged();
            }
        }
        [Localizable(true), Category(CategoryName.Appearance)]
        public virtual string Caption {
            get { return _caption; }
            set {
                if(value == null) value = string.Empty;
                if(Caption == value) return;
                _caption = value;
                OnItemChanged();
            }
        }
        [DefaultValue(null), Editor(typeof(DevExpress.Utils.Editors.UIObjectEditor), typeof(System.Drawing.Design.UITypeEditor)), Category(CategoryName.Data)]
        public virtual object Tag {
            get { return _tag; }
            set { _tag = value; }
        }
        object ICloneable.Clone() {
            return Clone();
        }
        protected abstract ItemBase Clone();
        public event EventHandler Changed;
        protected virtual void OnItemChanged() {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        public override string ToString() {
            return Caption;
        }
        [ListBindable(false)]
        public abstract class Collection<TItem> : CollectionBase, IEnumerable<TItem>, INotifyCollectionChanged
            where TItem : ItemBase {
            public event NotifyCollectionChangedEventHandler CollectionChanged;
            public TItem this[int index] {
                get { return List[index] as TItem; }
                set { List[index] = value; }
            }
            public HashSet<object> GetValues(ICollection<TItem> selection) {
                HashSet<object> values = new HashSet<object>();
                foreach(TItem item in List) {
                    if(selection.Contains(item))
                        values.Add(item.Value);
                }
                return values;
            }
            public TItem GetItemByValue(object value) {
                foreach(TItem item in List) {
                    if(Equals(value, item.Value))
                        return item;
                }
                return null;
            }
            public int GetItemIndexByValue(object value) {
                for(int i = 0; i < Count; i++) {
                    TItem item = this[i];
                    if(Equals(value, item.Value))
                        return i;
                }
                return -1;
            }
            public virtual void Assign(Collection<TItem> collection) {
                if(collection == null)
                    return;
                BeginUpdate();
                try {
                    Clear();
                    for(int n = 0; n < collection.Count; n++)
                        Add((TItem)collection[n].Clone());
                }
                finally { EndUpdate(); }
            }
            public void AddEnum(Type enumType, bool addEnumeratorIntegerValues = false, Converter<object, string> displayTextConverter = null) {
                if(displayTextConverter == null)
                    displayTextConverter = (x) => EnumDisplayTextHelper.GetDisplayText(x);
                BeginUpdate();
                try {
                    Array values = GetEnumValues(enumType);
                    foreach(object enumValue in values) {
                        object value = GetEnumValue(addEnumeratorIntegerValues, enumValue, enumType);
                        Add(CreateItem(value, displayTextConverter(enumValue)));
                    }
                }
                finally { EndUpdate(); }
            }
            public void AddEnum<TEnum>(bool addEnumeratorIntegerValues = false, Converter<TEnum, string> displayTextConverter = null) {
                if(displayTextConverter == null)
                    displayTextConverter = (x) => EnumDisplayTextHelper.GetDisplayText(x);
                BeginUpdate();
                try {
                    var values = GetEnumValues(typeof(TEnum));
                    foreach(TEnum enumValue in values) {
                        object value = GetEnumValue(addEnumeratorIntegerValues, enumValue, typeof(TEnum));
                        Add(CreateItem(value, displayTextConverter(enumValue)));
                    }
                }
                finally { EndUpdate(); }
            }
            static Array GetEnumValues(Type enumType) {
                enumType = Nullable.GetUnderlyingType(enumType) ?? enumType;
                if(enumType.IsEnum)
                    return Enum.GetValues(enumType);
                return Array.CreateInstance(enumType, 0);
            }
            static object GetEnumValue(bool addEnumeratorIntegerValues, object val, Type enumType) {
                if(addEnumeratorIntegerValues) {
                    Type enumValueType = Enum.GetUnderlyingType(enumType);
                    try { return Convert.ChangeType(val, enumValueType); }
                    catch { }
                }
                return val;
            }
            public virtual void AddRange(TItem[] items) {
                BeginUpdate();
                try {
                    foreach(TItem item in items)
                        Add(item);
                }
                finally { EndUpdate(); }
            }
            public virtual void Add(TItem item) {
                List.Add(item);
            }
            public void Add(object value, string caption) {
                List.Add(CreateItem(value, caption));
            }
            public virtual void Insert(int index, TItem item) {
                List.Insert(index, item);
            }
            public virtual void Remove(TItem item) {
                List.Remove(item);
            }
            public virtual void RemoveValue(object value) {
                bool hasRemoved = false;
                BeginUpdate();
                try {
                    for(int i = Count - 1; i >= 0; i--) {
                        TItem item = this[i];
                        if(Equals(value, item.Value)) {
                            hasRemoved = true;
                            List.RemoveAt(i);
                        }
                    }
                }
                finally {
                    if(hasRemoved)
                        EndUpdate();
                    else CancelUpdate();
                }
            }
            public int IndexOf(TItem item) {
                return List.IndexOf(item);
            }
            int lockUpdate;
            public virtual void BeginUpdate() {
                lockUpdate++;
            }
            public virtual void EndUpdate() {
                if(--lockUpdate == 0)
                    RaiseCollectionChanged(ResetArgs);
            }
            [EditorBrowsable(EditorBrowsableState.Advanced)]
            public virtual void CancelUpdate() {
                lockUpdate--;
            }
            readonly static NotifyCollectionChangedEventArgs ResetArgs = new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset);
            protected override void OnInsertComplete(int index, object value) {
                TItem item = value as TItem;
                item.Changed += OnItemChanged;
                RaiseCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, value));
            }
            protected override void OnRemoveComplete(int index, object value) {
                TItem item = value as TItem;
                item.Changed -= OnItemChanged;
                RaiseCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, value));
            }
            protected override void OnSetComplete(int index, object oldValue, object newValue) {
                TItem item = oldValue as TItem;
                item.Changed -= OnItemChanged;
                item = newValue as TItem;
                item.Changed += OnItemChanged;
                RaiseCollectionChanged(ResetArgs);
            }
            protected override void OnClear() {
                if(Count == 0)
                    return;
                BeginUpdate();
                try {
                    for(int n = Count - 1; n >= 0; n--)
                        RemoveAt(n);
                }
                finally { EndUpdate(); }
            }
            protected override void OnClearComplete() {
                RaiseCollectionChanged(ResetArgs);
            }
            protected virtual void OnItemChanged(object sender, EventArgs e) {
                RaiseCollectionChanged(ResetArgs);
            }
            protected virtual void RaiseCollectionChanged(NotifyCollectionChangedEventArgs e) {
                if(lockUpdate == 0) CollectionChanged?.Invoke(this, e);
            }
            IEnumerator<TItem> IEnumerable<TItem>.GetEnumerator() {
                foreach(TItem item in InnerList)
                    yield return item;
            }
            protected abstract TItem CreateItem(object value, string caption);
        }
        [ListBindable(false)]
        public class Selection<TItem> : HashSet<TItem>, ICollection<object>
            where TItem : ItemBase {
            public bool ContainsValue(object value) {
                foreach(TItem item in this)
                    if(Equals(item.Value, value)) return true;
                return false;
            }
            bool ICollection<object>.IsReadOnly {
                get { return ((ICollection<TItem>)this).IsReadOnly; }
            }
            bool ICollection<object>.Contains(object item) {
                return Contains((TItem)item);
            }
            void ICollection<object>.Add(object item) {
                Add((TItem)item);
            }
            bool ICollection<object>.Remove(object item) {
                return Remove((TItem)item);
            }
            void ICollection<object>.CopyTo(object[] array, int arrayIndex) {
                var selectedItems = new TItem[Count - arrayIndex];
                CopyTo(selectedItems, 0, selectedItems.Length);
                Array.Copy(array, arrayIndex, selectedItems, 0, selectedItems.Length);
            }
            IEnumerator<object> IEnumerable<object>.GetEnumerator() {
                return GetEnumerator();
            }
        }
        [EditorBrowsable(EditorBrowsableState.Never)]
        public abstract class Converter<TItem> : ExpandableObjectConverter
            where TItem : ItemBase {
            public override bool CanConvertTo(ITypeDescriptorContext context, Type destinationType) {
                if(destinationType == typeof(InstanceDescriptor))
                    return true;
                return base.CanConvertTo(context, destinationType);
            }
            public override object ConvertTo(ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType) {
                if(destinationType == null)
                    throw new ArgumentNullException("destinationType");
                if(destinationType.Equals(typeof(InstanceDescriptor)))
                    return GetInstanceDescriptor((TItem)value);
                return base.ConvertTo(context, culture, value, destinationType);
            }
            protected virtual InstanceDescriptor GetInstanceDescriptor(TItem item) {
                if(item.Value == null && item.Caption == string.Empty && item.Tag == null) {
                    var ctor = typeof(TItem).GetConstructor(Type.EmptyTypes);
                    return new InstanceDescriptor(ctor, new object[] { });
                }
                if(item.Tag != null) {
                    var ctor = typeof(TItem).GetConstructor(new Type[] { typeof(object), typeof(string), typeof(object) });
                    var parameters = new object[] { item.Value, item.Caption, item.Tag };
                    return new InstanceDescriptor(ctor, parameters);
                }
                else {
                    var ctor = typeof(TItem).GetConstructor(new Type[] { typeof(object), typeof(string) });
                    var parameters = new object[] { item.Value, item.Caption };
                    return new InstanceDescriptor(ctor, parameters);
                }
            }
        }
    }
}
