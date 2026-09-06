namespace DevExpress.UITemplates.Collection.Editors.Base {
    using System.ComponentModel;
    using DevExpress.XtraEditors.Internal;

    [EditorBrowsable(EditorBrowsableState.Never)]
    public class HtmlItemsControlActions : DevExpress.Utils.ControlActions {
        public void AddItem(IComponent component) {
            var htmlItemsControl = component as IHtmlItemsControl;
            if(htmlItemsControl != null) htmlItemsControl.AddItem();
        }
    }
}
