using System;

namespace MTTFTest.SessionAgent
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try { return SessionAgentHost.Run(); }
            catch (Exception ex)
            {
                SessionAgentHost.WriteAudit(
                    "SessionAgentFatal",
                    ex.GetBaseException().ToString());
                return 2;
            }
        }
    }
}
