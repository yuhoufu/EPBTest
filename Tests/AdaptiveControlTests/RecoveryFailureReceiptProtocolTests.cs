using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    /// <summary>Production FileStore boundary fault seam.  It delegates all normal
    /// behavior to the production Windows implementation and only injects
    /// faults at Create/Replace/read-back boundaries; no CAS logic is copied.
    /// </summary>
    internal sealed class FaultingAuthorityIo : IDurableAuthorityFileIo
    {
        private readonly WindowsDurableAuthorityFileIo _inner = new WindowsDurableAuthorityFileIo();

        internal int FailCreateCount { get; set; }
        internal int FailReplaceCount { get; set; }
        internal int FailReadCount { get; set; }
        internal int FailReadAfterReplaceCount { get; set; }

        public bool Exists(string path) => _inner.Exists(path);

        public byte[] ReadAllBytes(string path)
        {
            if (FailReadCount > 0)
            {
                FailReadCount--;
                throw new IOException("InjectedReadFailure");
            }
            return _inner.ReadAllBytes(path);
        }

        public void CreateAndFlush(string path, byte[] bytes)
        {
            if (FailCreateCount > 0)
            {
                FailCreateCount--;
                throw new IOException("InjectedCreateFailure");
            }
            _inner.CreateAndFlush(path, bytes);
        }

        public void Replace(string temporaryPath, string targetPath)
        {
            if (FailReplaceCount > 0)
            {
                FailReplaceCount--;
                throw new IOException("InjectedReplaceFailure");
            }
            _inner.Replace(temporaryPath, targetPath);
            if (FailReadAfterReplaceCount > 0)
            {
                FailReadCount += FailReadAfterReplaceCount;
                FailReadAfterReplaceCount = 0;
            }
        }

        public void Delete(string path) => _inner.Delete(path);
    }

    /// <summary>
    /// Strict schema4 authority tests.  The suite intentionally uses the
    /// production authority/store/factory APIs; it does not instantiate the
    /// legacy DurableRelaunchCoordinator and it never starts a process.
    /// </summary>
    internal static class RecoveryFailureReceiptProtocolTests
    {
        internal static int RunAll()
        {
            var passed = 0;
            Run("bootstrap真实创建及不可伪造capability", BootstrapAndCapability, ref passed);
            Run("missing/corrupt/unreadable默认Blocked-Unproven", MissingCorruptUnreadable, ref passed);
            Run("operation入口clone-normalize-freeze", OperationFreeze, ref passed);
            Run("canonical逐身份字段稳定且排除自由文本", CanonicalIdentityFields, ref passed);
            Run("同operation64并发单次提交", SameOperation64, ref passed);
            Run("同文件独立authority64并发CAS重评估", IndependentAuthority64, ref passed);
            Run("两authority barrier race loser精确ExistingBlocked", AuthorityBarrierExistingBlockedRace, ref passed);
            Run("同correlation不同canonical永久IdentityConflict", CorrelationConflict, ref passed);
            Run("同correlation稳定tuple变更不重放", TupleConflict, ref passed);
            Run("不同operation按sequence和budget推进", DifferentOperationBudget, ref passed);
            Run("重启重放不reset", ReplayAfterRestart, ref passed);
            Run("approved重启不暴露launch", ApprovedRestartNoLaunch, ref passed);
            Run("started alive不启动第二次", StartedAliveNoSecondLaunch, ref passed);
            Run("commit失败marker三路语义", CommitFailureTiers, ref passed);
            Run("全失败bytes不变且Unproven", AllFailureBytesUnchanged, ref passed);
            Run("schema迁移矩阵", MigrationMatrix, ref passed);
            Run("schema0/1/未知状态与legacy不变量fail-closed", MigrationInvalidMatrix, ref passed);
            Run("TOCTOU变更只返回blocked", ToctouMutation, ref passed);
            Run("single File.Replace并读回", SingleReplaceAndReadBack, ref passed);
            Run("CAS压力1000次无回退", CasStress1000, ref passed);
            Run("不同session serializer隔离", DifferentSessionsIsolation, ref passed);
            Run("大小写目录别名共享authority mutex", CaseInsensitivePathAlias, ref passed);
            Run("协议exact v6/外层身份/nonce隔离", ReceiptValidator, ref passed);
            Run("原始JSON白名单与字段类型严格校验", RawWireWhitelist, ref passed);
            Run("失败请求exact-v4原始hash与重放身份", RecoveryFailureRequestWire, ref passed);
            Run("协议拒绝v0/v2/v3", ExactProtocolVersions, ref passed);
            Run("wire序列化不反射permit nonce", WireScrubsNonce, ref passed);
            Run("canonical文化区域和prose重放", CultureAndReplay, ref passed);
            Run("生产FileStore原子IO故障边界与typed结果", ProductionFileIoFailureTiers, ref passed);
            Run("mutex Busy可重试且不污染进程熔断", MutexBusyRetry, ref passed);
            Run("Load成功后Commit Busy不进入marker且可重试", CommitBusyAfterSuccessfulLoad, ref passed);
            Run("active状态current与LastFailure逐字段绑定", ActiveStateEvidenceMutation, ref passed);
            Run("生产proof绑定且budget/circuit跨重启粘性", ProductionProofBudgetSticky, ref passed);
            Run("生产journal writer与authority并行互不覆盖", ProductionJournalAuthorityParallel, ref passed);
            Run("生产FileStore schema2/3/old-v4迁移矩阵", ProductionMigrationMatrix, ref passed);
            Run("生产FileStore Reconcile状态与探测竞态", ProductionReconcileMatrix, ref passed);
            Run("Reconcile Approved/LaunchIntent reload Busy可重试", ProductionReconcileReloadBusy, ref passed);
            Run("Reconcile Started/Attached probe后reload Busy可重试", ProductionReconcilePostProbeBusy, ref passed);
            Run("Reconcile load成功后commit Busy可重试", ProductionReconcileCommitBusy, ref passed);
            Run("生产FileStore迁移非pristine为耐久Blocked", ProductionBlockedMigration, ref passed);
            return passed;
        }

        /// <summary>
        /// Real-file pressure gate.  It is intentionally a separate entry so
        /// the normal deterministic suite remains fast; the parent process
        /// invokes this route when validating cross-process mutex/read-back
        /// behavior.
        /// </summary>
        internal static int RunRealPressure()
        {
            var sw = Stopwatch.StartNew();
            RealFileAuthority64SameOperation();
            RealFileAuthority64DifferentOperations();
            var elapsed64 = sw.ElapsedMilliseconds;
            var count = RealFileAuthority1000();
            Console.WriteLine("PRESSURE real-file 64-process elapsedMs=" + elapsed64 + " 1000-ops elapsedMs=" + count);
            return 3;
        }

        internal static int RunAuthorityChild(string[] args)
        {
            // Child mode is test-only.  No production project references or
            // process-launch paths are changed by this route.
            if (args == null || args.Length < 5) return 2;
            try
            {
                var dir = Encoding.UTF8.GetString(Convert.FromBase64String(args[1]));
                var session = args[2];
                var opId = args[3];
                var corr = args[4];
                var open = DurableRelaunchAuthorityFactory.TryOpenExisting(dir, session);
                if (!open.Succeeded) return 3;
                var op = Operation(session, corr);
                op.OperationId = opId;
                // The launch budget is frozen by the bootstrap proof.  A
                // child must consume that immutable value rather than
                // attempting to raise it in the operation payload.
                op.MaximumProcessRelaunches = open.Authority.Snapshot.MaximumProcessRelaunches;
                var result = open.Authority.RegisterFailureAndDecide(op);
                var receipt = result.Receipt;
                Console.WriteLine((result.Durable ? "D" : "U") + "|" + (result.Record?.AuthorityRevision ?? -1) + "|" +
                    (result.Record?.ConsecutiveFailures ?? -1) + "|" +
                    (receipt == null ? "null" : string.Join("|", new[]
                    {
                        receipt.RequestCorrelationId, receipt.RequestPayloadSha256, receipt.FailureCode,
                        receipt.FailureFingerprint, receipt.Disposition, receipt.Durable.ToString(),
                        receipt.PermitClosed.ToString(), receipt.FailureRegistered.ToString(), receipt.CircuitOpen.ToString(),
                        receipt.ConsecutiveCount.ToString(CultureInfo.InvariantCulture), receipt.RelaunchPermitGeneration.ToString(CultureInfo.InvariantCulture),
                        receipt.DecisionSequence.ToString(CultureInfo.InvariantCulture), receipt.DecisionUtcTicks.ToString(CultureInfo.InvariantCulture), receipt.DetailCode
                    })));
                return result.Durable && result.Receipt != null ? 0 : 4;
            }
            catch { return 5; }
        }

        private static void RealFileAuthority64SameOperation()
        {
            var dir = TempDirectory();
            try
            {
                var session = Guid.NewGuid().ToString("N");
                var created = DurableRelaunchAuthorityFactory.TryCreatePristine(dir, session, 1, 7, 1000);
                Assert(created.Succeeded, "real 64 same-op bootstrap failed: " + created.Reason);
                var opId = Guid.NewGuid().ToString("N");
                var corr = Guid.NewGuid().ToString("N");
                var exe = Process.GetCurrentProcess().MainModule.FileName;
                var processes = new List<Process>();
                for (var i = 0; i < 64; i++)
                {
                    processes.Add(StartAuthorityChild(exe, dir, session, opId, corr));
                }
                var outputs = new List<string>();
                foreach (var process in processes)
                {
                    Assert(process.WaitForExit(60000), "real same-op child timeout");
                    outputs.Add(process.StandardOutput.ReadToEnd().Trim());
                    Assert(process.ExitCode == 0, "real same-op child exit=" + process.ExitCode + " err=" + process.StandardError.ReadToEnd());
                    process.Dispose();
                }
                Assert(outputs.All(x =>
                {
                    var fields = x.Split('|');
                    long revision, count, receiptCount, sequence;
                    return fields.Length == 17 && fields[0] == "D" &&
                           long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out revision) && revision == 1 &&
                           long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out count) && count == 1 &&
                           long.TryParse(fields[12], NumberStyles.Integer, CultureInfo.InvariantCulture, out receiptCount) && receiptCount == 1 &&
                           long.TryParse(fields[14], NumberStyles.Integer, CultureInfo.InvariantCulture, out sequence) && sequence == 1 &&
                           fields[8] == "True" && fields[9] == "True" && fields[10] == "True" && fields[11] == "False";
                }) && outputs.Distinct(StringComparer.Ordinal).Count() == 1,
                    "64 independent same-op receipts were not one revision/sequence/full receipt");
            }
            finally { TryDelete(dir); }
        }

        private static void RealFileAuthority64DifferentOperations()
        {
            var dir = TempDirectory();
            try
            {
                var session = Guid.NewGuid().ToString("N");
                var created = DurableRelaunchAuthorityFactory.TryCreatePristine(dir, session, 1, 7, 1000);
                Assert(created.Succeeded, "real 64 different-op bootstrap failed: " + created.Reason);
                var exe = Process.GetCurrentProcess().MainModule.FileName;
                var processes = new List<Process>();
                var ids = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < 64; i++)
                {
                    var id = Guid.NewGuid().ToString("N");
                    ids.Add(id);
                    processes.Add(StartAuthorityChild(exe, dir, session, id, Guid.NewGuid().ToString("N")));
                }
                var sequence = new HashSet<long>();
                foreach (var process in processes)
                {
                    Assert(process.WaitForExit(60000), "real different-op child timeout");
                    var output = process.StandardOutput.ReadToEnd().Trim();
                    Assert(process.ExitCode == 0, "real different-op child exit=" + process.ExitCode + " err=" + process.StandardError.ReadToEnd());
                    var fields = output.Split('|');
                    // The child emits the complete durable receipt, not just
                    // the revision.  Keep this gate strict so a truncated or
                    // partially serialized receipt cannot make the
                    // cross-process pressure test pass.
                    Assert(fields.Length == 17 && fields[0] == "D", "different-op child receipt malformed: " + output);
                    Assert(!string.IsNullOrEmpty(fields[3]) && !string.IsNullOrEmpty(fields[4]) &&
                        !string.IsNullOrEmpty(fields[5]) && !string.IsNullOrEmpty(fields[6]) &&
                        (fields[7] == "RelaunchApproved" || fields[7] == "RelaunchAlreadyPending") &&
                        fields[8] == "True" &&
                        fields[9] == "True" && fields[10] == "True" && fields[11] == "False",
                        "different-op child durable receipt fields malformed: " + output);
                    long revision, count, receiptCount, decisionSequence;
                    Assert(long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out revision),
                        "different-op child revision malformed: " + output);
                    Assert(long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out count) && count == revision &&
                          long.TryParse(fields[12], NumberStyles.Integer, CultureInfo.InvariantCulture, out receiptCount) && receiptCount == revision &&
                          long.TryParse(fields[14], NumberStyles.Integer, CultureInfo.InvariantCulture, out decisionSequence) && decisionSequence == revision,
                        "different-op child count/sequence malformed: " + output);
                    sequence.Add(revision);
                    process.Dispose();
                }
                var final = DurableRelaunchAuthorityFactory.TryOpenExisting(dir, session);
                Assert(final.Succeeded && final.Authority.Snapshot.ConsecutiveFailures == 64 && final.Authority.Snapshot.AuthorityRevision == 64,
                    "64 different operations lost durable count/revision");
                Assert(sequence.Count == 64 && sequence.Min() == 1 && sequence.Max() == 64, "different-op revisions were not 1..64");
            }
            finally { TryDelete(dir); }
        }

        private static long RealFileAuthority1000()
        {
            var dir = TempDirectory();
            var sw = Stopwatch.StartNew();
            try
            {
                var session = Guid.NewGuid().ToString("N");
                var created = DurableRelaunchAuthorityFactory.TryCreatePristine(dir, session, 1, 7, 2000);
                Assert(created.Succeeded, "real 1000-op bootstrap failed: " + created.Reason);
                var authority = created.Authority;
                for (var i = 0; i < 1000; i++)
                {
                    var operation = Operation(session, Guid.NewGuid().ToString("N"));
                    operation.MaximumProcessRelaunches = authority.Snapshot.MaximumProcessRelaunches;
                    var result = authority.RegisterFailureAndDecide(operation);
                    Assert(result.Durable && result.Receipt != null && result.Record != null,
                        "real 1000-op write was not durable at " + i);
                    Assert(result.Record.AuthorityRevision == i + 1 &&
                           result.Record.ConsecutiveFailures == i + 1 &&
                           result.Receipt.ConsecutiveCount == i + 1 &&
                           result.Receipt.DecisionSequence == i + 1,
                        "real 1000-op revision/count/sequence gap at " + i);
                }
                var reopened = DurableRelaunchAuthorityFactory.TryOpenExisting(dir, session);
                Assert(reopened.Succeeded && reopened.Authority.Snapshot.AuthorityRevision == 1000 && reopened.Authority.Snapshot.ConsecutiveFailures == 1000,
                    "real 1000-op final record mismatch");
                return sw.ElapsedMilliseconds;
            }
            finally { TryDelete(dir); }
        }

        private static Process StartAuthorityChild(string exe, string directory, string session, string operationId, string correlation)
        {
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(directory));
            var info = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--watchdog-authority-child " + encoded + " " + session + " " + operationId + " " + correlation,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(exe)
            };
            return Process.Start(info);
        }

        private static void BootstrapAndCapability()
        {
            var dir = TempDirectory();
            try
            {
                var session = Guid.NewGuid().ToString("N");
                var created = DurableRelaunchAuthorityFactory.TryCreatePristine(dir, session, 321, 123456789);
                Assert(created.Succeeded && created.Authority != null, "strict bootstrap failed: " + created.Reason);
                Assert(created.BootstrapReceipt != null && created.BootstrapReceipt.SchemaVersion == 4 &&
                       created.BootstrapReceipt.SessionId == session && created.BootstrapReceipt.ProcessId == 321 &&
                       File.Exists(created.BootstrapReceipt.Path), "bootstrap receipt incomplete");
                var raw = File.ReadAllBytes(created.BootstrapReceipt.Path);
                var sha = Sha(raw);
                Assert(sha == created.BootstrapReceipt.SHA256, "bootstrap sha mismatch");
                var opened = DurableRelaunchAuthorityFactory.TryOpenExisting(dir, session);
                Assert(opened.Succeeded && opened.Authority.Snapshot.State == DurableRelaunchPermitState.None, "replay open failed");
                var primaryBefore = File.ReadAllBytes(created.BootstrapReceipt.Path);
                var decision = opened.Authority.RegisterFailureAndDecide(Operation(session, Guid.NewGuid().ToString("N")));
                var primaryAfter = File.ReadAllBytes(created.BootstrapReceipt.Path);
                Assert(decision.Durable && primaryBefore.SequenceEqual(primaryAfter), "relaunch writer changed primary session snapshot");
                var second = DurableRelaunchAuthorityFactory.TryCreatePristine(dir, session, 322, 9);
                Assert(!second.Succeeded && second.Blocked, "second bootstrap overwrote existing authority");
                var relaunchPath = Path.Combine(dir, "session-" + session + ".relaunch.json");
                Assert(File.Exists(relaunchPath) && !string.Equals(created.BootstrapReceipt.Path, relaunchPath, StringComparison.Ordinal), "authority overwrote primary snapshot");
                Assert(File.ReadAllText(created.BootstrapReceipt.Path).IndexOf("RelaunchState", StringComparison.Ordinal) >= 0, "primary bootstrap missing legacy-visible state");
            }
            finally { TryDelete(dir); }
        }

        private static void MissingCorruptUnreadable()
        {
            var dir = TempDirectory();
            try
            {
                var session = Guid.NewGuid().ToString("N");
                var missing = DurableRelaunchAuthorityFactory.TryOpenExisting(dir, session);
                Assert(missing.Blocked && missing.Unproven && missing.Authority == null, "missing authority was not blocked");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "session-" + session + ".json"), "{bad", Encoding.UTF8);
                var corrupt = DurableRelaunchAuthorityFactory.TryOpenExisting(dir, session);
                Assert(corrupt.Blocked && corrupt.Unproven, "corrupt authority was not blocked");
                File.WriteAllText(Path.Combine(dir, "session-" + session + ".json"), "{\"SchemaVersion\":5}", Encoding.UTF8);
                var unknown = DurableRelaunchAuthorityFactory.TryOpenExisting(dir, session);
                Assert(unknown.Blocked && unknown.Unproven, "unknown schema was not blocked");
                var goodDir = TempDirectory();
                try
                {
                    var created = DurableRelaunchAuthorityFactory.TryCreatePristine(goodDir, session, 1, 5);
                    Assert(created.Succeeded, "failed to create corruption fixture");
                    File.WriteAllText(Path.Combine(goodDir, "session-" + session + ".relaunch.json"), "{bad", Encoding.UTF8);
                    var relaunchCorrupt = DurableRelaunchAuthorityFactory.TryOpenExisting(goodDir, session);
                    Assert(relaunchCorrupt.Blocked && relaunchCorrupt.Unproven, "corrupt relaunch was not blocked");
                }
                finally { TryDelete(goodDir); }
            }
            finally { TryDelete(dir); }
        }

        private static void OperationFreeze()
        {
            var session = Guid.NewGuid().ToString("N");
            var store = InMemoryDurableRelaunchAuthorityStoreV4.CreatePristine(session);
            var authority = store.OpenAuthority().Authority;
            var op = Operation(session, Guid.NewGuid().ToString("N"));
            var result = authority.RegisterFailureAndDecide(op);
            op.FailureCode = "MUTATED"; op.SidecarProcessId = 999999; op.DetailCode = "prose after call";
            Assert(result.Durable && result.Receipt != null && authority.Snapshot.LastFailureCode != "MUTATED", "authority retained caller mutation");
            var bad = Operation(session, Guid.NewGuid().ToString("N"));
            bad.SidecarProcessId = 0;
            var rejected = authority.RegisterFailureAndDecide(bad);
            Assert(!rejected.ActionAllowed && rejected.Blocked, "zero PID did not fail closed");
        }

        private static void CanonicalIdentityFields()
        {
            var baseline = Operation(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
            var first = baseline.CanonicalSha256();
            Action<RecoveryFailureOperation>[] changes =
            {
                x => x.SidecarProcessId++, x => x.SidecarProcessStartUtcTicks++, x => x.ConnectionGeneration++,
                x => x.RecoveryAttemptGeneration++, x => x.Permanent = !x.Permanent, x => x.RunEpoch++,
                x => x.MaximumProcessRelaunches++, x => x.DeviceOrChannelGroup = "EPB11",
                x => x.RecoveryProgressToken = "progress-2", x => x.FailureFingerprint = "fingerprint-2"
            };
            foreach (var change in changes) { var clone = baseline.Clone(); change(clone); Assert(first != clone.CanonicalSha256(), "canonical omitted stable field"); }
            var free = baseline.Clone(); free.DetailCode = "controlled-detail";
            Assert(first != free.CanonicalSha256(), "controlled DetailCode should be stable identity");
            free = baseline.Clone(); free.FailureReason = "operator prose changed"; free.DiagnosticProse = "stack text"; free.ExceptionText = "exception details";
            Assert(first == free.CanonicalSha256(), "diagnostic prose leaked into canonical identity");
            Assert(first.Length == 64 && first == first.ToUpperInvariant(), "canonical not uppercase SHA256");
        }

        private static void SameOperation64()
        {
            var session = Guid.NewGuid().ToString("N");
            var authority = InMemoryDurableRelaunchAuthorityStoreV4.CreatePristine(session, 8).OpenAuthority().Authority;
            var op = Operation(session, Guid.NewGuid().ToString("N"));
            var results = new DurableAuthorityDecisionResult[64];
            Parallel.For(0, 64, i => results[i] = authority.RegisterFailureAndDecide(op));
            Assert(results.All(x => x.Durable && x.Receipt != null), "same operation had nondurable result");
            Assert(results.Select(x => x.Receipt.DecisionSequence).Distinct().Count() == 1 &&
                   results.Select(x => x.Record.AuthorityRevision).Distinct().Count() == 1 &&
                   authority.Snapshot.ConsecutiveFailures == 1, "same operation incremented more than once");
        }

        private static void IndependentAuthority64()
        {
            var session = Guid.NewGuid().ToString("N");
            var store = InMemoryDurableRelaunchAuthorityStoreV4.CreatePristine(session, 8);
            var op = Operation(session, Guid.NewGuid().ToString("N"));
            var results = new DurableAuthorityDecisionResult[64];
            Parallel.For(0, 64, i => results[i] = store.OpenAuthority().Authority.RegisterFailureAndDecide(op));
            Assert(results.All(x => x.Durable && x.Receipt != null), "independent authority result not durable");
            Assert(results.Select(x => x.Record.AuthorityRevision).Distinct().Count() == 1, "independent CAS published multiple revisions");
        }

        private static void AuthorityBarrierExistingBlockedRace()
        {
            var dir = TempDirectory();
            try
            {
                var session = Guid.NewGuid().ToString("N");
                var created = DurableRelaunchAuthorityFactory.TryCreatePristine(dir, session, 7, 700, 8);
                Assert(created.Succeeded, "barrier race bootstrap failed");
                var operation = OperationWithBudget(session, 8);
                operation.Permanent = true;
                var barrier = new Barrier(2);
                var results = new DurableAuthorityDecisionResult[2];
                var tasks = Enumerable.Range(0, 2).Select(index => Task.Run(() =>
                {
                    var open = DurableRelaunchAuthorityFactory.TryOpenExisting(dir, session);
                    Assert(open.Succeeded && open.Authority != null, "barrier race open failed");
                    barrier.SignalAndWait(30000);
                    results[index] = open.Authority.RegisterFailureAndDecide(operation);
                })).ToArray();
                Task.WaitAll(tasks);
                var applied = results.FirstOrDefault(x => x.CommitStatus == DurableAuthorityCommitStatus.CandidateApplied);
                var loser = results.FirstOrDefault(x => x.CommitStatus == DurableAuthorityCommitStatus.ExistingBlocked);
                Assert(applied != null && loser != null,
                    "barrier race did not produce exactly CandidateApplied+ExistingBlocked: " +
                    string.Join(",", results.Select(x => x == null ? "null" : x.CommitStatus + "/" + x.Reason + "/" + x.Durable)));
                Assert(applied.Durable && applied.Record != null && applied.Record.CircuitOpen && applied.FailureRegistered,
                    "barrier winner was not a durable blocked candidate");
                Assert(loser.Durable && loser.Record != null && loser.Record.CircuitOpen &&
                       !loser.FailureRegistered && loser.Receipt != null &&
                       loser.Receipt.RequestCorrelationId == operation.RequestCorrelationId &&
                       loser.Receipt.ConsecutiveCount == 1,
                    "barrier loser was not an existing-blocked sticky receipt");
                Assert(DurableRelaunchAuthorityV4Validator.Serialize(applied.Record) ==
                       DurableRelaunchAuthorityV4Validator.Serialize(loser.Record),
                    "barrier loser record fields differ from the durable winner");
                Assert(applied.Receipt != null && loser.Receipt != null &&
                       loser.Receipt.RequestCorrelationId == applied.Receipt.RequestCorrelationId &&
                       loser.Receipt.RequestPayloadSha256 == applied.Receipt.RequestPayloadSha256 &&
                       loser.Receipt.FailureCode == applied.Receipt.FailureCode &&
                       loser.Receipt.FailureFingerprint == applied.Receipt.FailureFingerprint &&
                       loser.Receipt.Disposition == applied.Receipt.Disposition &&
                       loser.Receipt.Durable == applied.Receipt.Durable &&
                       loser.Receipt.PermitClosed == applied.Receipt.PermitClosed &&
                       loser.Receipt.CircuitOpen == applied.Receipt.CircuitOpen &&
                       loser.Receipt.ConsecutiveCount == applied.Receipt.ConsecutiveCount &&
                       loser.Receipt.RelaunchPermitGeneration == applied.Receipt.RelaunchPermitGeneration &&
                       loser.Receipt.DecisionSequence == applied.Receipt.DecisionSequence &&
                       loser.Receipt.DecisionUtcTicks == applied.Receipt.DecisionUtcTicks &&
                       loser.Receipt.DetailCode == applied.Receipt.DetailCode &&
                       !loser.Receipt.FailureRegistered && applied.Receipt.FailureRegistered,
                    "barrier loser receipt did not exactly replay winner evidence");
                var bytes = File.ReadAllBytes(Path.Combine(dir, "session-" + session + ".relaunch.json"));
                var reopened = DurableRelaunchAuthorityFactory.TryOpenExisting(dir, session);
                Assert(reopened.Succeeded && reopened.Authority.Snapshot.AuthorityRevision == 1 &&
                       bytes.SequenceEqual(File.ReadAllBytes(Path.Combine(dir, "session-" + session + ".relaunch.json"))),
                    "barrier loser changed durable bytes/revision");
            }
            finally { TryDelete(dir); }
        }

        private static void CorrelationConflict()
        {
            var session = Guid.NewGuid().ToString("N"); var store = InMemoryDurableRelaunchAuthorityStoreV4.CreatePristine(session, 8); var authority = store.OpenAuthority().Authority;
            var op = Operation(session, Guid.NewGuid().ToString("N")); var first = authority.RegisterFailureAndDecide(op);
            var conflict = op.Clone(); conflict.RequestPayloadSha256 = Hash('B'); var second = authority.RegisterFailureAndDecide(conflict);
            Assert(first.Durable && second.Blocked && second.Receipt.Disposition == RecoveryFailureDispositions.IdentityConflict && authority.Snapshot.State == DurableRelaunchPermitState.Blocked, "correlation conflict did not circuit-open");
        }

        private static void TupleConflict()
        {
            var session = Guid.NewGuid().ToString("N"); var authority = InMemoryDurableRelaunchAuthorityStoreV4.CreatePristine(session, 8).OpenAuthority().Authority;
            var op = Operation(session, Guid.NewGuid().ToString("N")); authority.RegisterFailureAndDecide(op); var changed = op.Clone(); changed.ConnectionGeneration++;
            var result = authority.RegisterFailureAndDecide(changed); Assert(result.Blocked && result.Receipt.Disposition == RecoveryFailureDispositions.IdentityConflict, "tuple change was replayed");
        }

        private static void DifferentOperationBudget()
        {
            var session = Guid.NewGuid().ToString("N"); var authority = InMemoryDurableRelaunchAuthorityStoreV4.CreatePristine(session, 5).OpenAuthority().Authority;
            var firstOp = Operation(session, Guid.NewGuid().ToString("N")); firstOp.MaximumProcessRelaunches = 5;
            var secondOp = Operation(session, Guid.NewGuid().ToString("N")); secondOp.MaximumProcessRelaunches = 5;
            var first = authority.RegisterFailureAndDecide(firstOp);
            var second = authority.RegisterFailureAndDecide(secondOp);
            Assert(first.ActionAllowed && second.Receipt.Disposition == RecoveryFailureDispositions.RelaunchAlreadyPending && authority.Snapshot.ConsecutiveFailures == 2,
                "different operation did not preserve pending/budget semantics: first=" + first.ActionAllowed + "/" + first.Reason + "/" + first.CommitStatus + ", second=" + second.Receipt?.Disposition + "/" + second.Reason + ", state=" + authority.Snapshot.State + ", count=" + authority.Snapshot.ConsecutiveFailures + ", rev=" + authority.Snapshot.AuthorityRevision);
        }

        private static void ReplayAfterRestart()
        {
            var dir = TempDirectory();
            try
            {
                var session = Guid.NewGuid().ToString("N"); var created = DurableRelaunchAuthorityFactory.TryCreatePristine(dir, session, 1, 5); Assert(created.Succeeded, created.Reason);
                var op = Operation(session, Guid.NewGuid().ToString("N")); var first = created.Authority.RegisterFailureAndDecide(op); var restarted = DurableRelaunchAuthorityFactory.TryOpenExisting(dir, session);
                var replay = restarted.Authority.RegisterFailureAndDecide(op); Assert(first.Durable && replay.Durable && replay.Reason == "IdempotentReplay" && replay.Receipt.DecisionSequence == first.Receipt.DecisionSequence, "restart replay reset authority");
            }
            finally { TryDelete(dir); }
        }

        private static void ApprovedRestartNoLaunch()
        {
            var session = Guid.NewGuid().ToString("N"); var authority = InMemoryDurableRelaunchAuthorityStoreV4.CreatePristine(session, 8).OpenAuthority().Authority; authority.RegisterFailureAndDecide(Operation(session, Guid.NewGuid().ToString("N")));
            var calls = 0; var result = authority.ReconcileAfterRestart((pid, start) => { calls++; return DurableRelaunchProcessObservation.Unknown; }); Assert(calls == 0 && result.OutcomeUnknown && !result.ActionAllowed && result.Decision.Blocked, "approved restart exposed launch/probe");
        }

        private static void StartedAliveNoSecondLaunch()
        {
            var session = Guid.NewGuid().ToString("N"); var store = InMemoryDurableRelaunchAuthorityStoreV4.CreatePristine(session, 8); var authority = store.OpenAuthority().Authority;
            authority.RegisterFailureAndDecide(Operation(session, Guid.NewGuid().ToString("N")));
            var record = authority.Snapshot;
            var executable = Path.GetFullPath(Process.GetCurrentProcess().MainModule.FileName);
            var intentValue = new DurableLaunchIntent
            {
                SessionId = session,
                SessionNonce = record.LastFailureSessionNonce,
                Generation = record.Generation,
                PermitId = record.PermitId,
                PermitNonce = record.PermitNonce,
                IntentId = Guid.NewGuid().ToString("N"),
                ExecutablePath = executable,
                ExecutableSha256 = new string('A', 64),
                Arguments = "--authority-reconcile-test",
                WorkingDirectory = Path.GetDirectoryName(executable),
                LaunchOptionsCanonical = DurableLaunchCanonical.RequiredOptionsCanonical
            };
            intentValue.LaunchSpecSha256 = DurableLaunchCanonical.Sha256(intentValue);
            var intent = authority.PrepareLaunchIntent(intentValue);
            Assert(intent.Succeeded, "started fixture intent failed");
            Assert(authority.ConsumeLaunchIntent(intent.Capability).Succeeded,
                "started fixture consume failed");
            Assert(authority.CommitStarted(
                    intent.Capability,
                    Process.GetCurrentProcess().Id + 3000,
                    Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks + 3).Succeeded,
                "started fixture commit failed");
            authority = store.OpenAuthority().Authority; var calls = 0; var result = authority.ReconcileAfterRestart((pid, start) => { calls++; return DurableRelaunchProcessObservation.Alive; }); Assert(calls == 1 && !result.ActionAllowed && result.Reason == "ProcessAlive", "alive started state attempted relaunch");
        }

        private static void CommitFailureTiers()
        {
            var session = Guid.NewGuid().ToString("N"); var store = InMemoryDurableRelaunchAuthorityStoreV4.CreatePristine(session); var authority = store.OpenAuthority().Authority; store.FailNextCommit = true;
            var result = authority.RegisterFailureAndDecide(Operation(session, Guid.NewGuid().ToString("N"))); Assert(result.Blocked && !result.ActionAllowed && result.Durable && result.CommitStatus == DurableAuthorityCommitStatus.CandidateApplied && result.Record.CircuitOpen, "first commit failure did not durable-block via candidate marker");
            Assert(authority.Snapshot.State == DurableRelaunchPermitState.Blocked, "marker did not publish blocked state");
        }

        private static void AllFailureBytesUnchanged()
        {
            var session = Guid.NewGuid().ToString("N"); var store = InMemoryDurableRelaunchAuthorityStoreV4.CreatePristine(session); var before = store.RawJson; store.FailAllCommits = true; var authority = store.OpenAuthority().Authority; var result = authority.RegisterFailureAndDecide(Operation(session, Guid.NewGuid().ToString("N"))); var reopened = store.OpenAuthority(); Assert(!result.Durable && !result.ActionAllowed && result.Blocked && store.RawJson == before && reopened.Succeeded && reopened.Authority.Snapshot.AuthorityRevision == 0 && result.RestartSafety == DurableAuthorityRestartSafety.Unproven, "all-failure path changed bytes or claimed durable circuit");
        }

        private static void MigrationMatrix()
        {
            var session = Guid.NewGuid().ToString("N");
            var pristine3 = "{\"SchemaVersion\":3,\"SessionId\":\"" + session + "\",\"State\":0,\"Generation\":0,\"RelaunchGeneration\":0,\"ProcessId\":0,\"CurrentPid\":0,\"ConsecutiveFailures\":0,\"CircuitOpen\":false}";
            var migrated = DurableRelaunchAuthorityV4Validator.ValidateAndMigrate(pristine3, session); Assert(migrated.Proven && migrated.Record.State == DurableRelaunchPermitState.None, "schema3 pristine did not migrate None");
            var ambiguous = DurableRelaunchAuthorityV4Validator.ValidateAndMigrate("{\"SchemaVersion\":3,\"SessionId\":\"" + session + "\",\"State\":2,\"Generation\":1}", session); Assert(ambiguous.Blocked && ambiguous.Record.State == DurableRelaunchPermitState.Blocked, "schema3 in-flight did not block");
            var mismatch = DurableRelaunchAuthorityV4Validator.ValidateAndMigrate(pristine3, Guid.NewGuid().ToString("N")); Assert(mismatch.Blocked, "migration session mismatch not blocked");
            var old4 = DurableRelaunchAuthorityV4Validator.ValidateAndMigrate("{\"SchemaVersion\":4,\"SessionId\":\"" + session + "\",\"State\":0,\"Generation\":0,\"MaximumProcessRelaunches\":1}", session); Assert(old4.Proven && old4.Record.State == DurableRelaunchPermitState.None, "old v4 pristine not retained");
        }

        private static void MigrationInvalidMatrix()
        {
            var session = Guid.NewGuid().ToString("N");
            foreach (var schema in new[] { 0, 1, 5, 99 })
            {
                var result = DurableRelaunchAuthorityV4Validator.ValidateAndMigrate("{\"SchemaVersion\":" + schema + ",\"SessionId\":\"" + session + "\",\"State\":0}", session);
                Assert(result.Blocked && !result.Proven, "unknown schema " + schema + " was not unproven");
            }
            var invalidState = DurableRelaunchAuthorityV4Validator.ValidateAndMigrate("{\"SchemaVersion\":4,\"RecordKind\":\"DurableRelaunchAuthority\",\"RecordFormatRevision\":1,\"SessionId\":\"" + session + "\",\"State\":\"not-a-state\"}", session);
            Assert(invalidState.Blocked && !invalidState.Proven, "invalid state was parsed as None");
            foreach (var state in new[] { "Approved", "LaunchIntent", "Started", "Attached" })
            {
                var result = DurableRelaunchAuthorityV4Validator.ValidateAndMigrate("{\"SchemaVersion\":4,\"SessionId\":\"" + session + "\",\"State\":\"" + state + "\",\"Generation\":1,\"PermitId\":\"p\",\"PermitNonce\":\"n\"}", session);
                Assert(result.Blocked, "old-v4 in-flight " + state + " was reopened");
            }
            var failedNoCanonical = DurableRelaunchAuthorityV4Validator.ValidateAndMigrate("{\"SchemaVersion\":4,\"SessionId\":\"" + session + "\",\"State\":\"Failed\",\"Generation\":1}", session);
            Assert(failedNoCanonical.Blocked, "failed old-v4 without canonical evidence was reopened");
        }

        private static void ToctouMutation()
        {
            var session = Guid.NewGuid().ToString("N"); var dir = TempDirectory();
            try
            {
                var created = DurableRelaunchAuthorityFactory.TryCreatePristine(dir, session, 3, 4); Assert(created.Succeeded, created.Reason); var path = Path.Combine(dir, "session-" + session + ".relaunch.json"); var before = File.ReadAllBytes(path); File.AppendAllText(path, "x", Encoding.UTF8); var open = DurableRelaunchAuthorityFactory.TryOpenExisting(dir, session); Assert(open.Blocked && open.Unproven, "TOCTOU-mutated authority opened"); File.WriteAllBytes(path, before); var restored = DurableRelaunchAuthorityFactory.TryOpenExisting(dir, session); Assert(restored.Succeeded, "valid restored snapshot did not open");
            }
            finally { TryDelete(dir); }
        }

        private static void SingleReplaceAndReadBack()
        {
            var session = Guid.NewGuid().ToString("N"); var store = InMemoryDurableRelaunchAuthorityStoreV4.CreatePristine(session); var authority = store.OpenAuthority().Authority; var first = authority.RegisterFailureAndDecide(Operation(session, Guid.NewGuid().ToString("N"))); Assert(first.Durable && store.ReplaceCount == 1, "decision did not perform exactly one store replace"); Assert(store.OpenAuthority().Authority.Snapshot.AuthorityRevision == 1, "read-back revision incorrect");
        }

        private static void CasStress1000()
        {
            var session = Guid.NewGuid().ToString("N"); var store = InMemoryDurableRelaunchAuthorityStoreV4.CreatePristine(session, 10000); var failures = 0; Parallel.For(0, 1000, i => { var operation = Operation(session, Guid.NewGuid().ToString("N")); operation.MaximumProcessRelaunches = store.OpenAuthority().Authority.Snapshot.MaximumProcessRelaunches; var result = store.OpenAuthority().Authority.RegisterFailureAndDecide(operation); if (!result.Durable && !result.Blocked) Interlocked.Increment(ref failures); }); Assert(failures == 0 && store.OpenAuthority().Authority.Snapshot.AuthorityRevision >= 1, "CAS stress lost durable result");
        }

        private static void DifferentSessionsIsolation()
        {
            var a = Guid.NewGuid().ToString("N"); var b = Guid.NewGuid().ToString("N"); var sa = InMemoryDurableRelaunchAuthorityStoreV4.CreatePristine(a); var sb = InMemoryDurableRelaunchAuthorityStoreV4.CreatePristine(b); var ra = sa.OpenAuthority().Authority.RegisterFailureAndDecide(Operation(a, Guid.NewGuid().ToString("N"))); var rb = sb.OpenAuthority().Authority.RegisterFailureAndDecide(Operation(b, Guid.NewGuid().ToString("N"))); Assert(ra.Durable && rb.Durable && sa.RawJson.IndexOf(b, StringComparison.Ordinal) < 0 && sb.RawJson.IndexOf(a, StringComparison.Ordinal) < 0, "session serializer crossed authority data");
        }

        private static void CaseInsensitivePathAlias()
        {
            var dir = TempDirectory();
            try
            {
                var session = Guid.NewGuid().ToString("N");
                var created = DurableRelaunchAuthorityFactory.TryCreatePristine(dir, session, 1, 7);
                Assert(created.Succeeded, "case alias fixture create failed");
                var alias = dir.ToUpperInvariant();
                var opened = DurableRelaunchAuthorityFactory.TryOpenExisting(alias, session);
                Assert(opened.Succeeded, "case-insensitive directory alias did not reopen authority");
                var result = opened.Authority.RegisterFailureAndDecide(Operation(session, Guid.NewGuid().ToString("N")));
                Assert(result.Durable && result.Record.AuthorityRevision == 1, "case alias did not share revision/mutex");
            }
            finally { TryDelete(dir); }
        }

        private static void ReceiptValidator()
        {
            var session = Guid.NewGuid().ToString("N");
            var corr = Guid.NewGuid().ToString("N");
            var receipt = new RecoveryFailureReceipt
            {
                RequestCorrelationId = corr, RequestPayloadSha256 = Hash('A'), FailureCode = "DAQUnavailable",
                FailureFingerprint = "fp", Disposition = RecoveryFailureDispositions.CircuitOpen,
                Durable = true, PermitClosed = true, FailureRegistered = true, CircuitOpen = true,
                ConsecutiveCount = 1, RelaunchPermitGeneration = 1, DecisionSequence = 1,
                DecisionUtcTicks = DateTime.UtcNow.Ticks, DetailCode = "CircuitOpen"
            };
            var message = new WatchdogMessage { ProtocolVersion = WatchdogProtocol.Version, Type = WatchdogMessageType.RecoveryAttemptFailedReceipt, SessionId = session, CorrelationId = corr, RecoveryFailureReceipt = receipt };
            RecoveryFailureReceipt parsed; string reason;
            Assert(WatchdogProtocol.TryValidateRecoveryFailureReceipt(message, session, corr, Hash('A'), out parsed, out reason), "strict receipt rejected: " + reason);
            var mutators = new Action<WatchdogMessage>[]
            {
                x => x.Reason = "prose", x => x.RecoveryFailureCode = "DAQUnavailable", x => x.RecoveryFailurePermanent = true,
                x => x.RecoveryFailureDetail = "detail", x => x.RecoveryFailureContextSha256 = Hash('B'), x => x.RecoveryFailureOwner = "main",
                x => x.RecoveryFailureCorrelationId = corr, x => x.RootCode = "root", x => x.DeviceOrChannelGroup = "EPB10",
                x => x.RunId = "run", x => x.RecoveryStage = "stage", x => x.RecoveryProgressToken = "token",
                x => x.RecoveryProcessSource = "source", x => x.RecoveryFailureFingerprint = "fp",
                x => x.RelaunchPermitGeneration = 1, x => x.RelaunchPermitId = "permit", x => x.RelaunchPermitNonce = "nonce",
                x => x.AckSequence = 1, x => x.RecoveryCommitGeneration = 1,
                x => x.Session = new WatchdogRunSession { SessionId = session }, x => x.Heartbeat = new WatchdogHeartbeat { SessionId = session },
                x => x.StopSummary = new WatchdogStopSummary(), x => x.CheckpointMirror = new WatchdogCheckpointMirror(),
                x => x.BatchStartFailure = new WatchdogBatchStartFailureContext()
            };
            foreach (var mutate in mutators)
            {
                var invalid = new WatchdogMessage
                {
                    ProtocolVersion = WatchdogProtocol.Version, Type = WatchdogMessageType.RecoveryAttemptFailedReceipt,
                    SessionId = session, CorrelationId = corr, RecoveryFailureReceipt = receipt.Clone()
                };
                mutate(invalid);
                Assert(!WatchdogProtocol.TryValidateRecoveryFailureReceipt(invalid, session, corr, Hash('A'), out parsed, out reason),
                    "unrelated receipt field was accepted: " + reason);
            }
            var nonce = new WatchdogMessage
            {
                ProtocolVersion = WatchdogProtocol.Version, Type = WatchdogMessageType.RecoveryAttemptFailedReceipt,
                SessionId = session, CorrelationId = corr, RelaunchPermitNonce = "launch-only",
                RecoveryFailureReceipt = receipt.Clone()
            };
            Assert(!WatchdogProtocol.TryValidateRecoveryFailureReceipt(nonce, session, corr, out parsed, out reason) && reason == "UnrelatedPayloadPresent", "nested launch nonce accepted");
        }

        private static void ExactProtocolVersions()
        {
            var session = Guid.NewGuid().ToString("N"); var corr = Guid.NewGuid().ToString("N"); var message = new WatchdogMessage { ProtocolVersion = WatchdogProtocol.Version, Type = WatchdogMessageType.RecoveryAttemptFailedReceipt, SessionId = session, CorrelationId = corr, RecoveryFailureReceipt = new RecoveryFailureReceipt { RequestCorrelationId = corr, RequestPayloadSha256 = Hash('A'), FailureCode = "x", FailureFingerprint = "fp", Disposition = RecoveryFailureDispositions.CircuitOpen, DecisionSequence = 1, DecisionUtcTicks = DateTime.UtcNow.Ticks, DetailCode = "CircuitOpen" } }; foreach (var version in new[] { 0, 2, 3 }) { message.ProtocolVersion = version; RecoveryFailureReceipt ignored; string reason; Assert(!WatchdogProtocol.TryValidateRecoveryFailureReceipt(message, session, corr, out ignored, out reason) && reason == "ProtocolVersion", "protocol version " + version + " was accepted"); }
        }

        private static void RawWireWhitelist()
        {
            var session = Guid.NewGuid().ToString("N");
            var corr = Guid.NewGuid().ToString("N");
            var payload = Hash('A');
            var receipt = new RecoveryFailureReceipt
            {
                RequestCorrelationId = corr, RequestPayloadSha256 = payload,
                FailureCode = "DAQUnavailable", FailureFingerprint = "fp",
                Disposition = RecoveryFailureDispositions.CircuitOpen,
                Durable = true, PermitClosed = true, FailureRegistered = false,
                CircuitOpen = true, ConsecutiveCount = 1, RelaunchPermitGeneration = 1,
                DecisionSequence = 1, DecisionUtcTicks = DateTime.UtcNow.Ticks,
                DetailCode = "CircuitOpen"
            };
            var source = new WatchdogMessage
            {
                ProtocolVersion = WatchdogProtocol.Version,
                Type = WatchdogMessageType.RecoveryAttemptFailedReceipt,
                SessionId = session, CorrelationId = corr, RecoveryFailureReceipt = receipt
            };
            var json = WatchdogProtocol.Serialize(source);
            WatchdogMessage parsedMessage; RecoveryFailureReceipt parsed; string reason;
            Assert(WatchdogProtocol.TryParseRecoveryFailureReceiptWire(
                       json, session, corr, payload, out parsedMessage, out parsed, out reason),
                "raw whitelist rejected canonical receipt: " + reason);
            var unknownTop = json.Replace("\"RecoveryFailureReceipt\"", "\"Unknown\":0,\"RecoveryFailureReceipt\"");
            Assert(!WatchdogProtocol.TryParseRecoveryFailureReceiptWire(
                       unknownTop, session, corr, payload, out parsedMessage, out parsed, out reason) &&
                   reason == "UnknownTopLevelKey", "unknown top-level key accepted");
            var unknownNested = json.Replace("\"DetailCode\"", "\"RelaunchPermitNonce\":\"secret\",\"DetailCode\"");
            Assert(!WatchdogProtocol.TryParseRecoveryFailureReceiptWire(
                       unknownNested, session, corr, payload, out parsedMessage, out parsed, out reason) &&
                   reason == "UnknownReceiptKey", "nested launch-only key accepted");
            var wrongType = json.Replace("\"DecisionSequence\":1", "\"DecisionSequence\":\"1\"");
            Assert(!WatchdogProtocol.TryParseRecoveryFailureReceiptWire(
                       wrongType, session, corr, payload, out parsedMessage, out parsed, out reason) &&
                   reason == "ReceiptType", "numeric field with string type accepted");
            var canonicalProtocol = "\"ProtocolVersion\":" +
                WatchdogProtocol.Version.ToString(CultureInfo.InvariantCulture);
            var wrongVersion = json.Replace(canonicalProtocol, "\"ProtocolVersion\":3");
            Assert(!WatchdogProtocol.TryParseRecoveryFailureReceiptWire(
                       wrongVersion, session, corr, payload, out parsedMessage, out parsed, out reason) &&
                   reason == "ProtocolVersion", "v2 raw receipt accepted");

            // Exhaustive required-key/type gate: a default value or a
            // serializer's implicit coercion must never turn malformed wire
            // data into a valid authority receipt.
            var missingTop = new[]
            {
                json.Replace(canonicalProtocol + ",", string.Empty),
                json.Replace("\"Type\":\"RecoveryAttemptFailedReceipt\",", string.Empty),
                json.Replace("\"SessionId\":\"" + session + "\",", string.Empty),
                json.Replace("\"CorrelationId\":\"" + corr + "\",", string.Empty),
                json.Replace("\"RecoveryFailureReceipt\":{", "\"RecoveryFailureReceipt\":null,")
            };
            foreach (var candidate in missingTop)
                Assert(!WatchdogProtocol.TryParseRecoveryFailureReceiptWire(candidate, session, corr, payload, out parsedMessage, out parsed, out reason),
                    "missing required top-level wire key was accepted");

            var badNested = new[]
            {
                json.Replace("\"RequestCorrelationId\":\"" + corr + "\"", "\"RequestCorrelationId\":1"),
                json.Replace("\"RequestPayloadSha256\":\"" + payload + "\"", "\"RequestPayloadSha256\":false"),
                json.Replace("\"FailureCode\":\"DAQUnavailable\"", "\"FailureCode\":1"),
                json.Replace("\"FailureFingerprint\":\"fp\"", "\"FailureFingerprint\":[]"),
                json.Replace("\"Disposition\":\"CircuitOpen\"", "\"Disposition\":1"),
                json.Replace("\"Durable\":true", "\"Durable\":\"true\""),
                json.Replace("\"PermitClosed\":true", "\"PermitClosed\":\"true\""),
                json.Replace("\"FailureRegistered\":false", "\"FailureRegistered\":\"false\""),
                json.Replace("\"CircuitOpen\":true", "\"CircuitOpen\":\"true\""),
                json.Replace("\"ConsecutiveCount\":1", "\"ConsecutiveCount\":\"1\""),
                json.Replace("\"RelaunchPermitGeneration\":1", "\"RelaunchPermitGeneration\":\"1\""),
                json.Replace("\"DecisionSequence\":1", "\"DecisionSequence\":\"1\""),
                json.Replace("\"DecisionUtcTicks\":" + receipt.DecisionUtcTicks.ToString(CultureInfo.InvariantCulture), "\"DecisionUtcTicks\":\"bad\""),
                json.Replace("\"DetailCode\":\"CircuitOpen\"", "\"DetailCode\":1")
            };
            foreach (var candidate in badNested)
                Assert(!WatchdogProtocol.TryParseRecoveryFailureReceiptWire(candidate, session, corr, payload, out parsedMessage, out parsed, out reason),
                    "wrong nested JSON type was accepted");
            var unknownType = json.Replace("\"Type\":\"RecoveryAttemptFailedReceipt\"", "\"Type\":\"Unknown\"");
            Assert(!WatchdogProtocol.TryParseRecoveryFailureReceiptWire(unknownType, session, corr, payload, out parsedMessage, out parsed, out reason),
                "unknown recovery receipt type was accepted");
        }

        private static void WireScrubsNonce()
        {
            var session = Guid.NewGuid().ToString("N"); var corr = Guid.NewGuid().ToString("N"); var message = new WatchdogMessage { ProtocolVersion = WatchdogProtocol.Version, Type = WatchdogMessageType.RecoveryAttemptFailedReceipt, SessionId = session, CorrelationId = corr, RelaunchPermitNonce = "secret", RelaunchPermitId = "launch-id", RelaunchPermitGeneration = 9, Session = new WatchdogRunSession { SessionId = session, RelaunchPermitNonce = "nested-secret" }, Heartbeat = new WatchdogHeartbeat { SessionId = session }, RecoveryFailureReceipt = new RecoveryFailureReceipt { RequestCorrelationId = corr, RequestPayloadSha256 = Hash('A'), FailureCode = "x", FailureFingerprint = "fp", Disposition = RecoveryFailureDispositions.CircuitOpen, DecisionSequence = 1, DecisionUtcTicks = DateTime.UtcNow.Ticks, DetailCode = "CircuitOpen" } }; var json = WatchdogProtocol.Serialize(message); Assert(json.IndexOf("secret", StringComparison.Ordinal) < 0 && json.IndexOf("launch-id", StringComparison.Ordinal) < 0 && json.IndexOf("Heartbeat", StringComparison.Ordinal) < 0 && json.IndexOf("\"Session\"", StringComparison.Ordinal) < 0, "wire receipt reflected unrelated/launch fields");
        }

        private static void RecoveryFailureRequestWire()
        {
            var session = Guid.NewGuid().ToString("N");
            var correlation = Guid.NewGuid().ToString("N");
            var request = new WatchdogMessage
            {
                ProtocolVersion = WatchdogProtocol.Version,
                Type = WatchdogMessageType.RecoveryAttemptFailed,
                SessionId = session,
                CorrelationId = correlation,
                Reason = "diagnostic prose",
                RecoveryFailureCode = "CheckpointInvariant",
                RecoveryFailurePermanent = true,
                RecoveryFailureDetail = "stack detail",
                RecoveryFailureContextSha256 = Hash('C'),
                RecoveryFailureOwner = string.Empty,
                RecoveryFailureCorrelationId = string.Empty,
                RootCode = "CheckpointInvariant",
                DeviceOrChannelGroup = "RecoveryCheckpoint",
                RunId = "run-1",
                RecoveryStage = "RecoveryAdmission",
                RecoveryProgressToken = "progress-1",
                RecoveryProcessSource = RecoveryFailurePolicy.RecoveryProcessSource,
                RecoveryFailureFingerprint = "RFP3-" + new string('a', 64),
                // These must be scrubbed by the request serializer.
                RelaunchPermitGeneration = 99,
                RelaunchPermitId = "not-on-wire",
                RelaunchPermitNonce = "secret"
            };
            var wire = WatchdogProtocol.Serialize(request);
            Assert(wire.IndexOf("not-on-wire", StringComparison.Ordinal) < 0 &&
                   wire.IndexOf("secret", StringComparison.Ordinal) < 0 &&
                   wire.IndexOf("RelaunchPermit", StringComparison.Ordinal) < 0,
                "request reflected launch-only authority");
            WatchdogMessage parsed;
            string payload;
            string reason;
            Assert(WatchdogProtocol.TryParseRecoveryFailureRequestWire(
                    wire, session, out parsed, out payload, out reason),
                "canonical request rejected: " + reason);
            Assert(payload == WatchdogProtocol.ComputeWireSha256(wire) &&
                   parsed.CorrelationId == correlation &&
                   parsed.RecoveryFailurePermanent &&
                   parsed.RecoveryFailureFingerprint ==
                       request.RecoveryFailureFingerprint,
                "raw request identity/hash changed");

            var retry = WatchdogProtocol.Serialize(parsed);
            Assert(string.Equals(wire, retry, StringComparison.Ordinal) &&
                   WatchdogProtocol.ComputeWireSha256(retry) == payload,
                "same correlation request was not byte-stable");
            var changedProse = wire.Replace(
                "diagnostic prose", "different diagnostic prose");
            Assert(WatchdogProtocol.TryParseRecoveryFailureRequestWire(
                       changedProse, session, out _, out var changedHash, out reason) &&
                   changedHash != payload,
                "changed raw request did not change operation payload hash");
            var unknown = wire.Replace(
                "\"Reason\"", "\"RelaunchPermitNonce\":\"secret\",\"Reason\"");
            Assert(!WatchdogProtocol.TryParseRecoveryFailureRequestWire(
                       unknown, session, out _, out _, out reason) &&
                   reason == "RequestShape",
                "unknown/launch-only request key accepted");
            var v2 = wire.Replace(
                "\"ProtocolVersion\":" + WatchdogProtocol.Version.ToString(CultureInfo.InvariantCulture),
                "\"ProtocolVersion\":3");
            Assert(!WatchdogProtocol.TryParseRecoveryFailureRequestWire(
                       v2, session, out _, out _, out reason) &&
                   reason == "RequestProtocolOrType",
                "v2 failure request accepted");
            var wrongSession = Guid.NewGuid().ToString("N");
            Assert(!WatchdogProtocol.TryParseRecoveryFailureRequestWire(
                       wire, wrongSession, out _, out _, out reason) &&
                   reason == "RequestIdentity",
                "wrong expected session accepted");
        }

        private static void CultureAndReplay()
        {
            var op = Operation(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N")); var old = CultureInfo.CurrentCulture; try { CultureInfo.CurrentCulture = new CultureInfo("tr-TR"); var a = op.CanonicalSha256(); CultureInfo.CurrentCulture = new CultureInfo("en-US"); var b = op.Clone().CanonicalSha256(); Assert(a == b, "culture changed canonical digest"); } finally { CultureInfo.CurrentCulture = old; }
        }

        private static void ProductionFileIoFailureTiers()
        {
            // These cases use the production FileStore/CAS implementation;
            // only the final atomic I/O boundary is fault-injected.
            var decisionFailureDir = TempDirectory();
            var markerFailureDir = TempDirectory();
            var createFailureDir = TempDirectory();
            var readBackFailureDir = TempDirectory();
            var proofSwapDir = TempDirectory();
            var readFailureDir = TempDirectory();
            try
            {
                var session = Guid.NewGuid().ToString("N");
                var io = new FaultingAuthorityIo();
                var authority = CreateInjectedAuthority(decisionFailureDir, session, io, 8);
                io.FailReplaceCount = 1;
                var markerResult = authority.RegisterFailureAndDecide(OperationWithBudget(session, 8));
                Assert(markerResult.Durable && markerResult.Blocked && markerResult.Record != null &&
                       markerResult.Receipt != null &&
                       markerResult.CommitStatus == DurableAuthorityCommitStatus.CandidateApplied &&
                       markerResult.Record.CircuitOpen,
                    "decision write failure did not produce a validated durable blocked marker");
                var reopened = DurableRelaunchAuthorityFactory.TryOpenExisting(decisionFailureDir, session);
                Assert(reopened.Succeeded && reopened.Authority.Snapshot.State == DurableRelaunchPermitState.Blocked,
                    "durable marker was not reopenable after decision write failure");

                // Create/Flush failure is a failed candidate boundary.  The
                // compensating blocked marker is then allowed to commit, but
                // only as CandidateApplied with a complete read-back pair.
                var createSession = Guid.NewGuid().ToString("N");
                var createIo = new FaultingAuthorityIo();
                var createAuthority = CreateInjectedAuthority(createFailureDir, createSession, createIo, 8);
                createIo.FailCreateCount = 1;
                var createFailure = createAuthority.RegisterFailureAndDecide(OperationWithBudget(createSession, 8));
                Assert(createFailure.Durable && createFailure.Blocked &&
                       createFailure.CommitStatus == DurableAuthorityCommitStatus.CandidateApplied &&
                       createFailure.Record != null && createFailure.Record.CircuitOpen,
                    "Create/Flush failure did not produce CandidateApplied blocked marker");
                var createReopen = DurableRelaunchAuthorityFactory.TryOpenExisting(createFailureDir, createSession);
                Assert(createReopen.Succeeded && createReopen.Authority.Snapshot.State == DurableRelaunchPermitState.Blocked,
                    "Create/Flush failure marker was not reopenable");

                // A replacement can succeed while its first read-back is
                // unavailable.  The outer transaction must compensate with
                // one durable marker; neither partial bytes nor a missing
                // authority may be observed.
                var readBackSession = Guid.NewGuid().ToString("N");
                var readBackIo = new FaultingAuthorityIo();
                var readBackAuthority = CreateInjectedAuthority(readBackFailureDir, readBackSession, readBackIo, 8);
                readBackIo.FailReadAfterReplaceCount = 4;
                var readBack = readBackAuthority.RegisterFailureAndDecide(OperationWithBudget(readBackSession, 8));
                Assert(readBack.Durable && readBack.Blocked &&
                       readBack.CommitStatus == DurableAuthorityCommitStatus.CandidateApplied &&
                       readBack.Record != null && readBack.Record.CircuitOpen,
                    "replace/read-back failure did not converge to CandidateApplied marker: " + readBack.CommitStatus + "/" + readBack.Durable + "/" + readBack.Blocked + "/" + readBack.Reason + "/" + readBack.Record?.State + "/remainingReads=" + readBackIo.FailReadCount);
                var readBackReopen = DurableRelaunchAuthorityFactory.TryOpenExisting(readBackFailureDir, readBackSession);
                Assert(readBackReopen.Succeeded && readBackReopen.Authority.Snapshot.State == DurableRelaunchPermitState.Blocked,
                    "replace/read-back failure left incomplete authority bytes");

                var markerSession = Guid.NewGuid().ToString("N");
                var markerIo = new FaultingAuthorityIo();
                var markerAuthority = CreateInjectedAuthority(markerFailureDir, markerSession, markerIo, 8);
                var markerPath = Path.Combine(markerFailureDir, "session-" + markerSession + ".relaunch.json");
                var before = File.ReadAllBytes(markerPath);
                markerIo.FailReplaceCount = 2;
                var allFailed = markerAuthority.RegisterFailureAndDecide(OperationWithBudget(markerSession, 8));
                Assert(!allFailed.Durable && !allFailed.ActionAllowed && allFailed.Blocked &&
                       allFailed.RestartSafety == DurableAuthorityRestartSafety.Unproven &&
                       before.SequenceEqual(File.ReadAllBytes(markerPath)),
                    "three-tier all-failure path changed bytes or claimed durable safety");
                var afterAllFailed = DurableRelaunchAuthorityFactory.TryOpenExisting(markerFailureDir, markerSession);
                Assert(afterAllFailed.Succeeded && afterAllFailed.Authority.Snapshot.AuthorityRevision == 0,
                    "all-failure old bytes were not reopenable");

                var proofSession = Guid.NewGuid().ToString("N");
                var proofIo = new FaultingAuthorityIo();
                var proofAuthority = CreateInjectedAuthority(proofSwapDir, proofSession, proofIo, 8);
                var proof = proofAuthority.Capability.ProofPath;
                var proofBytes = File.ReadAllBytes(proof);
                proofBytes[proofBytes.Length - 2] = (byte)(proofBytes[proofBytes.Length - 2] == (byte)'0' ? (byte)'1' : (byte)'0');
                File.WriteAllBytes(proof, proofBytes);
                var proofPath = Path.Combine(proofSwapDir, "session-" + proofSession + ".relaunch.json");
                var proofAuthorityBytes = File.ReadAllBytes(proofPath);
                var proofFailure = proofAuthority.RegisterFailureAndDecide(OperationWithBudget(proofSession, 8));
                Assert(proofFailure.CommitStatus == DurableAuthorityCommitStatus.ReadFailed &&
                       !proofFailure.Durable && proofFailure.Record == null && proofFailure.Receipt == null &&
                       proofAuthorityBytes.SequenceEqual(File.ReadAllBytes(proofPath)),
                    "proof mismatch was not typed ReadFailed with null record");

                var readSession = Guid.NewGuid().ToString("N");
                var readIo = new FaultingAuthorityIo();
                var readAuthority = CreateInjectedAuthority(readFailureDir, readSession, readIo, 8);
                readIo.FailReadCount = 8;
                var readFailure = readAuthority.RegisterFailureAndDecide(OperationWithBudget(readSession, 8));
                Assert(readFailure.CommitStatus == DurableAuthorityCommitStatus.ReadFailed &&
                       !readFailure.Durable && readFailure.Record == null && readFailure.Receipt == null,
                    "read failure was not fail-closed with null record");
            }
            finally
            {
                TryDelete(decisionFailureDir); TryDelete(markerFailureDir); TryDelete(createFailureDir); TryDelete(readBackFailureDir);
                TryDelete(proofSwapDir); TryDelete(readFailureDir);
            }
        }

        private static void MutexBusyRetry()
        {
            var dir = TempDirectory();
            try
            {
                var session = Guid.NewGuid().ToString("N");
                var created = DurableRelaunchAuthorityFactory.TryCreatePristine(dir, session, 7, 700, 8);
                Assert(created.Succeeded, "busy fixture bootstrap failed");
                var busy = false;
                var store = new DurableRelaunchAuthorityFileStore(dir, session, new WindowsDurableAuthorityFileIo(),
                    milliseconds => !busy);
                var capability = new DurableRelaunchAuthorityBootstrapCapability(created.BootstrapReceipt, store.CanonicalPath);
                var authority = new DurableRelaunchAuthorityV4(session, store, capability);
                busy = true;
                var blocked = authority.RegisterFailureAndDecide(OperationWithBudget(session, 8));
                Assert(blocked.CommitStatus == DurableAuthorityCommitStatus.Busy && !blocked.Durable &&
                       !blocked.ActionAllowed && blocked.Unproven && !blocked.BlockedInCurrentProcess &&
                       authority.Snapshot.State == DurableRelaunchPermitState.None,
                    "mutex timeout was not typed Busy/retryable");
                busy = false;
                var retried = authority.RegisterFailureAndDecide(OperationWithBudget(session, 8));
                Assert(retried.Durable && retried.ActionAllowed && retried.Record.AuthorityRevision == 1 &&
                       retried.Record.ConsecutiveFailures == 1,
                    "authority did not recover after transient Busy admission");
            }
            finally { TryDelete(dir); }
        }

        private static void CommitBusyAfterSuccessfulLoad()
        {
            var dir = TempDirectory();
            try
            {
                var session = Guid.NewGuid().ToString("N");
                var created = DurableRelaunchAuthorityFactory.TryCreatePristine(dir, session, 7, 700, 8);
                Assert(created.Succeeded, "commit-busy fixture bootstrap failed");
                var admission = 0;
                // The first Load is admitted, the immediately following
                // TryCommit is Busy.  The next complete transaction is
                // admitted again, proving Busy is not converted into a
                // marker/circuit by the authority.
                var store = new DurableRelaunchAuthorityFileStore(
                    dir, session, new WindowsDurableAuthorityFileIo(),
                    milliseconds => Interlocked.Increment(ref admission) != 2);
                var capability = new DurableRelaunchAuthorityBootstrapCapability(
                    created.BootstrapReceipt, store.CanonicalPath);
                var authority = new DurableRelaunchAuthorityV4(session, store, capability);
                var first = authority.RegisterFailureAndDecide(OperationWithBudget(session, 8));
                Assert(first.CommitStatus == DurableAuthorityCommitStatus.Busy &&
                       !first.Durable && !first.ActionAllowed && first.Unproven &&
                       !first.BlockedInCurrentProcess && !authority.IsBlockedInCurrentProcess,
                    "successful Load followed by Busy commit was not retryable");
                var path = Path.Combine(dir, "session-" + session + ".relaunch.json");
                var bytesAfterBusy = File.ReadAllBytes(path);
                var second = authority.RegisterFailureAndDecide(OperationWithBudget(session, 8));
                Assert(second.Durable && second.ActionAllowed &&
                       (second.CommitStatus == DurableAuthorityCommitStatus.CandidateApplied ||
                        second.CommitStatus == DurableAuthorityCommitStatus.Committed),
                    "authority did not retry after Commit Busy");
                Assert(authority.Snapshot.AuthorityRevision == 1 &&
                       authority.Snapshot.State == DurableRelaunchPermitState.Approved &&
                       !bytesAfterBusy.SequenceEqual(File.ReadAllBytes(path)),
                    "Commit Busy created a marker or prevented the later candidate");
            }
            finally { TryDelete(dir); }
        }

        private static void ActiveStateEvidenceMutation()
        {
            foreach (var state in new[]
            {
                DurableRelaunchPermitState.Approved,
                DurableRelaunchPermitState.LaunchIntent,
                DurableRelaunchPermitState.Started,
                DurableRelaunchPermitState.Attached
            })
            {
                var dir = TempDirectory();
                try
                {
                    var session = Guid.NewGuid().ToString("N");
                    var created = DurableRelaunchAuthorityFactory.TryCreatePristine(dir, session, 7, 700, 8);
                    Assert(created.Succeeded, "active " + state + " fixture bootstrap failed");
                    var authority = created.Authority;
                    var first = authority.RegisterFailureAndDecide(OperationWithBudget(session, 8));
                    Assert(first.Durable && first.Record != null && first.Record.State == DurableRelaunchPermitState.Approved,
                        "active " + state + " evidence setup failed");
                    if (state != DurableRelaunchPermitState.Approved)
                    {
                        var executable = Path.GetFullPath(Process.GetCurrentProcess().MainModule.FileName);
                        var launch = new DurableLaunchIntent
                        {
                            SessionId = session,
                            SessionNonce = first.Record.LastFailureSessionNonce,
                            Generation = first.Record.Generation,
                            PermitId = first.Record.PermitId,
                            PermitNonce = first.Record.PermitNonce,
                            IntentId = Guid.NewGuid().ToString("N"),
                            ExecutablePath = executable,
                            ExecutableSha256 = new string('A', 64),
                            Arguments = "--active-state-evidence",
                            WorkingDirectory = Path.GetDirectoryName(executable),
                            LaunchOptionsCanonical = DurableLaunchCanonical.RequiredOptionsCanonical
                        };
                        launch.LaunchSpecSha256 = DurableLaunchCanonical.Sha256(launch);
                        var prepared = authority.PrepareLaunchIntent(launch);
                        Assert(prepared.Succeeded, "active launch fixture prepare failed");
                        if (state == DurableRelaunchPermitState.Started ||
                            state == DurableRelaunchPermitState.Attached)
                        {
                            Assert(authority.ConsumeLaunchIntent(prepared.Capability).Succeeded,
                                "active launch fixture consume failed");
                            Assert(authority.CommitStarted(
                                    prepared.Capability,
                                    9007,
                                    900700).Succeeded,
                                "active launch fixture started failed");
                            if (state == DurableRelaunchPermitState.Attached)
                                Assert(authority.CommitAttached(prepared.Capability).Succeeded,
                                    "active launch fixture attached failed");
                        }
                    }
                    var valid = authority.Snapshot;
                    Assert(valid.State == state,
                        "active fixture ended in " + valid.State + " expected " + state);
                    WriteRelaunchJson(dir, session, valid);
                    var validOpen = DurableRelaunchAuthorityFactory.TryOpenExisting(dir, session);
                    Assert(validOpen.Succeeded && validOpen.Authority != null &&
                           validOpen.Authority.Snapshot.State == state,
                        "valid active " + state + " record was rejected");

                    var mutations = new List<Action<DurableRelaunchAuthorityRecord>>
                    {
                        x => x.ConnectionGeneration++,
                        x => x.RecoveryAttemptGeneration++,
                        x => x.RunEpoch++,
                        x => x.RecoveryStage = "mutated-stage",
                        x => x.RecoveryProgressToken = "mutated-progress",
                        x => x.PermitId = Guid.NewGuid().ToString("N"),
                        x => x.PermitNonce = Guid.NewGuid().ToString("N"),
                        x => x.LastFailureConnectionGeneration++,
                        x => x.LastFailurePermitNonce = Guid.NewGuid().ToString("N")
                    };
                    if (state == DurableRelaunchPermitState.Started || state == DurableRelaunchPermitState.Attached)
                    {
                        mutations.Add(x => x.ProcessId = 0);
                        mutations.Add(x => x.ProcessStartUtcTicks = 0);
                        mutations.Add(x => x.LastFailureProcessId = 0);
                        mutations.Add(x => x.LastFailureProcessStartUtcTicks = 0);
                        mutations.Add(x => x.LaunchConsumed = false);
                    }
                    else
                    {
                        mutations.Add(x => x.ProcessId = 1);
                        mutations.Add(x => x.ProcessStartUtcTicks = 1);
                    }
                    foreach (var mutate in mutations)
                    {
                        var candidate = valid.Clone();
                        mutate(candidate);
                        WriteRelaunchJson(dir, session, candidate);
                        var rejected = DurableRelaunchAuthorityFactory.TryOpenExisting(dir, session);
                        Assert(rejected.Blocked && rejected.Unproven && rejected.Authority == null,
                            "active " + state + " mutation was accepted: " + rejected.Reason);
                    }
                }
                finally { TryDelete(dir); }
            }
        }

        private static DurableRelaunchAuthorityV4 CreateInjectedAuthority(
            string directory, string session, FaultingAuthorityIo io, int budget)
        {
            var created = DurableRelaunchAuthorityFactory.TryCreatePristine(directory, session, 7, 700, budget);
            Assert(created.Succeeded && created.BootstrapReceipt != null, "injected authority bootstrap failed");
            var store = new DurableRelaunchAuthorityFileStore(directory, session, io);
            var capability = new DurableRelaunchAuthorityBootstrapCapability(created.BootstrapReceipt, store.CanonicalPath);
            return new DurableRelaunchAuthorityV4(session, store, capability);
        }

        private static void ProductionProofBudgetSticky()
        {
            var dir = TempDirectory();
            WatchdogJournalStore journal = null;
            try
            {
                var session = Guid.NewGuid().ToString("N");
                var created = DurableRelaunchAuthorityFactory.TryCreatePristine(dir, session, 7, 700, 2);
                Assert(created.Succeeded && created.BootstrapReceipt != null &&
                    !string.IsNullOrEmpty(created.BootstrapReceipt.ProofPath), "production proof bootstrap failed");
                // Exercise the real primary journal writer alongside the
                // independent relaunch authority.  The mutable primary may
                // advance repeatedly; the immutable proof and relaunch file
                // must remain separate and reopenable throughout.
                journal = new WatchdogJournalStore(dir, session, "sidecar", new WatchdogJournalPolicy(), 7, 700);
                var proofPath = created.BootstrapReceipt.ProofPath;
                var proofBefore = File.ReadAllBytes(proofPath);
                // The mutable primary/main journal may advance independently;
                // the strict authority must remain bound to the immutable
                // proof rather than to the primary file's current bytes.
                Assert(journal.TryPublishSnapshotSynchronously(
                    "{\"SchemaVersion\":4,\"SessionId\":\"" + session + "\",\"State\":\"Running-1\"}"),
                    "real journal primary update failed");
                var reopened = DurableRelaunchAuthorityFactory.TryOpenExisting(dir, session);
                Assert(reopened.Succeeded && reopened.Authority.Snapshot.MaximumProcessRelaunches == 2,
                    "mutable primary prevented proof-bound reopen");
                Assert(proofBefore.SequenceEqual(File.ReadAllBytes(proofPath)), "bootstrap proof changed after primary update");
                Assert(journal.TryPublishSnapshotSynchronously(
                    "{\"SchemaVersion\":4,\"SessionId\":\"" + session + "\",\"State\":\"Running-2\"}"),
                    "real journal second primary update failed");
                var reopenedAgain = DurableRelaunchAuthorityFactory.TryOpenExisting(dir, session);
                Assert(reopenedAgain.Succeeded && proofBefore.SequenceEqual(File.ReadAllBytes(proofPath)),
                    "authority/proof was coupled to the mutable journal writer");
                var tamperedProof = proofBefore.ToArray();
                tamperedProof[tamperedProof.Length - 2] = (byte)(tamperedProof[tamperedProof.Length - 2] == (byte)'0' ? (byte)'1' : (byte)'0');
                File.WriteAllBytes(proofPath, tamperedProof);
                var relaunchPath = Path.Combine(dir, "session-" + session + ".relaunch.json");
                var authorityBeforeProofSwap = File.ReadAllBytes(relaunchPath);
                var staleAuthorityDecision = reopened.Authority.RegisterFailureAndDecide(OperationWithBudget(session, 2));
                Assert(staleAuthorityDecision.CommitStatus == DurableAuthorityCommitStatus.ReadFailed &&
                       !staleAuthorityDecision.Durable && staleAuthorityDecision.Record == null &&
                       staleAuthorityDecision.Receipt == null && authorityBeforeProofSwap.SequenceEqual(File.ReadAllBytes(relaunchPath)),
                       "proof-swapped live authority exposed stale record or changed bytes");
                var tampered = DurableRelaunchAuthorityFactory.TryOpenExisting(dir, session);
                Assert(tampered.Blocked && tampered.Unproven, "tampered immutable bootstrap proof was accepted");
                File.WriteAllBytes(proofPath, proofBefore);
                reopened = DurableRelaunchAuthorityFactory.TryOpenExisting(dir, session);
                Assert(reopened.Succeeded, "restored immutable bootstrap proof did not reopen");

                var first = reopened.Authority.RegisterFailureAndDecide(OperationWithBudget(session, 2));
                Assert(first.Durable && first.ActionAllowed && first.Record.State == DurableRelaunchPermitState.Approved,
                    "first production decision did not consume frozen budget");
                var exhausted = reopened.Authority.RegisterFailureAndDecide(OperationWithBudget(session, 2));
                Assert(exhausted.Durable && exhausted.Blocked && !exhausted.ActionAllowed &&
                    (exhausted.CommitStatus == DurableAuthorityCommitStatus.Committed ||
                     exhausted.CommitStatus == DurableAuthorityCommitStatus.CandidateApplied),
                    "budget exhaustion did not persist a blocked decision");
                var blockedBytes = File.ReadAllBytes(relaunchPath);
                var reopenedBlocked = DurableRelaunchAuthorityFactory.TryOpenExisting(dir, session);
                Assert(reopenedBlocked.Succeeded && reopenedBlocked.Authority.Snapshot.State == DurableRelaunchPermitState.Blocked,
                    "blocked authority did not reopen as blocked");
                var changed = OperationWithBudget(session, 999);
                var sticky = reopenedBlocked.Authority.RegisterFailureAndDecide(changed);
                Assert(sticky.Durable && sticky.Blocked && !sticky.ActionAllowed &&
                    sticky.CommitStatus == DurableAuthorityCommitStatus.ExistingBlocked &&
                    sticky.Receipt != null && !sticky.FailureRegistered &&
                    blockedBytes.SequenceEqual(File.ReadAllBytes(relaunchPath)),
                    "sticky blocked authority advanced/reopened on new budget/correlation");

                var mismatchDir = TempDirectory();
                try
                {
                    var mismatchSession = Guid.NewGuid().ToString("N");
                    var mismatchCreate = DurableRelaunchAuthorityFactory.TryCreatePristine(mismatchDir, mismatchSession, 7, 701, 3);
                    Assert(mismatchCreate.Succeeded, "budget mismatch fixture failed");
                    var mismatch = mismatchCreate.Authority.RegisterFailureAndDecide(OperationWithBudget(mismatchSession, 4));
                    var mismatchOpen = DurableRelaunchAuthorityFactory.TryOpenExisting(mismatchDir, mismatchSession);
                    Assert(!mismatch.Durable && mismatch.Blocked && !mismatch.ActionAllowed && !mismatch.FailureRegistered &&
                        mismatch.CommitStatus == DurableAuthorityCommitStatus.Invalid && mismatch.Reason == "BudgetMismatch" &&
                        mismatch.Record != null && mismatch.Record.ConsecutiveFailures == 0 && mismatch.Record.AuthorityRevision == 0 &&
                        mismatchOpen.Succeeded && mismatchOpen.Authority.Snapshot.State == DurableRelaunchPermitState.None,
                        "operation budget mismatch was registered or raised authority budget: decision=" + mismatch.CommitStatus + "/" + mismatch.Durable + "/" + mismatch.Blocked + "/" + mismatch.Reason + ", open=" + mismatchOpen.Reason + "/" + mismatchOpen.Blocked + "/" + mismatchOpen.Unproven);
                }
                finally { TryDelete(mismatchDir); }
            }
            finally
            {
                try { journal?.Dispose(); } catch { }
                TryDelete(dir);
            }
        }

        private static RecoveryFailureOperation OperationWithBudget(string session, int budget)
        {
            var operation = Operation(session, Guid.NewGuid().ToString("N"));
            operation.MaximumProcessRelaunches = budget;
            return operation;
        }

        private static void ProductionBlockedMigration()
        {
            var dir = TempDirectory();
            try
            {
                var session = Guid.NewGuid().ToString("N");
                var created = DurableRelaunchAuthorityFactory.TryCreatePristine(dir, session, 9, 900, 10);
                Assert(created.Succeeded, "blocked migration bootstrap failed");
                var relaunchPath = Path.Combine(dir, "session-" + session + ".relaunch.json");
                var legacy = "{\"SchemaVersion\":3,\"SessionId\":\"" + session + "\",\"State\":2,\"Generation\":1,\"PermitId\":\"legacy\",\"PermitNonce\":\"legacy-nonce\",\"ProcessId\":9,\"ProcessStartUtcTicks\":900,\"ConsecutiveFailures\":1}";
                File.WriteAllText(relaunchPath, legacy, new UTF8Encoding(false));
                var first = DurableRelaunchAuthorityFactory.TryOpenExisting(dir, session);
                Assert(first.Succeeded && first.Authority.Snapshot.State == DurableRelaunchPermitState.Blocked &&
                    first.Authority.Snapshot.SchemaVersion == WatchdogJournalPolicy.CurrentSchemaVersion,
                    "legacy ambiguous record was not durably migrated to Blocked");
                var durableBlockedBytes = File.ReadAllBytes(relaunchPath);
                var second = DurableRelaunchAuthorityFactory.TryOpenExisting(dir, session);
                Assert(second.Succeeded && second.Authority.Snapshot.State == DurableRelaunchPermitState.Blocked &&
                    durableBlockedBytes.SequenceEqual(File.ReadAllBytes(relaunchPath)), "blocked migration changed on reopen");
                var replay = second.Authority.RegisterFailureAndDecide(OperationWithBudget(session, 10));
                Assert(replay.Durable && replay.Blocked &&
                     replay.CommitStatus == DurableAuthorityCommitStatus.ExistingBlocked &&
                     (replay.Receipt == null || !replay.FailureRegistered) &&
                    durableBlockedBytes.SequenceEqual(File.ReadAllBytes(relaunchPath)), "migrated blocked state reopened or advanced");
            }
            finally { TryDelete(dir); }
        }

        private static void ProductionJournalAuthorityParallel()
        {
            var dir = TempDirectory();
            WatchdogJournalStore journal = null;
            try
            {
                var session = Guid.NewGuid().ToString("N");
                var created = DurableRelaunchAuthorityFactory.TryCreatePristine(dir, session, 7, 700, 1000);
                Assert(created.Succeeded, "parallel production bootstrap failed");
                journal = new WatchdogJournalStore(dir, session, "parallel", new WatchdogJournalPolicy(), 7, 700);
                var proofPath = created.BootstrapReceipt.ProofPath;
                var proofBefore = File.ReadAllBytes(proofPath);
                var authority = created.Authority;
                var errors = 0;
                var writer = Task.Run(() =>
                {
                    for (var i = 0; i < 300; i++)
                    {
                        if (!journal.TryPublishSnapshotSynchronously("{\"SchemaVersion\":4,\"SessionId\":\"" + session + "\",\"WriterSequence\":" + i.ToString(CultureInfo.InvariantCulture) + "}"))
                            Interlocked.Increment(ref errors);
                    }
                });
                var registrar = Task.Run(() =>
                {
                    for (var i = 0; i < 300; i++)
                    {
                        var operation = OperationWithBudget(session, 1000);
                        var result = authority.RegisterFailureAndDecide(operation);
                        if (!result.Durable || result.Record == null || result.Receipt == null ||
                            result.Record.AuthorityRevision != i + 1 ||
                            result.Record.ConsecutiveFailures != i + 1 ||
                            result.Receipt.DecisionSequence != i + 1)
                            Interlocked.Increment(ref errors);
                        if ((i % 37) == 0)
                        {
                            var reopened = DurableRelaunchAuthorityFactory.TryOpenExisting(dir, session);
                            if (!reopened.Succeeded) Interlocked.Increment(ref errors);
                        }
                    }
                });
                Task.WaitAll(writer, registrar);
                var reopenedFinal = DurableRelaunchAuthorityFactory.TryOpenExisting(dir, session);
                Assert(errors == 0 && reopenedFinal.Succeeded &&
                       reopenedFinal.Authority.Snapshot.AuthorityRevision == 300 &&
                       reopenedFinal.Authority.Snapshot.ConsecutiveFailures == 300 &&
                       proofBefore.SequenceEqual(File.ReadAllBytes(proofPath)),
                    "parallel journal/authority lost a write or mutated immutable proof: errors=" + errors);
            }
            finally
            {
                try { journal?.Dispose(); } catch { }
                TryDelete(dir);
            }
        }

        private static void ProductionMigrationMatrix()
        {
            foreach (var schema in new[] { 2, 3 })
            {
                foreach (var ambiguous in new[] { false, true })
                {
                    var dir = TempDirectory();
                    try
                    {
                        var session = Guid.NewGuid().ToString("N");
                        var created = DurableRelaunchAuthorityFactory.TryCreatePristine(dir, session, 7, 700, 10);
                        Assert(created.Succeeded, "schema" + schema + " migration fixture failed");
                        var raw = ambiguous
                            ? "{\"SchemaVersion\":" + schema + ",\"SessionId\":\"" + session + "\",\"State\":2,\"Generation\":1,\"PermitId\":\"legacy\",\"PermitNonce\":\"legacy\"}"
                            : "{\"SchemaVersion\":" + schema + ",\"SessionId\":\"" + session + "\",\"State\":0,\"MaximumProcessRelaunches\":10,\"Generation\":0,\"ConsecutiveFailures\":0,\"ProcessId\":0,\"ProcessStartUtcTicks\":0,\"ConnectionGeneration\":0,\"RecoveryAttemptGeneration\":0,\"RecoveryCommitGeneration\":0,\"RunEpoch\":0,\"CircuitOpen\":false}";
                        File.WriteAllText(Path.Combine(dir, "session-" + session + ".relaunch.json"), raw, new UTF8Encoding(false));
                        var opened = DurableRelaunchAuthorityFactory.TryOpenExisting(dir, session);
                        Assert(opened.Succeeded && opened.Authority != null &&
                               opened.Authority.Snapshot.State == (ambiguous ? DurableRelaunchPermitState.Blocked : DurableRelaunchPermitState.None),
                            "schema" + schema + " " + (ambiguous ? "ambiguous" : "pristine") + " migration was not durable");
                    }
                    finally { TryDelete(dir); }
                }
            }

            foreach (var state in Enum.GetValues(typeof(DurableRelaunchPermitState)).Cast<DurableRelaunchPermitState>())
            {
                var dir = TempDirectory();
                try
                {
                    var session = Guid.NewGuid().ToString("N");
                    var created = DurableRelaunchAuthorityFactory.TryCreatePristine(dir, session, 7, 700, 10);
                    Assert(created.Succeeded, "old-v4 fixture failed for " + state);
                    DurableRelaunchAuthorityRecord record;
                    if (state == DurableRelaunchPermitState.None)
                    {
                        record = DurableRelaunchAuthorityV4Validator.CreatePristine(session, 10);
                        record.BootstrapMarker = null;
                    }
                    else
                    {
                        var first = created.Authority.RegisterFailureAndDecide(OperationWithBudget(session, 10));
                        Assert(first.Durable, "old-v4 evidence fixture failed for " + state);
                        record = created.Authority.Snapshot;
                        record.State = state;
                        if (state == DurableRelaunchPermitState.Started || state == DurableRelaunchPermitState.Attached)
                        {
                            record.ProcessId = 42; record.ProcessStartUtcTicks = 4200;
                            record.ConnectionGeneration = Math.Max(1, record.ConnectionGeneration);
                            record.RecoveryAttemptGeneration = Math.Max(1, record.RecoveryAttemptGeneration);
                        }
                        record.RecoveryCommitGeneration = state == DurableRelaunchPermitState.Committed ? 1 : 0;
                        record.CircuitOpen = state == DurableRelaunchPermitState.Blocked || state == DurableRelaunchPermitState.Revoked;
                    }
                    record.SchemaVersion = 4;
                    var json = StripOldV4Identity(DurableRelaunchAuthorityV4Validator.Serialize(record));
                    File.WriteAllText(Path.Combine(dir, "session-" + session + ".relaunch.json"), json, new UTF8Encoding(false));
                    var opened = DurableRelaunchAuthorityFactory.TryOpenExisting(dir, session);
                    Assert(opened.Succeeded && opened.Authority != null,
                        "old-v4 " + state + " did not reach durable current schema");
                    var expected = state == DurableRelaunchPermitState.None ? DurableRelaunchPermitState.None :
                                   state == DurableRelaunchPermitState.Failed
                                       ? state : DurableRelaunchPermitState.Blocked;
                    Assert(opened.Authority.Snapshot.State == expected,
                        "old-v4 " + state + " migrated to " + opened.Authority.Snapshot.State + " not " + expected);
                }
                finally { TryDelete(dir); }
            }

            var mismatchDir = TempDirectory();
            try
            {
                var session = Guid.NewGuid().ToString("N");
                var created = DurableRelaunchAuthorityFactory.TryCreatePristine(mismatchDir, session, 7, 700, 10);
                Assert(created.Succeeded, "migration mismatch fixture failed");
                File.WriteAllText(Path.Combine(mismatchDir, "session-" + session + ".relaunch.json"),
                    "{\"SchemaVersion\":3,\"SessionId\":\"" + Guid.NewGuid().ToString("N") + "\",\"State\":0}", new UTF8Encoding(false));
                var mismatch = DurableRelaunchAuthorityFactory.TryOpenExisting(mismatchDir, session);
                Assert(mismatch.Blocked && mismatch.Unproven && mismatch.Authority == null,
                    "migration session mismatch was not Unproven");
            }
            finally { TryDelete(mismatchDir); }
        }

        private static string StripOldV4Identity(string json)
        {
            return json.Replace("\"RecordKind\":\"DurableRelaunchAuthority\",", string.Empty)
                       .Replace("\"RecordFormatRevision\":1,", string.Empty)
                       .Replace("\"RecordFormatRevision\":2,", string.Empty);
        }

        private static void ProductionReconcileMatrix()
        {
            foreach (var pending in new[] { DurableRelaunchPermitState.Approved, DurableRelaunchPermitState.LaunchIntent })
                RunProductionReconcileCase(pending, DurableRelaunchProcessObservation.Unknown, 0);
            foreach (var state in new[] { DurableRelaunchPermitState.Started, DurableRelaunchPermitState.Attached })
            {
                RunProductionReconcileCase(state, DurableRelaunchProcessObservation.Alive, 1);
                RunProductionReconcileCase(state, DurableRelaunchProcessObservation.Dead, 1);
                RunProductionReconcileCase(state, DurableRelaunchProcessObservation.Unknown, 1);
                RunProductionReconcileCase(state, DurableRelaunchProcessObservation.IdentityMismatch, 1);
            }

            // A probe is deliberately raced with a second real authority
            // commit.  Reconcile must reload and retry instead of returning
            // a stale Alive proof for the old revision/identity tuple.
            var dir = TempDirectory();
            try
            {
                var session = Guid.NewGuid().ToString("N");
                var created = DurableRelaunchAuthorityFactory.TryCreatePristine(dir, session, 7, 700, 10);
                var first = created.Authority.RegisterFailureAndDecide(OperationWithBudget(session, 10));
                Assert(first.Durable, "reconcile race evidence setup failed");
                AdvanceAuthorityToState(created.Authority, DurableRelaunchPermitState.Started);
                var racing = DurableRelaunchAuthorityFactory.TryOpenExisting(dir, session);
                var observed = DurableRelaunchAuthorityFactory.TryOpenExisting(dir, session);
                var callbackCount = 0;
                var raced = false;
                long racedRevision = 0;
                var reconciliation = observed.Authority.ReconcileAfterRestart((pid, start) =>
                {
                    callbackCount++;
                    if (!raced)
                    {
                        raced = true;
                        var next = OperationWithBudget(session, 10);
                        var changed = racing.Authority.RegisterFailureAndDecide(next);
                        Assert(changed.Durable, "reconcile race authority commit failed");
                        racedRevision = changed.Record.AuthorityRevision;
                    }
                    return DurableRelaunchProcessObservation.Alive;
                });
                Assert(callbackCount >= 2 && reconciliation.Decision != null &&
                       reconciliation.Decision.Reason == "ProcessAlive" &&
                       racedRevision > 0 &&
                       reconciliation.Decision.Record.AuthorityRevision == racedRevision,
                    "reconcile returned stale proof during concurrent authority commit");
            }
            finally { TryDelete(dir); }
        }

        private static void ProductionReconcileReloadBusy()
        {
            // Approved and LaunchIntent share the same no-probe recovery
            // branch.  The injected admission gate lets construction read
            // the real file once, then makes only the reconciliation reload
            // busy.  This is a production FileStore test; no CAS algorithm
            // is reproduced in the test assembly.
            foreach (var state in new[] { DurableRelaunchPermitState.Approved, DurableRelaunchPermitState.LaunchIntent })
            {
                var dir = TempDirectory();
                try
                {
                    var session = Guid.NewGuid().ToString("N");
                    var created = DurableRelaunchAuthorityFactory.TryCreatePristine(dir, session, 7, 700, 10);
                    Assert(created.Succeeded && created.Authority != null, "reload Busy " + state + " bootstrap failed");
                    var first = created.Authority.RegisterFailureAndDecide(OperationWithBudget(session, 10));
                    Assert(first.Durable && first.Record.State == DurableRelaunchPermitState.Approved,
                        "reload Busy " + state + " evidence setup failed");
                    var record = AdvanceAuthorityToState(created.Authority, state);
                    var path = Path.Combine(dir, "session-" + session + ".relaunch.json");
                    var before = File.ReadAllBytes(path);
                    var beforeRevision = record.AuthorityRevision;
                    var admissions = 0;
                    var authority = OpenProductionAuthorityWithAdmission(
                        dir, session, created.BootstrapReceipt,
                        timeout => Interlocked.Increment(ref admissions) != 2);
                    Assert(!authority.IsBlockedInCurrentProcess && authority.Snapshot.State == state,
                        "reload Busy " + state + " construction was unexpectedly blocked");

                    var result = authority.ReconcileAfterRestart((pid, start) =>
                    {
                        throw new InvalidOperationException("Approved/LaunchIntent must not probe");
                    });
                    AssertBusyReconcile(result, "reload Busy " + state);
                    Assert(!authority.IsBlockedInCurrentProcess && authority.Snapshot.State == state,
                        "reload Busy " + state + " poisoned local circuit");
                    Assert(before.SequenceEqual(File.ReadAllBytes(path)) &&
                           authority.Snapshot.AuthorityRevision == beforeRevision,
                        "reload Busy " + state + " changed durable bytes/revision");

                    var retry = authority.ReconcileAfterRestart(null);
                    Assert(retry.Decision != null && retry.Decision.Durable &&
                           retry.Decision.CommitStatus == DurableAuthorityCommitStatus.CandidateApplied &&
                           retry.Decision.Blocked && retry.Decision.Record != null &&
                           retry.Decision.Record.State == DurableRelaunchPermitState.Blocked &&
                           retry.Decision.Record.AuthorityRevision == beforeRevision + 1,
                        "reload Busy " + state + " did not retry to a durable marker");
                }
                finally { TryDelete(dir); }
            }
        }

        private static void ProductionReconcilePostProbeBusy()
        {
            foreach (var state in new[] { DurableRelaunchPermitState.Started, DurableRelaunchPermitState.Attached })
            {
                var dir = TempDirectory();
                try
                {
                    var session = Guid.NewGuid().ToString("N");
                    var created = DurableRelaunchAuthorityFactory.TryCreatePristine(dir, session, 7, 700, 10);
                    Assert(created.Succeeded && created.Authority != null, "post-probe Busy " + state + " bootstrap failed");
                    var first = created.Authority.RegisterFailureAndDecide(OperationWithBudget(session, 10));
                    Assert(first.Durable, "post-probe Busy " + state + " evidence setup failed");
                    var record = AdvanceAuthorityToState(created.Authority, state);
                    var path = Path.Combine(dir, "session-" + session + ".relaunch.json");
                    var before = File.ReadAllBytes(path);
                    var beforeRevision = record.AuthorityRevision;
                    var admissions = 0;
                    var authority = OpenProductionAuthorityWithAdmission(
                        dir, session, created.BootstrapReceipt,
                        timeout => Interlocked.Increment(ref admissions) != 2);
                    Assert(!authority.IsBlockedInCurrentProcess && authority.Snapshot.State == state,
                        "post-probe Busy " + state + " construction was unexpectedly blocked");

                    var probes = 0;
                    var result = authority.ReconcileAfterRestart((pid, start) =>
                    {
                        probes++;
                        Assert(pid == 42 && start == 4200, "post-probe Busy used the wrong frozen identity");
                        return DurableRelaunchProcessObservation.Alive;
                    });
                    Assert(probes == 1, "post-probe Busy " + state + " did not perform exactly one probe");
                    AssertBusyReconcile(result, "post-probe Busy " + state);
                    Assert(result.Observation == DurableRelaunchProcessObservation.Alive &&
                           !authority.IsBlockedInCurrentProcess && authority.Snapshot.State == state,
                        "post-probe Busy " + state + " changed observation/state");
                    Assert(before.SequenceEqual(File.ReadAllBytes(path)) &&
                           authority.Snapshot.AuthorityRevision == beforeRevision,
                        "post-probe Busy " + state + " changed durable bytes/revision");

                    var retry = authority.ReconcileAfterRestart((pid, start) =>
                    {
                        probes++;
                        return DurableRelaunchProcessObservation.Alive;
                    });
                    Assert(probes == 2 && retry.Decision != null && retry.Decision.Durable &&
                           retry.Decision.Reason == "ProcessAlive" && !retry.Decision.ActionAllowed &&
                           retry.Decision.Record != null && retry.Decision.Record.State == state &&
                           retry.Decision.Record.AuthorityRevision == beforeRevision,
                        "post-probe Busy " + state + " did not retry to the live authority");
                    Assert(before.SequenceEqual(File.ReadAllBytes(path)),
                        "post-probe Busy " + state + " retry changed live authority bytes");
                }
                finally { TryDelete(dir); }
            }
        }

        private static void ProductionReconcileCommitBusy()
        {
            var dir = TempDirectory();
            try
            {
                var session = Guid.NewGuid().ToString("N");
                var created = DurableRelaunchAuthorityFactory.TryCreatePristine(dir, session, 7, 700, 10);
                Assert(created.Succeeded && created.Authority != null, "commit Busy bootstrap failed");
                var first = created.Authority.RegisterFailureAndDecide(OperationWithBudget(session, 10));
                Assert(first.Durable && first.Record.State == DurableRelaunchPermitState.Approved,
                    "commit Busy evidence setup failed");
                var record = created.Authority.Snapshot;
                WriteRelaunchJson(dir, session, record);
                var path = Path.Combine(dir, "session-" + session + ".relaunch.json");
                var before = File.ReadAllBytes(path);
                var beforeRevision = record.AuthorityRevision;
                var admissions = 0;
                var authority = OpenProductionAuthorityWithAdmission(
                    dir, session, created.BootstrapReceipt,
                    timeout => Interlocked.Increment(ref admissions) != 3);
                Assert(!authority.IsBlockedInCurrentProcess && authority.Snapshot.State == DurableRelaunchPermitState.Approved,
                    "commit Busy construction was unexpectedly blocked");

                // Load succeeds (#2), but TryCommit is denied (#3).  This
                // covers the commit-side Busy contract separately from the
                // reload-side contract above.
                var result = authority.ReconcileAfterRestart(null);
                AssertBusyReconcile(result, "commit Busy after successful load");
                Assert(!authority.IsBlockedInCurrentProcess && authority.Snapshot.State == DurableRelaunchPermitState.Approved &&
                       before.SequenceEqual(File.ReadAllBytes(path)) && authority.Snapshot.AuthorityRevision == beforeRevision,
                    "commit Busy changed marker/local circuit/bytes");

                var retry = authority.ReconcileAfterRestart(null);
                Assert(retry.Decision != null && retry.Decision.Durable &&
                       retry.Decision.CommitStatus == DurableAuthorityCommitStatus.CandidateApplied &&
                       retry.Decision.Blocked && retry.Decision.Record != null &&
                       retry.Decision.Record.State == DurableRelaunchPermitState.Blocked &&
                       retry.Decision.Record.AuthorityRevision == beforeRevision + 1,
                    "commit Busy did not retry to a durable marker");
            }
            finally { TryDelete(dir); }
        }

        private static void AssertBusyReconcile(DurableAuthorityReconcileResult result, string label)
        {
            Assert(result != null && result.Decision != null &&
                   result.Decision.CommitStatus == DurableAuthorityCommitStatus.Busy &&
                   !result.Decision.Durable && !result.Decision.ActionAllowed &&
                   !result.Decision.Blocked && !result.Decision.BlockedInCurrentProcess &&
                   result.Decision.Unproven && result.Decision.Record == null &&
                   result.Decision.Receipt == null && !result.OutcomeUnknown &&
                   string.Equals(result.Reason, "AuthorityMutexBusy", StringComparison.Ordinal),
                label + " did not return the typed retryable Busy result: " +
                (result == null || result.Decision == null ? "null" : result.Decision.CommitStatus + "/" + result.Decision.Reason));
        }

        private static DurableRelaunchAuthorityV4 OpenProductionAuthorityWithAdmission(
            string directory,
            string session,
            WatchdogJournalBootstrapReceipt receipt,
            Func<int, bool> waitAdmission)
        {
            var store = new DurableRelaunchAuthorityFileStore(
                directory, session, new WindowsDurableAuthorityFileIo(), waitAdmission);
            var capability = new DurableRelaunchAuthorityBootstrapCapability(receipt, store.CanonicalPath);
            return new DurableRelaunchAuthorityV4(session, store, capability);
        }

        private static void RunProductionReconcileCase(
            DurableRelaunchPermitState state,
            DurableRelaunchProcessObservation observation,
            int expectedProbeCalls)
        {
            var dir = TempDirectory();
            try
            {
                var session = Guid.NewGuid().ToString("N");
                var created = DurableRelaunchAuthorityFactory.TryCreatePristine(dir, session, 7, 700, 10);
                Assert(created.Succeeded, "reconcile " + state + " bootstrap failed");
                var first = created.Authority.RegisterFailureAndDecide(OperationWithBudget(session, 10));
                Assert(first.Durable, "reconcile " + state + " evidence setup failed");
                var record = AdvanceAuthorityToState(created.Authority, state);
                var opened = DurableRelaunchAuthorityFactory.TryOpenExisting(dir, session);
                Assert(opened.Succeeded, "reconcile " + state + " reopen failed: " + opened.Reason);
                var calls = 0;
                var result = opened.Authority.ReconcileAfterRestart((pid, start) =>
                {
                    calls++;
                    return observation;
                });
                Assert(calls == expectedProbeCalls, "reconcile " + state + " probed " + calls + " times");
                Assert(result.Decision != null && !result.ActionAllowed,
                    "reconcile " + state + " exposed an action");
                if (state == DurableRelaunchPermitState.Approved || state == DurableRelaunchPermitState.LaunchIntent)
                {
                    Assert(result.OutcomeUnknown && result.Decision.Blocked &&
                           result.Decision.Record != null && result.Decision.Record.State == DurableRelaunchPermitState.Blocked,
                        "reconcile " + state + " did not durably block OutcomeUnknown");
                }
                else if (observation == DurableRelaunchProcessObservation.Alive)
                {
                    Assert(result.Decision.Reason == "ProcessAlive" && result.Decision.Record.State == state,
                        "reconcile alive " + state + " changed state");
                }
                else if (observation == DurableRelaunchProcessObservation.Dead)
                {
                    Assert(result.Decision.Durable &&
                           (result.Decision.Record.State == DurableRelaunchPermitState.Failed || result.Decision.Record.State == DurableRelaunchPermitState.Blocked),
                        "reconcile dead " + state + " did not persist Failed/Blocked");
                }
                else
                {
                    Assert(result.Decision.Durable && result.Decision.Blocked &&
                           result.Decision.Record.State == DurableRelaunchPermitState.Blocked,
                        "reconcile unknown/PID reuse " + state + " did not persist Blocked");
                }
            }
            finally { TryDelete(dir); }
        }

        private static void WriteRelaunchJson(string dir, string session, DurableRelaunchAuthorityRecord record)
        {
            File.WriteAllText(Path.Combine(dir, "session-" + session + ".relaunch.json"),
                DurableRelaunchAuthorityV4Validator.Serialize(record), new UTF8Encoding(false));
        }

        private static DurableRelaunchAuthorityRecord AdvanceAuthorityToState(
            DurableRelaunchAuthorityV4 authority,
            DurableRelaunchPermitState state)
        {
            Assert(authority != null, "authority fixture missing");
            if (state == DurableRelaunchPermitState.Approved)
                return authority.Snapshot;
            Assert(state == DurableRelaunchPermitState.LaunchIntent ||
                   state == DurableRelaunchPermitState.Started ||
                   state == DurableRelaunchPermitState.Attached,
                "unsupported active fixture state " + state);
            var record = authority.Snapshot;
            var executable = Path.GetFullPath(Process.GetCurrentProcess().MainModule.FileName);
            var intent = new DurableLaunchIntent
            {
                SessionId = record.SessionId,
                SessionNonce = record.LastFailureSessionNonce,
                Generation = record.Generation,
                PermitId = record.PermitId,
                PermitNonce = record.PermitNonce,
                IntentId = Guid.NewGuid().ToString("N"),
                ExecutablePath = executable,
                ExecutableSha256 = new string('A', 64),
                Arguments = "--production-reconcile-fixture",
                WorkingDirectory = Path.GetDirectoryName(executable),
                LaunchOptionsCanonical = DurableLaunchCanonical.RequiredOptionsCanonical
            };
            intent.LaunchSpecSha256 = DurableLaunchCanonical.Sha256(intent);
            var prepared = authority.PrepareLaunchIntent(intent);
            Assert(prepared.Succeeded, "active fixture prepare failed: " + prepared.Reason);
            if (state == DurableRelaunchPermitState.LaunchIntent)
                return authority.Snapshot;
            Assert(authority.ConsumeLaunchIntent(prepared.Capability).Succeeded,
                "active fixture consume failed");
            Assert(authority.CommitStarted(prepared.Capability, 42, 4200).Succeeded,
                "active fixture started failed");
            if (state == DurableRelaunchPermitState.Attached)
                Assert(authority.CommitAttached(prepared.Capability).Succeeded,
                    "active fixture attached failed");
            return authority.Snapshot;
        }

        private static RecoveryFailureOperation Operation(string session, string correlation)
        {
            return new RecoveryFailureOperation
            {
                OperationId = Guid.NewGuid().ToString("N"), SessionId = session, SessionNonce = "0123456789abcdef0123456789abcdef",
                SidecarProcessId = 42, SidecarProcessStartUtcTicks = 4200, ConnectionGeneration = 3, RecoveryAttemptGeneration = 7,
                RequestCorrelationId = correlation, RequestPayloadSha256 = Hash('A'), FailureCode = "DAQUnavailable", FailureFingerprint = "fingerprint-1",
                Permanent = false, DetailCode = "DAQUnavailable", RunId = "run-1", RunEpoch = 1, RecoveryStage = "FirstFreshBatch",
                RecoveryProgressToken = "stage-7", RecoveryProcessSource = "RecoveryProcess", DeviceOrChannelGroup = "EPB10", MaximumProcessRelaunches = 8
            };
        }

        private static string Hash(char c) => new string(c, 64);
        private static string TempDirectory() => Path.Combine(Path.GetTempPath(), "epb-p07-v4-" + Guid.NewGuid().ToString("N"));
        private static string Sha(byte[] bytes) { using (var sha = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToUpperInvariant(); }
        private static void TryDelete(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }
        private static void Run(string name, Action test, ref int passed) { test(); passed++; Console.WriteLine("PASS " + name); }
        private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }

    /// <summary>
    /// Test-only deterministic store.  It deliberately lives in the test
    /// assembly; production strict authority never exposes an in-memory
    /// implementation or accepts a legacy store.
    /// </summary>
    internal sealed class InMemoryDurableRelaunchAuthorityStoreV4 : IDurableRelaunchAuthorityStoreV4
    {
        private readonly object _gate = new object();
        private readonly string _path;
        private string _json;
        private long _replaceCount;

        private InMemoryDurableRelaunchAuthorityStoreV4(string sessionId, DurableRelaunchAuthorityRecord initial)
        {
            if (!DurableRelaunchAuthorityV4.IsCanonicalSession(sessionId)) throw new ArgumentException("SessionId must be GUID N.", nameof(sessionId));
            SessionId = sessionId;
            _path = "memory://authority/" + sessionId + ".relaunch.json";
            if (initial != null) _json = DurableRelaunchAuthorityV4Validator.Serialize(initial.Clone());
        }

        internal string SessionId { get; }
        public string CanonicalPath => _path;
        internal bool FailNextCommit { get; set; }
        internal bool FailAllCommits { get; set; }
        internal bool FailNextReplace { get; set; }
        internal bool FailMarkerCommit { get; set; }
        internal long ReplaceCount { get { lock (_gate) return _replaceCount; } }
        internal string RawJson { get { lock (_gate) return _json; } }

        public DurableAuthorityStoreReadResult Load(string expectedSessionId)
        {
            lock (_gate)
            {
                if (!string.Equals(expectedSessionId, SessionId, StringComparison.Ordinal))
                    return new DurableAuthorityStoreReadResult { Blocked = true, Unproven = true, FailureKind = DurableAuthorityFailureKind.SessionMismatch, Reason = "SessionMismatch" };
                if (string.IsNullOrEmpty(_json))
                    return new DurableAuthorityStoreReadResult { Blocked = true, Unproven = true, FailureKind = DurableAuthorityFailureKind.Missing, Reason = "AuthorityMissing" };
                var validation = DurableRelaunchAuthorityV4Validator.ValidateAndMigrate(_json, SessionId);
                if (validation == null || validation.Record == null || validation.Blocked)
                    return new DurableAuthorityStoreReadResult { Exists = true, Blocked = true, Unproven = !validation?.Proven ?? true, FailureKind = validation?.FailureKind ?? DurableAuthorityFailureKind.Corrupt, Reason = validation?.Reason ?? "AuthorityInvalid" };
                var bytes = new UTF8Encoding(false).GetBytes(_json);
                return new DurableAuthorityStoreReadResult { Exists = true, Record = validation.Record.Clone(), Revision = validation.Record.AuthorityRevision, Sha256 = DurableRelaunchAuthorityFileStore.Sha256Hex(bytes), Reason = validation.Reason };
            }
        }

        public DurableAuthorityStoreCommitResult TryCommit(DurableRelaunchAuthorityRecord candidate, long expectedRevision, string expectedSha256, bool blockedMarker)
        {
            lock (_gate)
            {
                if (FailAllCommits || FailNextCommit || (blockedMarker && FailMarkerCommit) || FailNextReplace)
                {
                    FailNextCommit = false; FailNextReplace = false;
                    return new DurableAuthorityStoreCommitResult { Status = DurableAuthorityCommitStatus.WriteFailed, Reason = "InjectedCommitFailure" };
                }
                var current = Load(SessionId);
                if (current == null || current.Record == null) return new DurableAuthorityStoreCommitResult { Status = DurableAuthorityCommitStatus.DurableBlocked, Reason = "CurrentMissing" };
                if (current.Record.State == DurableRelaunchPermitState.Blocked || current.Record.State == DurableRelaunchPermitState.Revoked || current.Record.CircuitOpen)
                    return new DurableAuthorityStoreCommitResult { Status = DurableAuthorityCommitStatus.DurableBlocked, Record = current.Record.Clone(), Sha256 = current.Sha256, Revision = current.Revision, Reason = "CircuitOpen" };
                if (current.Revision != expectedRevision || !string.Equals(current.Sha256 ?? string.Empty, expectedSha256 ?? string.Empty, StringComparison.Ordinal))
                    return new DurableAuthorityStoreCommitResult { Status = DurableAuthorityCommitStatus.Conflict, Reason = "ExpectedRevisionOrShaMismatch" };
                var next = candidate.Clone();
                next.AuthorityRevision = expectedRevision + 1;
                next.SchemaVersion = WatchdogJournalPolicy.CurrentSchemaVersion; next.RecordKind = DurableRelaunchAuthorityV4Validator.RequiredRecordKind; next.RecordFormatRevision = DurableRelaunchAuthorityV4Validator.RequiredFormatRevision;
                _json = DurableRelaunchAuthorityV4Validator.Serialize(next); _replaceCount++;
                var after = Load(SessionId);
                if (after == null || after.Record == null || after.Blocked || after.Unproven)
                    return new DurableAuthorityStoreCommitResult { Status = DurableAuthorityCommitStatus.ReadFailed, Reason = "ReadBackValidationFailed" };
                return new DurableAuthorityStoreCommitResult { Status = blockedMarker ? DurableAuthorityCommitStatus.CandidateApplied : DurableAuthorityCommitStatus.Committed, Record = after.Record.Clone(), Sha256 = after.Sha256, Revision = after.Revision, Reason = blockedMarker ? "BlockedMarkerApplied" : "Committed" };
            }
        }

        internal static InMemoryDurableRelaunchAuthorityStoreV4 CreatePristine(string sessionId, int budget = 8)
        {
            var record = DurableRelaunchAuthorityV4Validator.CreatePristine(sessionId, budget);
            record.BootstrapPrimaryPath = "memory://primary/" + sessionId + ".json";
            record.BootstrapPrimarySha256 = new string('A', 64);
            record.BootstrapProofPath = "memory://primary/" + sessionId + ".bootstrap.json";
            record.BootstrapProofSha256 = new string('B', 64);
            record.BootstrapProofMarker = "schema4-authority-bootstrap-proof-v1";
            record.BootstrapMaximumProcessRelaunches = budget;
            return new InMemoryDurableRelaunchAuthorityStoreV4(sessionId, record);
        }

        internal DurableAuthorityOpenResult OpenAuthority()
        {
            lock (_gate)
            {
                var read = Load(SessionId);
                if (read == null || read.Record == null || read.Blocked || read.Unproven)
                    return new DurableAuthorityOpenResult { Blocked = true, Unproven = true, Store = read, Reason = read?.Reason ?? "AuthorityOpenBlocked" };
                var primary = new WatchdogJournalBootstrapReceipt("memory://primary", "memory://primary/" + SessionId + ".json", SessionId, new string('A', 64), 4, 0, "schema4-session-bootstrap-v1", 1, 1,
                    "memory://primary/" + SessionId + ".json", new string('A', 64),
                    "memory://primary/" + SessionId + ".bootstrap.json", new string('B', 64),
                    read.Record.MaximumProcessRelaunches, "schema4-authority-bootstrap-proof-v1");
                var cap = new DurableRelaunchAuthorityBootstrapCapability(primary, CanonicalPath);
                return new DurableAuthorityOpenResult { Succeeded = true, Authority = new DurableRelaunchAuthorityV4(SessionId, this, cap), Store = read, Reason = "Opened" };
            }
        }

        internal void SeedJson(string json) { lock (_gate) _json = json; }
    }
}
