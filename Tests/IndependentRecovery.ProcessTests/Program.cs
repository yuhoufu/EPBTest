using System;

namespace AdaptiveControlTests
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 3 && args[0] == "--independent-worker-child")
                    return IndependentBoundedWorkerTests.Child(args[1], args[2]);
                var passed = IndependentBoundedWorkerTests.RunAll();
                Console.WriteLine($"PASS independent workers {passed}/{passed}");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }
}
