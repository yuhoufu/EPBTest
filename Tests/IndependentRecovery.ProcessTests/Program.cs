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
                if (args.Length == 4 && args[0] == "--state-consume")
                    return IndependentProjectStateTests.ConsumeChild(args[1], args[2], long.Parse(args[3]));
                var passed = IndependentBoundedWorkerTests.RunAll();
                Console.WriteLine($"PASS independent workers {passed}/{passed}");
                IndependentProjectStateTests.RunAll();
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }
}
