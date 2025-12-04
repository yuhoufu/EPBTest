namespace DevExpress.UITemplates.Collection.Editors {
    using System;
    using System.ComponentModel;
    using System.Drawing;
    using MTEmbTest.Assets.Toolbox; 
    using DevExpress.UITemplates.Collection.Components;
    using DevExpress.UITemplates.Collection.Utilities;
    using DevExpress.Utils.Html;

    [ToolboxItem(true)]
    [ToolboxBitmap(typeof(ToolboxBitmapRoot), "ChipGroup.bmp")]
    [Description("Similar to a radio group, a Chip Group displays chips across multiple lines. It supports single value and mandatory selection.")]
    public class ChipGroup : ChipGroupBase {
        protected override void SetupItemsSelector(DxHtmlElement selectorElement) {
            selectorElement.SetAttribute(nameof(ItemsSelector.IsMultiSelect), false);
            selectorElement.SetAttribute(nameof(ItemsSelector.IsMandatorySelection), IsMandatorySelection);
            if(selectedItem != null)
                selectorElement.SetAttribute(nameof(ItemsSelector.SelectedItem), selectedItem);
        }
        protected override void OnItemsSelectorSelectionChanged(DxHtmlElementEventArgs args) {
            selectedItem = GetItemsSelectorSelectedItem();
            RaiseSelectedItemChanged();
            RaiseSelectedValueChanged();
        }
        #region Properties
        Chip selectedItem;
        [Browsable(false), DefaultValue(null), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Chip SelectedItem {
            get { return selectedItem; }
            set {
                if(selectedItem == value) return;
                selectedItem = value;
                OnSelectedItemChanged(value);
            }
        }
        protected virtual void OnSelectedItemChanged(Chip chip) {
            SetItemsSelectorSelectedItem(chip);
            RaiseSelectedItemChanged();
            RaiseSelectedValueChanged();
        }
        [Browsable(false), DefaultValue(null), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public object SelectedValue {
            get { return selectedItem?.Value; }
            set { SelectedItem = (value as Chip) ?? Items.GetItemByValue(value); }
        }
        bool mandatoryCore;
        [DefaultValue(false), System.ComponentModel.Category(XtraEditors.CategoryName.Behavior)]
        public bool IsMandatorySelection {
            get { return mandatoryCore; }
            set {
                if(mandatoryCore == value) return;
                mandatoryCore = value;
                SetItemsSelectorIsMandatorySelection(value);
            }
        }
        #endregion Properties
        #region Events
        readonly static object selectedItemChanged = new object();
        readonly static object selectedValueChanged = new object();
        //
        public event EventHandler SelectedItemChanged {
            add { Events.AddHandler(selectedItemChanged, value); }
            remove { Events.RemoveHandler(selectedItemChanged, value); }
        }
        protected void RaiseSelectedItemChanged() {
            var handler = Events[selectedItemChanged] as EventHandler;
            handler?.Invoke(this, EventArgs.Empty);
        }
        public event EventHandler SelectedValueChanged {
            add { Events.AddHandler(selectedValueChanged, value); }
            remove { Events.RemoveHandler(selectedValueChanged, value); }
        }
        protected void RaiseSelectedValueChanged() {
            var handler = Events[selectedValueChanged] as EventHandler;
            handler?.Invoke(this, EventArgs.Empty);
        }
        #endregion Events
        #region Theme
        protected override string LoadDefaultTemplate() {
            return ChipGroupHtmlCssAsset.Default.Html;
        }
        protected override string LoadDefaultStyles() {
            return ChipGroupHtmlCssAsset.Default.Css;
        }
        sealed class ChipGroupHtmlCssAsset : HtmlCssAsset {
            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
            static ChipGroupHtmlCssAsset() {
                ItemsSelector.Register();
            }
            public static readonly HtmlCssAsset Default = new ChipGroupHtmlCssAsset();
        }
        #endregion Theme
        protected override ICustomDxHtmlPreview CreateHtmlEditorPreview() {
            var previewControl = new ChipGroup();
            previewControl.Items.AddRange(new Chip[] {
                new Chip(1,"One"),
                new Chip(2,"Two"),
                new Chip(3,"Three"),
            });
            previewControl.SelectedItem = previewControl.Items[0];
            return previewControl;
        }
    }
}
