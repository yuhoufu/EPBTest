using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Config
{
    // —— UI POCO —— //
    public sealed class UiControlState
    {
        public string Name { get; set; }
        public bool Checked { get; set; }
        public bool Enabled { get; set; }
        public bool DefaultChecked { get; set; }
    }

    public sealed class UiFormState
    {
        public string FormName { get; set; }

        public Dictionary<string, UiControlState> Controls { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public UiControlState GetOrAdd(string name, bool? defaultChecked = null)
        {
            if (!Controls.TryGetValue(name, out var c))
            {
                c = new UiControlState
                {
                    Name = name, Checked = defaultChecked ?? false, Enabled = true,
                    DefaultChecked = defaultChecked ?? false
                };
                Controls[name] = c;
            }

            return c;
        }
    }

    public sealed class UiConfig
    {
        public Dictionary<string, UiFormState> Forms { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public UiFormState GetOrAddForm(string formName)
        {
            if (!Forms.TryGetValue(formName, out var f))
            {
                f = new UiFormState { FormName = formName };
                Forms[formName] = f;
            }

            return f;
        }
    }
}