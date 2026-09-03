using System;

namespace RecoveryKernelTests
{
    internal static class Program
    {
        private static int Main()
        {
            try
            {
                var passed = RecoveryKernelStateMachineTests.RunAll();
                Console.WriteLine($"PASS {passed}/{passed}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
        }
    }
}
