namespace DevExpress.UITemplates.Collection.Editors {
    using System.Collections.Generic;
    using System.Collections.Specialized;
    using System.ComponentModel;
    using System.Linq;
    using System.Windows.Forms;
    using DevExpress.UITemplates.Collection.Components;
    using DevExpress.UITemplates.Collection.Editors.Base;
    using DevExpress.Utils;
    using DevExpress.Utils.Design;
    using DevExpress.Utils.Drawing;
    using DevExpress.Utils.Html;
    using DevExpress.Utils.Layout;
    using DevExpress.XtraEditors.Internal;

    [Designer("DevExpress.XtraEditors.Design.HtmlItemsControlDesigner, " + AssemblyInfo.SRAssemblyEditorsDesign, Aliases.IDesigner)]
    [SmartTagAction(typeof(HtmlItemsControlActions), "AddItem", "Add Item", SmartTagActionType.RefreshBoundsAfterExecute | SmartTagActionType.RefreshContentAfterExecute)]
    public abstract class ChipGroupBase : HtmlButtonBase, ItemsSelector.IItemContentProvider, IHtmlItemsControl {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static ChipGroupBase() {
            ItemsSelector.Register();
        }
        readonly Chip.Collection itemsCore;
        protected ChipGroupBase() {
            itemsCore = CreateItemsCollection();
            Items.CollectionChanged += OnItemsCollectionChanged;
        }
        protected override void Dispose(bool disposing) {
            if(selectorElement != null)
                selectorElement.RemoveEventListener("selection-changed", OnItemsSelectorSelectionChanged);
            Items.CollectionChanged -= OnItemsCollectionChanged;
            base.Dispose(disposing);
        }
        protected virtual Chip.Collection CreateItemsCollection() {
            return new Chip.Collection();
        }
        protected virtual void OnItemsCollectionChanged(object sender, NotifyCollectionChangedEventArgs e) {
            if(selectorElement != null)
                selectorElement.SetAttribute(nameof(ItemsSelector.Items), Items);
            OnPropertiesChanged();
        }
        DxHtmlElement selectorElement;
        protected override void FindParts(Dictionary<object, DxHtmlElement> parts, DxHtmlRootElement root) {
            base.FindParts(parts, root);
            selectorElement = root.FindElementsByTag("items-selector").FirstOrDefault();
            if(selectorElement != null) {
                selectorElement.SetAttribute(nameof(ItemSize), ItemSize);
                selectorElement.SetAttribute(nameof(ItemsSelector.ItemContentProvider), this);
                selectorElement.SetAttribute(nameof(ItemsSelector.Items), Items);
                SetupItemsSelector(selectorElement);
                selectorElement.AddEventListener("selection-changed", OnItemsSelectorSelectionChanged);
            }
        }
        protected override object GetPartImage(string partName, ObjectState state, DxHtmlElementBase element) {
            if(partName == "Item.Check")
                return Collection.Images.UIElements.Check;
            if(partName == "Item.Icon") {
                var chip = ItemsSelector.GetItem<Chip>(element);
                if(chip != null) return chip.Caption;
            }
            return base.GetPartImage(partName, state, element);
        }
        protected override string GetPartText(string partName, DxHtmlElementBase element) {
            if(partName == "Item.Caption") {
                var chip = ItemsSelector.GetItem<Chip>(element);
                if(chip != null) return chip.Caption;
            }
            return base.GetPartText(partName, element);
        }
        protected abstract void SetupItemsSelector(DxHtmlElement selectorElement);
        protected abstract void OnItemsSelectorSelectionChanged(DxHtmlElementEventArgs args);
        #region Properties
        public enum GroupAutoSizeMode {
            Default,
            Horizontal,
            Vertical,
            None
        }
        GroupAutoSizeMode autoSizeModeCore = GroupAutoSizeMode.Default;
        [DefaultValue(GroupAutoSizeMode.Default), SmartTagProperty("Auto Size Mode", "", SmartTagActionType.RefreshBoundsAfterExecute)]
        [System.ComponentModel.Category("Layout")]
        public GroupAutoSizeMode AutoSizeMode {
            get { return autoSizeModeCore; }
            set {
                if(AutoSizeMode == value) return;
                autoSizeModeCore = value;
                base.AutoSize = GetActualAutoSize(value);
                LayoutChanged();
                RaiseSizeableChanged();
            }
        }
        bool GetActualAutoSize(GroupAutoSizeMode mode) {
            if(mode == GroupAutoSizeMode.Horizontal)
                return true;
            return Parent is TableLayoutPanel || Parent is TablePanel;
        }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
        [Editor(typeof(DevExpress.Utils.Design.DXCollectionEditorBase), typeof(System.Drawing.Design.UITypeEditor)), Localizable(true)]
        [SmartTagProperty("Items", "", 2)]
        [System.ComponentModel.Category("Data")]
        public Chip.Collection Items {
            get { return itemsCore; }
        }
        [Browsable(false), EditorBrowsable(EditorBrowsableState.Never), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public override bool AutoSize {
            get { return base.AutoSize; }
            set { base.AutoSize = value; }
        }
        bool showCheckCore = true;
        [DefaultValue(true)]
        [System.ComponentModel.Category("Appearance")]
        public bool ShowCheck {
            get { return showCheckCore; }
            set {
                if(showCheckCore == value) return;
                showCheckCore = value;
                ResetItemsSelectorItems();
            }
        }
        ItemsSelector.Size itemSizeCore = ItemsSelector.Size.Default;
        [DefaultValue(ItemsSelector.Size.Default)]
        [System.ComponentModel.Category("Layout")]
        public ItemsSelector.Size ItemSize {
            get { return itemSizeCore; }
            set {
                if(itemSizeCore == value) return;
                itemSizeCore = value;
                SetItemsSelectorItemSize(value);
            }
        }
        #endregion Properties
        #region Theme
        bool ItemsSelector.IItemContentProvider.OnCreateItemContent(DxHtmlRootElement root, ItemsSelector.Item item) {
            string itemTemplateId = ShowCheck ? "chip-check-template" : "chip-template";
            DxHtmlDocumentFragment template = root.FindTemplate(itemTemplateId);
            if(template != null)
                item.SetContent(template);
            return (template != null);
        }
        #endregion
        #region API
        protected void ResetItemsSelectorItems() {
            if(selectorElement != null) {
                var selector = selectorElement.GetAttribute("self") as ItemsSelector;
                if(selector != null) 
                    selector.ResetItems();
            }
        }
        protected void UpdateItemsSelectorSelection() {
            if(selectorElement != null) {
                var selector = selectorElement.GetAttribute("self") as ItemsSelector;
                if(selector != null) 
                    selector.UpdateSelection();
            }
        }
        protected Chip GetItemsSelectorSelectedItem() {
            return selectorElement.GetAttribute(nameof(ItemsSelector.SelectedItem)) as Chip;
        }
        protected void SetItemsSelectorSelectedItem(Chip chip) {
            if(selectorElement != null)
                selectorElement.SetAttribute(nameof(ItemsSelector.SelectedItem), chip);
        }
        protected void SetItemsSelectorIsMandatorySelection(bool value) {
            if(selectorElement != null)
                selectorElement.SetAttribute(nameof(ItemsSelector.IsMandatorySelection), value);
        }
        protected void SetItemsSelectorItemSize(ItemsSelector.Size value) {
            if(selectorElement != null)
                selectorElement.SetAttribute(nameof(ItemSize), value);
        }
        #endregion API
        protected override void OnMouseUp(System.Windows.Forms.MouseEventArgs e) {
            var dxArgs = DXMouseEventArgs.GetMouseArgs(e);
            dxArgs.Handled = GetValidationCanceled();
            base.OnMouseUp(dxArgs);
        }
        protected override void AdjustSize() {
            if(AutoSizeMode == GroupAutoSizeMode.Horizontal) 
                Size = PreferredSize;
            if(AutoSizeMode == GroupAutoSizeMode.Vertical) 
                Size = GetPreferredSize(Size);
        }
        protected override bool IsAutoHeight {
            get { return AutoSizeMode == GroupAutoSizeMode.Vertical; }
        }
        protected override bool IsAutoWidth {
            get { return AutoSizeMode == GroupAutoSizeMode.Horizontal; }
        }
        void IHtmlItemsControl.OnInitializeNewComponent() {
            Items.Add(new Chip(1, "Item 1"));
            Items.Add(new Chip(2, "Item 2"));
        }
        void IHtmlItemsControl.AddItem() {
            Items.Add(new Chip(Items.Count + 1, "Item " + (Items.Count + 1)));
        }
    }
}
