using DevExpress.XtraEditors;
using System;

namespace MTEmbTest.Models
{
    public class PressureSettingControl
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public CheckEdit EnableCheckEdit { get; set; }
        public TextEdit PressureTextEdit { get; set; }
        public string Unit { get; set; } = "bar";

        public int PressureValue
        {
            get
            {
                if (int.TryParse(PressureTextEdit.Text, out int value))
                    return value;
                return 0;
            }
            set
            {
                PressureTextEdit.Text = value.ToString();
            }
        }

        public bool IsEnabled
        {
            get => EnableCheckEdit?.Checked ?? false;
            set
            {
                if (EnableCheckEdit != null)
                    EnableCheckEdit.Checked = value;
            }
        }

        public bool IsValid => IsEnabled && PressureValue > 0;

        public PressureSettingControl(int id, string name, CheckEdit enableCheckEdit, TextEdit pressureTextEdit, string unit = "bar")
        {
            Id = id;
            Name = name ?? throw new ArgumentNullException(nameof(name));
            EnableCheckEdit = enableCheckEdit ?? throw new ArgumentNullException(nameof(enableCheckEdit));
            PressureTextEdit = pressureTextEdit ?? throw new ArgumentNullException(nameof(pressureTextEdit));
            Unit = unit;

            Initialize();
        }

        private void Initialize()
        {
            IsEnabled = false;
            PressureValue = 0;

            EnableCheckEdit.CheckedChanged += (s, e) =>
            {
                PressureTextEdit.Enabled = IsEnabled;
            };

            PressureTextEdit.Enabled = IsEnabled;
        }

        public void SetPressure(int pressure, bool enable = true)
        {
            IsEnabled = enable;
            PressureValue = pressure;
        }

        public void Reset()
        {
            IsEnabled = false;
            PressureValue = 0;
        }

        public bool ValidatePressure(int min = 0, int max = 100)
        {
            return PressureValue >= min && PressureValue <= max;
        }

        public override string ToString()
        {
            return $"{Id}.{Name}: {(IsEnabled ? $"{PressureValue} {Unit}" : "禁用")}";
        }
    }
}