using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using DataOperation;
using IO.NI;

// Diagnostic reproduction only. Uses real production writer/coordinator,
// a fresh local data directory and synthetic input; never opens hardware.
internal static class GapRepro
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static DaqDiskBatch Batch(long sequence)
    {
        var times = ArrayPool<DateTime>.Shared.Rent(1);
        var current = ArrayPool<double>.Shared.Rent(1);
        var pressure = ArrayPool<double>.Shared.Rent(1);
        times[0] = DateTime.UtcNow;
        current[0] = 0.02;
        pressure[0] = 0.1;
        var channel = (DaqDiskChannelBatch)Activator.CreateInstance(typeof(DaqDiskChannelBatch), Flags, null,
            new object[] { 4, current }, null);
        return (DaqDiskBatch)Activator.CreateInstance(typeof(DaqDiskBatch), Flags, null,
            new object[] { "Dev1", 5L, sequence, 1, times, new[] { channel }, pressure, null, Stopwatch.GetTimestamp() }, null);
    }
    private static long Value(object snapshot, string name)
    {
        return Convert.ToInt64(snapshot.GetType().GetProperty(name, Flags).GetValue(snapshot));
    }
    private static int Main(string[] args)
    {
        string root = Path.GetFullPath(args[0]);
        if (Directory.Exists(root)) throw new InvalidOperationException("Fresh output directory required.");
        Directory.CreateDirectory(root);
        using (var writer = new EpbDiskWriter(new DataRetentionPolicy
        {
            DataStorePath = Path.Combine(root, "data"),
            IndexAndExportPath = Path.Combine(root, "index"),
            IndexDbFile = "index.db", FileSizeMb = 1, RetainAllData = true
        }))
        {
            writer.BeginCycleAtDaqBoundary(4, 1, DateTime.UtcNow, "Dev1", 5, 121920);
            writer.WriteDeviceBatch("Dev1", 5, 121921, new[] { DateTime.UtcNow },
                new[] { new EpbChannelDiskBatch(4, new[] { 0.02 }, new[] { 0.1 }) }, 1, 1);
            int rejected = 0;
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    writer.WriteDeviceBatch("Dev1", 5, 121928, new[] { DateTime.UtcNow },
                        new[] { new EpbChannelDiskBatch(4, new[] { 0.02 }, new[] { 0.1 }) }, 1, 1);
                }
                catch (InvalidDataException ex) { rejected++; Console.WriteLine("Writer attempt " + (i + 1) + ": " + ex.Message); }
            }
            if (rejected != 3) throw new Exception("Expected deterministic sequence-gap rejection.");
            var type = Assembly.Load("Controller").GetType("Controller.DaqPersistenceCoordinator", true);
            object coordinator = Activator.CreateInstance(type, Flags, null,
                new object[] { new Func<IEpbCycleRecorder>(() => new DiskWriterRecorderAdapter(writer)), null, 16, 8, 2, 1000.0, 100.0, 1000, 2, null, null }, null);
            try
            {
                type.GetMethod("Enqueue", Flags).Invoke(coordinator, new object[] { Batch(121928) });
                type.GetMethod("Enqueue", Flags).Invoke(coordinator, new object[] { Batch(121929) });
                Thread.Sleep(1600);
                object first = type.GetMethod("GetSnapshot", Flags).Invoke(coordinator, new object[] { "Dev1" });
                Thread.Sleep(1000);
                object second = type.GetMethod("GetSnapshot", Flags).Invoke(coordinator, new object[] { "Dev1" });
                foreach (var s in new[] { first, second })
                    Console.WriteLine("Snapshot State=" + s.GetType().GetProperty("State", Flags).GetValue(s) +
                        " Persisted=" + Value(s, "Sequence") + " InFlight=" + Value(s, "InFlightSequence") +
                        " Head=" + Value(s, "PendingHeadSequence"));
                if (Value(first, "InFlightSequence") != 121928 || Value(second, "InFlightSequence") != 121928 ||
                    Value(second, "PendingHeadSequence") != 121929 || Value(second, "Sequence") != 0)
                    throw new Exception("Expected production coordinator to remain blocked on the same poison batch.");
                Console.WriteLine("CONFIRMED_OLD_DEFECT: deterministic gap is retried past recovery deadline; following batch never advances.");
                Console.WriteLine("This confirms a defect, not a fix or a hardware validation.");
            }
            finally { ((IDisposable)coordinator).Dispose(); }
        }
        return 0;
    }
}
