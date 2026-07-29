using System.Runtime.CompilerServices;

namespace Controller
{
    public sealed partial class EpbCycleRunner
    {
        private bool CommandForward([CallerMemberName] string stage = null)
        {
            return _manager != null
                ? _manager.CommandEpbForward(_channel, stage)
                : _do.SetEpbForward(_channel);
        }

        private bool CommandReverse([CallerMemberName] string stage = null)
        {
            return _manager != null
                ? _manager.CommandEpbReverse(_channel, stage)
                : _do.SetEpbReverse(_channel);
        }

        private bool CommandOff([CallerMemberName] string stage = null)
        {
            return _manager != null
                ? _manager.CommandEpbOff(_channel, stage)
                : _do.SetEpbOff(_channel);
        }

        private bool CommandOffHighPriority([CallerMemberName] string stage = null)
        {
            return _manager != null
                ? _manager.CommandEpbOffHighPriority(_channel, stage)
                : _do.SetEpbOffHighPriority(_channel);
        }
    }
}
