namespace DevExpress.UITemplates.Collection.Editors {
    using System.ComponentModel;
    using System.Drawing;
    using MTEmbTest.Assets.Toolbox; 
    using DevExpress.UITemplates.Collection.Components;
    using DevExpress.UITemplates.Collection.Utilities;
    using DevExpress.Utils;
    using DevExpress.Utils.Html;

    [ToolboxItem(true)]
    [ToolboxBitmap(typeof(ToolboxBitmapRoot), "ToggleButton.bmp")]
    [Description("A toggle button used to select one of two mutually exclusive options/values.")]
    public class ToggleButton : CheckButtonBase {
        #region Properties
        public enum Position { Inside, Outside, None }
        Position textPositionCore = Position.Outside;
        [DefaultValue(Position.Outside), System.ComponentModel.Category("Text")]
        [Utils.Design.SmartTagProperty("Text Position", "", 1, Utils.Design.SmartTagActionType.RefreshBoundsAfterExecute | Utils.Design.SmartTagActionType.RefreshContentAfterExecute)]
        public Position TextPosition {
            get { return textPositionCore; }
            set {
                if(textPositionCore == value) return;
                textPositionCore = value;
                OnPropertiesChanged();
            }
        }
        string checkedTextCore;
        [DefaultValue(null), System.ComponentModel.Category("Text")]
        [Utils.Design.SmartTagProperty("Checked Text", "", 1, Utils.Design.SmartTagActionType.RefreshBoundsAfterExecute | Utils.Design.SmartTagActionType.RefreshContentAfterExecute)]
        public string CheckedText {
            get { return checkedTextCore; }
            set {
                if(checkedTextCore == value) return;
                checkedTextCore = value;
                OnPropertiesChanged();
            }
        }
        string uncheckedTextCore;
        [DefaultValue(null), System.ComponentModel.Category("Text")]
        [Utils.Design.SmartTagProperty("Unchecked Text", "", 1, Utils.Design.SmartTagActionType.RefreshBoundsAfterExecute | Utils.Design.SmartTagActionType.RefreshContentAfterExecute)]
        public string UncheckedText {
            get { return uncheckedTextCore; }
            set {
                if(uncheckedTextCore == value) return;
                uncheckedTextCore = value;
                OnPropertiesChanged();
            }
        }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden), Browsable(false)]
        public override string Text {
            get {
                if(TextPosition != Position.None) {
                    if(Checked)
                        return checkedTextCore ?? "ON";
                    else
                        return uncheckedTextCore ?? "OFF";
                }
                return string.Empty;
            }
            set { }
        }
        #endregion
        #region Theme
        protected override string GetPartText(string partName, DxHtmlElementBase element) {
            if(partName == "InsideText")
                return (TextPosition == Position.Inside) ? Text : null;
            if(partName == "OutsideText")
                return (TextPosition == Position.Outside) ? Text : null;
            return base.GetPartText(partName, element);
        }
        protected override void OnIconImageOptionsChanged(object sender, ImageOptionsChangedEventArgs args) {
            base.OnIconImageOptionsChanged(sender, args);
            EnsureButtonContentTemplate();
        }
        protected override string GetButtonContentTemplateId() {
            return IconImageOptions.HasImage ? "toggle-button-image-template" : "toggle-button-template";
        }
        protected override string LoadDefaultTemplate() {
            return ToggleButtonHtmlCssAsset.Default.Html;
        }
        protected override string LoadDefaultStyles() {
            return ToggleButtonHtmlCssAsset.Default.Css;
        }
        sealed class ToggleButtonHtmlCssAsset : HtmlCssAsset {
            static ToggleButtonHtmlCssAsset() {
                CheckItemBase.Register();
            }
            public static readonly HtmlCssAsset Default = new ToggleButtonHtmlCssAsset();
        }
        #endregion Theme
        protected override ICustomDxHtmlPreview CreateHtmlEditorPreview() {
            var previewControl = new ToggleButton();
            previewControl.TextPosition = TextPosition;
            previewControl.CheckedText = string.IsNullOrEmpty(CheckedText) ? "{CheckedText}" : CheckedText;
            previewControl.UncheckedText = string.IsNullOrEmpty(UncheckedText) ? "{CheckedText}" : UncheckedText;
            previewControl.IconImageOptions.Assign(IconImageOptions);
            return previewControl;
        }
    }
}
