using System;

namespace MTTFTest.EngineHostIntegrationTests
{
    internal static class Program
    {
        private static int Main()
        {
            try
            {
                var passed = EngineHostTests.RunAll();
                Console.WriteLine("PASS " + passed + "/" + passed);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL " + ex);
                return 1;
            }
        }
    }
}
