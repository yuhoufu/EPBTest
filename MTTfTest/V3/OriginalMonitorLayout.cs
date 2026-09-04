using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace MTEmbTest
{
    // WinForms may scale the designer and clamp the top-level Form to the desktop
    // tracking limit independently. Derive the original coordinate scale from the
    // root table's fixed grid units, never from the possibly clipped Form size.
    internal sealed class OriginalMonitorLayout : IDisposable
    {
        private sealed class Metrics
        {
            internal Control Control;
            internal Rectangle Bounds;
            internal Padding Margin;
            internal Padding Padding;
            internal Size Minimum;
            internal Font Font;
            internal float[] Columns;
            internal float[] Rows;
            internal string ToggleStyles;
        }

        private readonly Form _form;
        private readonly Size _designSize;
        private readonly Action _applied;
        private readonly Metrics[] _metrics;
        private readonly List<Font> _fonts = new List<Font>();
        private bool _applying;
        private Size _lastSize;

        internal OriginalMonitorLayout(Form form, Action applied = null)
        {
            _form = form;
            _applied = applied;
            _designSize = CaptureUnclippedDesignSize(form);
            form.AutoScaleMode = AutoScaleMode.None;
            _metrics = Descendants(form).Select(control => new Metrics
            {
                Control = control, Bounds = control.Bounds, Margin = control.Margin,
                Padding = control.Padding, Minimum = control.MinimumSize, Font = control.Font,
                ToggleStyles = (control as DevExpress.UITemplates.Collection.Editors.ToggleButton)?.HtmlTemplate.Styles,
                Columns = (control as TableLayoutPanel)?.ColumnStyles.Cast<ColumnStyle>()
                    .Select(style => style.SizeType == SizeType.Absolute ? style.Width : -1).ToArray(),
                Rows = (control as TableLayoutPanel)?.RowStyles.Cast<RowStyle>()
                    .Select(style => style.SizeType == SizeType.Absolute ? style.Height : -1).ToArray()
            }).ToArray();
            form.SizeChanged += OnSizeChanged;
            form.Shown += OnSizeChanged;
        }

        private static Size CaptureUnclippedDesignSize(Form form)
        {
            var root = form.Controls.Find("uiTableLayoutPanel1", true).OfType<TableLayoutPanel>().Single();
            // These are the existing Designer's 1084-pixel settings column and
            // 18-pixel vertical gutter on its 2808 x 1682 authored surface.
            // The table styles retain WinForms' actual scale even when the form
            // ClientSize was constrained before this composition root ran.
            var column = root.ColumnStyles[1];
            var row = root.RowStyles[2];
            if (column.SizeType != SizeType.Absolute || row.SizeType != SizeType.Absolute || column.Width <= 0 || row.Height <= 0)
                throw new InvalidOperationException("OriginalMonitorDesignGridInvalid");
            return new Size((int)Math.Round(2808 * column.Width / 1084F), (int)Math.Round(1682 * row.Height / 18F));
        }

        private static IEnumerable<Control> Descendants(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                yield return child;
                foreach (var nested in Descendants(child)) yield return nested;
            }
        }

        private void OnSizeChanged(object sender, EventArgs args)
        {
            var size = _form.ClientSize;
            if (_applying || size == _lastSize || size.Width < 300 || size.Height < 200) return;
            _applying = true;
            var oldFonts = _fonts.ToArray();
            _fonts.Clear();
            var x = size.Width / (float)_designSize.Width;
            var y = size.Height / (float)_designSize.Height;
            _form.SuspendLayout();
            foreach (var entry in _metrics) entry.Control.SuspendLayout();
            try
            {
                foreach (var entry in _metrics)
                {
                    var control = entry.Control;
                    control.Margin = Scale(entry.Margin, x, y);
                    control.Padding = Scale(entry.Padding, x, y);
                    control.MinimumSize = new Size((int)(entry.Minimum.Width * x), (int)(entry.Minimum.Height * y));
                    var font = new Font(entry.Font.FontFamily, Math.Max(5F, entry.Font.Size * Math.Min(x, y)),
                        entry.Font.Style, entry.Font.Unit);
                    _fonts.Add(font);
                    control.Font = font;
                    // SunnyUI keeps its group title inset in unscaled pixels during
                    // WinForms font autoscale. Fit that inset to the actual title font.
                    if (control is Sunny.UI.UIGroupBox)
                        control.Padding = new Padding(control.Padding.Left,
                            Math.Min(control.Padding.Top, font.Height + 4), control.Padding.Right, control.Padding.Bottom);
                    if (control is DevExpress.XtraEditors.BaseEdit editor)
                    {
                        editor.Properties.Appearance.Font = font;
                        editor.Properties.Appearance.Options.UseFont = true;
                        editor.Properties.AutoHeight = false;
                    }
                    if (control is DevExpress.XtraEditors.CheckEdit check)
                    {
                        var glyph = Math.Max(8, (int)(18 * Math.Min(x, y)));
                        check.Properties.CheckBoxOptions.SvgImageSize = new Size(glyph, glyph);
                    }
                    if (control is DevExpress.XtraEditors.ComboBoxEdit combo)
                        foreach (DevExpress.XtraEditors.Controls.EditorButton button in combo.Properties.Buttons)
                            button.Width = Math.Max(12, (int)(30 * x));
                    if (entry.ToggleStyles != null && control is DevExpress.UITemplates.Collection.Editors.ToggleButton toggle)
                        toggle.HtmlTemplate.Styles = Regex.Replace(entry.ToggleStyles, @"(\d+(?:\.\d+)?)px", match =>
                            (float.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * Math.Min(x, y))
                            .ToString("0.##", CultureInfo.InvariantCulture) + "px") +
                            "\n.inside-caption { font-size: " + (12 * Math.Min(x, y)).ToString("0.##", CultureInfo.InvariantCulture) + "px; }";
                    if (control.Dock == DockStyle.None)
                        control.Bounds = new Rectangle((int)(entry.Bounds.X * x), (int)(entry.Bounds.Y * y),
                            Math.Max(1, (int)(entry.Bounds.Width * x)), Math.Max(1, (int)(entry.Bounds.Height * y)));
                    if (!(control is TableLayoutPanel table)) continue;
                    for (var index = 0; index < entry.Columns.Length; index++)
                        if (entry.Columns[index] >= 0) table.ColumnStyles[index].Width = entry.Columns[index] * x;
                    for (var index = 0; index < entry.Rows.Length; index++)
                        if (entry.Rows[index] >= 0) table.RowStyles[index].Height = entry.Rows[index] * y;
                }
                _lastSize = size;
            }
            finally
            {
                foreach (var entry in _metrics.Reverse()) entry.Control.ResumeLayout(true);
                _form.ResumeLayout(true);
                foreach (var font in oldFonts) font.Dispose();
                _applying = false;
            }
            _applied?.Invoke();
        }

        private static Padding Scale(Padding value, float x, float y) => new Padding(
            (int)(value.Left * x), (int)(value.Top * y), (int)(value.Right * x), (int)(value.Bottom * y));

        public void Dispose()
        {
            _form.SizeChanged -= OnSizeChanged;
            _form.Shown -= OnSizeChanged;
            foreach (var font in _fonts) font.Dispose();
            _fonts.Clear();
        }
    }
}
