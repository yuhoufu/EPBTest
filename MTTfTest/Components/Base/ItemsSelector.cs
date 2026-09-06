namespace DevExpress.UITemplates.Collection.Components {
    using System.Collections;
    using System.Collections.Generic;
    using System.Collections.Specialized;
    using System.ComponentModel;
    using System.Linq;
    using DevExpress.Utils;
    using DevExpress.Utils.Html;
    using DevExpress.Utils.Html.Internal;

    public sealed class ItemsSelector : DxHtmlComponent {
        readonly static ICollection EmptyCollection = new object[0];
        public static void Register() {
            DxHtmlDocument.Define<ItemsSelector>("items-selector");
            DxHtmlDocument.Define<Item>("items-selector-item");
        }
        public static TItem GetItem<TItem>(DxHtmlElementBase element)
            where TItem : Editors.ItemBase {
            var parent = element.ParentElement;
            while(parent != null) {
                if(parent.TagName == "items-selector-item")
                    return parent.GetAttribute(nameof(Item.ItemObject)) as TItem;
                parent = parent.ParentElement;
            }
            return null;
        }
        //
        public interface IItemContentProvider {
            bool OnCreateItemContent(DxHtmlRootElement root, Item item);
        }
        public enum Size {
            Small = -1,
            Default = 0,
            Large = 1,
        }
        public ICollection Items {
            get { return GetAttribute(nameof(Items)) as ICollection; }
        }
        public object SelectedItem {
            get { return GetAttribute(nameof(SelectedItem)); }
        }
        public ICollection<object> Selection {
            get { return GetAttribute(nameof(Selection)) as ICollection<object> ?? selectionCore; }
        }
        public bool IsMultiSelect {
            get {
                object value = GetAttribute(nameof(IsMultiSelect));
                return value is bool && (bool)value;
            }
        }
        public bool? IsMandatorySelection {
            get {
                object value = GetAttribute(nameof(IsMandatorySelection));
                return value is bool ? new bool?((bool)value) : null;
            }
        }
        public string ItemClass {
            get { return GetAttribute(nameof(ItemClass)) as string ?? string.Empty; }
        }
        public string ItemTemplate {
            get { return GetAttribute(nameof(ItemTemplate)) as string ?? string.Empty; }
        }
        public Size? ItemSize {
            get {
                object value = GetAttribute(nameof(ItemSize));
                if(value is int)
                    return new Size?((Size)(int)value);
                if(value is string) {
                    if(string.Equals((string)value, nameof(Size.Small), System.StringComparison.OrdinalIgnoreCase))
                        return Size.Small;
                    if(string.Equals((string)value, nameof(Size.Default), System.StringComparison.OrdinalIgnoreCase))
                        return Size.Default;
                    if(string.Equals((string)value, nameof(Size.Large), System.StringComparison.OrdinalIgnoreCase))
                        return Size.Large;
                }
                return value is Size ? new Size?((Size)value) : null;
            }
        }
        readonly HashSet<object> selectionCore = new HashSet<object>();
        ICollection itemsCollection;
        public override void ConnectedCallback() {
            base.ConnectedCallback();
            if(string.IsNullOrEmpty(Element.ClassName))
                Element.ClassName = "items-selector";
            CreateItems(EnsureItemsCollection(), ItemClass, ItemSize);
            SetAttribute("self", this);
        }
        public override void DisconnectedCallback() {
            base.DisconnectedCallback();
            if(itemsCollection != null)
                Unsubscribe(itemsCollection);
            itemsCollection = null;
        }
        public override void AttributeChangedCallback(string attributeName) {
            base.AttributeChangedCallback(attributeName);
            if(string.Equals(attributeName, nameof(Items), System.StringComparison.OrdinalIgnoreCase)) {
                ResetItems();
                return;
            }
            if(string.Equals(attributeName, nameof(ItemSize), System.StringComparison.OrdinalIgnoreCase)) {
                UpdateItemSizes(ItemClass, ItemSize);
                return;
            }
            if(lockSelection == 0) {
                if(string.Equals(attributeName, nameof(SelectedItem), System.StringComparison.OrdinalIgnoreCase)) {
                    SyncSelection(Selection, SelectedItem, IsMultiSelect);
                    UpdateItemsSelection(Selection);
                    return;
                }
                if(string.Equals(attributeName, nameof(Selection), System.StringComparison.OrdinalIgnoreCase)) {
                    UpdateItemsSelection(Selection);
                    return;
                }
            }
        }
        public void UpdateSelection() {
            UpdateItemsSelection(Selection);
        }
        internal IItemContentProvider ItemContentProvider {
            get { return GetAttribute(nameof(ItemContentProvider)) as IItemContentProvider; }
        }
        internal bool OnCreateItemContent(Item item) {
            string itemTemplateId = ItemTemplate;
            if(!string.IsNullOrEmpty(itemTemplateId)) {
                var template = Element.RootElement.FindTemplate(itemTemplateId);
                if(template != null)
                    item.SetContent(template);
                return (template != null);
            }
            return TryCreateItemContent(item);
        }
        bool TryCreateItemContent(Item item) {
            var itemContentProvider = GetAttribute(nameof(ItemContentProvider)) as IItemContentProvider;
            return (itemContentProvider != null) && itemContentProvider.OnCreateItemContent(Element.RootElement, item);
        }
        public void ResetItems(bool resetSelection = true) {
            ClearChildren();
            if(resetSelection) selectionCore.Clear();
            CreateItems(EnsureItemsCollection(), ItemClass, ItemSize);
        }
        ICollection EnsureItemsCollection() {
            if(itemsCollection == null) {
                itemsCollection = GetAttribute(nameof(Items)) as ICollection;
                if(itemsCollection != null)
                    Subscribe(itemsCollection);
            }
            return itemsCollection ?? EmptyCollection;
        }
        void Subscribe(ICollection collection) {
            var bList = collection as IBindingList;
            if(bList != null)
                bList.ListChanged += OnBindingChanged;
            var incc = collection as INotifyCollectionChanged;
            if(incc != null)
                incc.CollectionChanged += OnCollectionChanged;
        }
        void Unsubscribe(ICollection collection) {
            var bList = collection as IBindingList;
            if(bList != null)
                bList.ListChanged -= OnBindingChanged;
            var incc = collection as INotifyCollectionChanged;
            if(incc != null)
                incc.CollectionChanged -= OnCollectionChanged;
        }
        void OnBindingChanged(object sender, ListChangedEventArgs e) {
            if(e.ListChangedType == ListChangedType.Reset)
                ResetItems();
        }
        void OnCollectionChanged(object sender, NotifyCollectionChangedEventArgs e) {
            if(e.Action == NotifyCollectionChangedAction.Reset)
                ResetItems();
        }
        bool SyncSelection(ICollection<object> selection, object itemObject, bool isMultiSelect) {
            if(!isMultiSelect)
                return SyncSingleSelection(selection, itemObject, IsMandatorySelection);
            else {
                if(!selection.Remove(itemObject))
                    selection.Add(itemObject);
                return true;
            }
        }
        bool SyncSingleSelection(ICollection<object> selection, object itemObject, bool? isMandatory) {
            object selectedBefore = selection.FirstOrDefault();
            if(!isMandatory.HasValue) {
                selection.Clear();
                if(!IsControlPressed)
                    selection.Add(itemObject);
            }
            else {
                bool wasSelected = selection.Contains(itemObject);
                selection.Clear();
                if(wasSelected && !isMandatory.Value) {
                    SetSelectedItemCore(null);
                    return true;
                }
                selection.Add(itemObject);
            }
            object selectedAfter = selection.FirstOrDefault();
            SetSelectedItemCore(selectedAfter);
            return !Equals(selectedBefore, selectedAfter);
        }
        void SetSelectedItemCore(object selectedAfter) {
            if(lockSelection > 0)
                SetAttribute(nameof(SelectedItem), selectedAfter);
        }
        int lockSelection = 0;
        internal void OnItemSelected(DxHtmlElementEventArgs args) {
            object itemObject = args.Element.GetAttribute(nameof(Item.ItemObject));
            lockSelection++;
            try {
                if(SyncSelection(Selection, itemObject, IsMultiSelect)) {
                    UpdateItemsSelection(Selection);
                    DispatchEvent("selection-changed", args);
                }
            }
            finally { lockSelection--; }
        }
        static bool IsControlPressed {
            get { return System.Windows.Forms.Control.ModifierKeys == System.Windows.Forms.Keys.Control; }
        }
        void UpdateItemsSelection(ICollection<object> selection) {
            foreach(var child in Element.Children) {
                var selectorItem = child.GetAttribute("self") as Item;
                if(selectorItem != null)
                    selectorItem.UpdateStates(selection.Contains(selectorItem.ItemObject));
            }
        }
        void UpdateItemSizes(string itemClassName, Size? itemSize) {
            int index = 0, count = (itemsCollection != null) ? itemsCollection.Count : 0;
            foreach(var child in Element.Children)
                child.ClassName = CalcClassName(itemClassName, itemSize, index++, count);
        }
        void CreateItems(ICollection itemsCollection, string itemClassName, Size? itemSize) {
            int index = 0, count = itemsCollection.Count;
            foreach(object itemObject in itemsCollection) {
                var itemElement = DxHtmlDocument.CreateElement("items-selector-item");
                itemElement.ClassName = CalcClassName(itemClassName, itemSize, index++, count);
                itemElement.SetAttribute(nameof(Item.ItemObject), itemObject);
                itemElement.SetAttribute("items-selector", this);
                AppendChild(itemElement);
            }
        }
        static string CalcClassName(string className, Size? size, int index, int count) {
            if(string.IsNullOrEmpty(className))
                className = "items-selector-item";
            className = className + " " + size.GetValueOrDefault().ToString().ToLowerInvariant();
            if(index == 0 && count > 1)
                return className + " first";
            if(index == count - 1 && count > 1)
                return className + " last";
            return className + " middle";
        }
        public sealed class Item : DxHtmlComponent {
            public object ItemObject {
                get { return GetAttribute(nameof(ItemObject)); }
            }
            ElementInternals _internals;
            public override void ConnectedCallback() {
                base.ConnectedCallback();
                _internals = AttachInternals();
                var selector = GetAttribute("items-selector") as ItemsSelector;
                object itemObject = ItemObject;
                if(selector == null || !selector.OnCreateItemContent(this))
                    SetInnerText(GetText(itemObject));
                AddEventListener("mouseup", OnItemMouseUp);
                SetAttribute("self", this);
                var selection = (selector != null) ? selector.Selection : null;
                if(selection != null && selection.Count > 0)
                    UpdateStates(selection.Contains(itemObject));
            }
            public override void DisconnectedCallback() {
                RemoveEventListener("mouseup", OnItemMouseUp);
                DetachInternals();
                _internals = null;
                base.DisconnectedCallback();
            }
            void OnItemMouseUp(DxHtmlElementEventArgs args) {
                var mouseArgs = args as DxHtmlElementMouseEventArgs;
                if(mouseArgs != null && mouseArgs.IsClick) {
                    var meArgs = mouseArgs.MouseArgs as DXMouseEventArgs;
                    if(meArgs != null && meArgs.Handled)
                        return;
                    OnSelectItem(args);
                }
            }
            void OnSelectItem(DxHtmlElementEventArgs args) {
                var selector = GetAttribute("items-selector") as ItemsSelector;
                if(selector != null)
                    selector.OnItemSelected(args);
                args.CancelBubble = true;
                args.SuppressOwnerEvent = true;
            }
            public void UpdateStates(bool isSelected) {
                if(_internals == null)
                    return;
                if(isSelected)
                    _internals.States.Add("--selected");
                else
                    _internals.States.Delete("--selected");
            }
            public void SetContent(DxHtmlDocumentFragment template) {
                AppendChild(template.CloneNode(true));
            }
            public void SetContent(DxHtmlElement content) {
                AppendChild(content);
            }
            public void SetContent(string content) {
                SetInnerHtml(content);
            }
            static string GetText(object itemObject) {
                if(itemObject == null)
                    return string.Empty;
                return itemObject as string ?? itemObject.ToString();
            }
        }
    }
}
