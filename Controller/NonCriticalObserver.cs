using System;

namespace Controller
{
    /// <summary>
    /// 隔离 UI、诊断、日志桥接等非关键观察者。观察者可以获知控制结果，
    /// 但任何一个观察者的异常都不得反向改变电机、计时器、落盘或恢复状态。
    /// </summary>
    internal static class NonCriticalObserver
    {
        public static int Invoke(Action observers, Action<Exception> onError = null)
        {
            if (observers == null) return 0;
            var succeeded = 0;
            foreach (var item in observers.GetInvocationList())
            {
                try
                {
                    ((Action)item)();
                    succeeded++;
                }
                catch (Exception ex)
                {
                    Report(onError, ex);
                }
            }
            return succeeded;
        }

        public static int Invoke<T>(
            Action<T> observers,
            T value,
            Action<Exception> onError = null)
        {
            if (observers == null) return 0;
            var succeeded = 0;
            foreach (var item in observers.GetInvocationList())
            {
                try
                {
                    ((Action<T>)item)(value);
                    succeeded++;
                }
                catch (Exception ex)
                {
                    Report(onError, ex);
                }
            }
            return succeeded;
        }

        public static int Invoke<T1, T2>(
            Action<T1, T2> observers,
            T1 value1,
            T2 value2,
            Action<Exception> onError = null)
        {
            if (observers == null) return 0;
            var succeeded = 0;
            foreach (var item in observers.GetInvocationList())
            {
                try
                {
                    ((Action<T1, T2>)item)(value1, value2);
                    succeeded++;
                }
                catch (Exception ex)
                {
                    Report(onError, ex);
                }
            }
            return succeeded;
        }

        public static int Invoke<T1, T2, T3>(
            Action<T1, T2, T3> observers,
            T1 value1,
            T2 value2,
            T3 value3,
            Action<Exception> onError = null)
        {
            if (observers == null) return 0;
            var succeeded = 0;
            foreach (var item in observers.GetInvocationList())
            {
                try
                {
                    ((Action<T1, T2, T3>)item)(value1, value2, value3);
                    succeeded++;
                }
                catch (Exception ex)
                {
                    Report(onError, ex);
                }
            }
            return succeeded;
        }

        private static void Report(Action<Exception> onError, Exception exception)
        {
            try { onError?.Invoke(exception); }
            catch { }
        }
    }
}
