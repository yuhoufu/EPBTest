using System;

namespace IO.NI
{
    public sealed class DaqRuntimeConfigException : ArgumentException
    {
        public DaqRuntimeConfigException(string message, string parameterName = null)
            : base("DaqRuntimeConfigInvalid: " + (message ?? string.Empty), parameterName)
        {
        }

        public string Code => "DaqRuntimeConfigInvalid";
    }
}
