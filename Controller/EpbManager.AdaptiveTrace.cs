using Controller.Adaptive;

namespace Controller
{
    public sealed partial class EpbManager
    {
        private readonly AdaptiveDecisionTraceBuffer _adaptiveDecisionTrace =
            new AdaptiveDecisionTraceBuffer();

        private void OnRunnerAdaptiveDecisionObserved(AdaptiveDecisionTraceSample item)
        {
            _runIdByChannel.TryGetValue(item.Channel, out var runId);
            _currentCycleNumberByChannel.TryGetValue(item.Channel, out var cycleNumber);
            item.RunId = runId;
            item.CycleNumber = cycleNumber;
            _adaptiveDecisionTrace.Append(item);
        }
    }
}
