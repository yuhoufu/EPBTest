using System.IO;

namespace Controller.Alarm
{
    internal static class AlarmSnapshotFileEvidence
    {
        public static bool HasCsvAndBin(string alarmDirectory, int channel, int cycleNumber)
        {
            if (string.IsNullOrWhiteSpace(alarmDirectory))
                return false;

            var stem = $"EPB{channel}_Cycle_{cycleNumber:D6}";
            return File.Exists(Path.Combine(alarmDirectory, stem + ".csv")) &&
                   File.Exists(Path.Combine(alarmDirectory, stem + ".bin"));
        }
    }
}
