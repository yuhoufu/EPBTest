# 现场基线源码摘录：1185a09（不含工作区未完成修复）

## Controller/EpbManager.cs

```text
1977:         private void UnmarkHydraulicParticipant(int channel)
1978:         {
1979:             _formalParticipantLeases.TryGetValue(channel, out var formalLease);
1980:             lock (GetHydraulicParticipantGate(channel))
1981:             {
1982:                 _hydraulicParticipants.TryRemove(channel, out _);
1983:                 _firstEligibleFormalSlotByChannel.TryRemove(channel, out _);
1984:             }
1985:             RequestFormalParticipantRetirement(
1986:                 formalLease,
1987:                 "HydraulicParticipantRemoved");
1988:         }
```
```text
2006:         private bool TryUnmarkHydraulicParticipant(
2007:             int channel,
2008:             long expectedVersion,
2009:             string reason,
2010:             bool logRejected = true)
2011:         {
2012:             FormalBatchParticipantLease formalLease = null;
2013:             lock (GetHydraulicParticipantGate(channel))
2014:             {
2015:                 var participantExists = _hydraulicParticipants.ContainsKey(channel);
2016:                 _hydraulicParticipantVersions.TryGetValue(channel, out var currentVersion);
2017:                 if (!RecoveryEpochGuard.CanApplyParticipantCleanup(
2018:                         participantExists,
2019:                         expectedVersion,
2020:                         currentVersion))
2021:                 {
2022:                     if (logRejected)
2023:                         _log?.Warn(
2024:                             $"EPB[{channel}] 拒绝迟到的液压参与状态清理。" +
2025:                             $"ExpectedVersion={expectedVersion} CurrentVersion={currentVersion} " +
2026:                             $"Reason={reason}",
2027:                             "液压协调");
2028:                     return false;
2029:                 }
2030: 
2031:                 _hydraulicParticipants.TryRemove(channel, out _);
2032:                 _firstEligibleFormalSlotByChannel.TryRemove(channel, out _);
2033:                 _formalParticipantLeases.TryGetValue(channel, out formalLease);
2034:                 RequestFormalParticipantRetirement(
2035:                     formalLease,
2036:                     reason);
2037:                 return true;
2038:             }
2039:         }
2040: 
```
```text
2075:         private void RequestFormalParticipantRetirement(
2076:             FormalBatchParticipantLease lease,
2077:             string reason)
2078:         {
2079:             if (lease == null) return;
2080:             if (!_formalBatchSlots.RequestRetirement(lease, reason)) return;
2081:             lock (_channelExecutionGates[lease.Channel - 1])
2082:                 _channelExecutionFence.RevokeIfCurrent(lease.ExecutionPermit);
2083:             ObserveSafetyTask(
2084:                 CompleteFormalParticipantRetirementFenceAsync(
2085:                     lease,
2086:                     reason),
2087:                 "FormalParticipantRetirementFence",
2088:                 lease.Channel);
2089:         }
```
```text
4307:         private bool IsChannelExecutionPermitCurrent(
4308:             int channel,
4309:             ChannelExecutionPermit permit)
4310:         {
4311:             return permit.Channel == channel &&
4312:                    _channelExecutionFence.IsCurrent(permit) &&
4313:                    permit.RunEpoch == Interlocked.Read(ref _runEpoch) &&
4314:                    IsChannelEnabled(channel) &&
4315:                    !RequiresProcessRestart;
4316:         }
```
```text
6830:                             context.CutoffParticipantVersions.TryGetValue(
6831:                                 channel,
6832:                                 out var expectedParticipantVersion);
6833:                             if (!TryUnmarkHydraulicParticipant(
6834:                                     channel,
6835:                                     expectedParticipantVersion,
6836:                                     $"DaqCutoff:{context.Device}:RecoveryEpoch={context.RecoveryEpoch}",
6837:                                     logRejected: false))
6838:                                 cutoffSafetyDiagnostics.Add(
6839:                                     $"DAQ截止拒绝迟到液压撤权 EPB={channel} " +
6840:                                     $"ExpectedVersion={expectedParticipantVersion}");
6841:                         }
6842: 
6843:                         var scheduledReleases = new List<KeyValuePair<int, Task>>(affected.Length);
6844:                         foreach (var channel in affected)
6845:                         {
6846:                             var capturedChannel = channel;
6847:                             try
6848:                             {
```
```text
7459:                     if (!IsCurrentRecovery(context)) return;
7460: 
7461:                     context.ValidationDetail = "PowerEnableThenMechanicalRelease";
7462:                     await ExecuteDaqRecoveryRejoinPrerequisitesAsync(
7463:                             powerRecoveryChannels,
7464:                             _powerSupply == null
7465:                                 ? null
7466:                                 : (channels, ct) => _powerSupply.PrepareAndEnableAsync(channels, ct),
7467:                             rejoinChannels.Length == 0
7468:                                 ? null
7469:                                 : (_, ct) => EnsureMotorReleasedBeforeFormalRejoinAsync(
7470:                                     rejoinChannels,
7471:                                     rejoinPlan,
7472:                                     $"DaqRecovery:{device}",
7473:                                     ct),
7474:                             context.Cancellation.Token)
7475:                         .ConfigureAwait(false);
7476:                     if (!IsCurrentRecovery(context)) return;
7477:                     foreach (var rejoinedChannel in rejoinChannels)
```
```text
8240:             bool countAsFailure = true)
8241:         {
8242:             if (!IsCurrentRecovery(context)) return;
8243:             if (Interlocked.CompareExchange(ref context.MaintenanceScheduled, 1, 0) != 0) return;
8244: 
8245:             var failureCount = countAsFailure
8246:                 ? context.FailureBackoff.RecordFailure()
8247:                 : context.FailureBackoff.Current;
8248:             if (countAsFailure && failureCount >= SoftwareRecoveryEscalationAttempts)
8249:             {
8250:                 // DAQ software maintenance is bounded per incident.  Reaching
8251:                 // the budget is a single SafeIdle/Terminal outcome, not an
8252:                 // instruction to recycle the batch or relaunch the process.
8253:                 CompleteCancelledRecovery(
8254:                     context,
8255:                     $"DaqSelfMaintenanceBudgetExhausted Device={context.Device}; " +
8256:                     $"Failure={failureCount}; Code={code}; SafeIdleOnly");
8257:                 return;
8258:             }
8259:             var delayMs = countAsFailure
8260:                 ? GetDaqSelfMaintenanceDelayMs(failureCount)
8261:                 : 0;
```

## Controller/EpbManager.PauseResume.cs

```text
2322:         private async Task EnsureMotorReleasedBeforeFormalRejoinAsync(
2323:             IEnumerable<int> channels,
2324:             ElectricalStaggerPlan staggerPlan,
2325:             string reason,
2326:             CancellationToken token)
2327:         {
2328:             var selected = (channels ?? Array.Empty<int>())
2329:                 .Distinct()
2330:                 .OrderBy(channel => channel)
2331:                 .ToArray();
2332:             if (selected.Length == 0) return;
2333:             var rejoinPermits = selected.ToDictionary(
2334:                 channel => channel,
2335:                 channel => _channelExecutionFence.Capture(channel));
2336:             if (rejoinPermits.Any(pair =>
2337:                     !IsChannelExecutionPermitCurrent(pair.Key, pair.Value)))
2338:                 throw new InvalidOperationException(
2339:                     $"FormalRejoinRejected StaleExecutionPermit " +
2340:                     $"Channels=[{string.Join(",", selected)}]");
2341: 
2342:             await InvokeAfterCycleExecutionQuiescenceAsync(
2343:                     selected,
2344:                     WaitForPreviousCycleExecutionAsync,
2345:                     ct => EnsureMotorReleasedBeforeFormalRejoinCoreAsync(
2346:                         selected,
2347:                         staggerPlan,
2348:                         reason,
2349:                         ct),
2350:                     "MotorReleaseBeforeFormalRejoin",
2351:                     token)
2352:                 .ConfigureAwait(false);
2353:         }
2354: 
```

## Controller/ChannelExecutionFence.cs

```text
86:         internal bool RevokeIfCurrent(ChannelExecutionPermit permit)
87:         {
88:             if (permit.Channel < 1 || permit.Channel > 12) return false;
89:             var entry = _entries.GetOrAdd(permit.Channel, _ => new Entry());
90:             lock (entry.Gate)
91:             {
92:                 if (!entry.Authorized || entry.Generation != permit.Generation ||
93:                     entry.RunEpoch != permit.RunEpoch) return false;
94:                 entry.Generation++;
95:                 entry.Authorized = false;
96:                 try { entry.Revocation.Cancel(); } catch { }
97:                 return true;
98:             }
99:         }
100: 
101:         internal bool IsCurrent(ChannelExecutionPermit permit)
102:         {
103:             if (!permit.Authorized || permit.Channel < 1 || permit.Channel > 12) return false;
104:             var current = Capture(permit.Channel);
105:             return current.Authorized &&
106:                    current.RunEpoch == permit.RunEpoch &&
107:                    current.Generation == permit.Generation;
108:         }
109: 
```

## Controller/ChannelRuntimeState.cs

```text
540:                 .ToArray();
541:             var candidate = next.Clone();
542: 
543:             return _states.AddOrUpdate(
544:                     candidate.Channel,
545:                     _ => CloneWithRevision(candidate, 1),
546:                     (_, current) => IsLatchedStop(current.State) &&
547:                                     !allowTerminalReset &&
548:                                     !(allowSystemFaultReset &&
549:                                       current.State == ChannelRuntimeState.SystemFault)
550:                         ? current
551:                         : CloneWithRevision(candidate, current.Revision + 1))
552:                 .Clone();
```

## Controller/RecoveryIncidentCoordinator.cs

```text
480:                 if (body == null)
481:                     throw new InvalidOperationException("Recovery worker factory returned null body.");
482: 
483:                 var scheduled = _port.Schedule(() => RunWorkerAsync(created, body));
484:                 if (scheduled == null)
485:                     throw new InvalidOperationException("Recovery worker scheduler returned null Task.");
486:                 created.SetWorkerTask(scheduled);
487: 
488:                 if (!_port.Bind(created.TaskLease, scheduled))
489:                     throw new InvalidOperationException("Recovery worker binding failed.");
490: 
491:                 publishRecovering(entry.Contract.Clone());
492:                 if (_port.IsRecoveringPublished != null &&
493:                     !_port.IsRecoveringPublished(entry.Contract.Clone()))
494:                     throw new InvalidOperationException(
495:                         "Recovering publication did not commit the complete owner contract.");
496: 
497:                 _port.Observe?.Invoke(created, scheduled);
498:                 incident = created;
499:                 CompleteBegin(entry, BeginResult.Created, created);
500:                 return BeginResult.Created;
501:             }
502:             catch (Exception registrationError)
503:             {
504:                 ReportFaultOutsideGate("registration", registrationError);
505:                 AbortEntryOutsideGate(
506:                     entry,
507:                     "RecoveryContractRegistrationFailed",
508:                     registrationError);
509:                 CompleteBegin(entry, BeginResult.Rejected, null);
510:                 incident = null;
```
```text
527:         private async Task RunWorkerAsync(Incident incident, Func<Task> body)
528:         {
529:             try
530:             {
531:                 await incident.StartSignal.ConfigureAwait(false);
532:                 if (incident.IsAborting ||
533:                     incident.TerminalPublished != 0 ||
534:                     incident.TerminalStateCommitted)
535:                     return;
536:                 incident.TaskLease.ReportProgress("WorkerStarted");
537:                 var bodyTask = body();
538:                 if (bodyTask == null)
539:                     throw new InvalidOperationException(
540:                         $"Recovery worker body returned null Task. Incident={incident.Contract.IncidentId:N}");
541:                 await bodyTask.ConfigureAwait(false);
```

## MTTFTest.Watchdog/SupervisorServiceHost.cs

```text
1234:             if (!string.Equals(authority.Receipt.SafetyAgentExecutablePath,
1235:                     executable, StringComparison.OrdinalIgnoreCase) ||
1236:                 !string.Equals(authority.Receipt.SafetyAgentExecutableSha256,
1237:                     request.ExecutableSha256, StringComparison.Ordinal))
1238:                 throw new InvalidDataException(
1239:                     "SupervisorSafetyAuthorityExecutableHashMismatch");
```

## MTTFTest.Watchdog/SupervisorSafetyAgentLaunchClient.cs

```text
75:                     AuthorityReceiptRevision = token.ReceiptRevision,
76:                     AuthorityReceiptCanonicalSha256 =
77:                         token.ReceiptCanonicalSha256,
78:                     ExecutablePath = executablePath,
79:                     ExecutableSha256 = SupervisorProtocol.ComputeSha256(
80:                         executablePath),
81:                     Arguments = arguments ?? string.Empty,
82:                     ArgumentsSha256 = SupervisorProtocol.ComputeTextSha256(
83:                         arguments ?? string.Empty),
```

## Watchdog.Protocol/SupervisorProtocol.cs

```text
65:         public static string ComputeSha256(string path)
66:         {
67:             using (var sha = SHA256.Create())
68:             using (var stream = new FileStream(
69:                        path,
70:                        FileMode.Open,
71:                        FileAccess.Read,
72:                        FileShare.ReadWrite | FileShare.Delete))
73:                 return BitConverter.ToString(sha.ComputeHash(stream))
74:                     .Replace("-", string.Empty)
75:                     .ToUpperInvariant();
76:         }
77: 
```

## Watchdog.Protocol/DurableJsonFileStore.cs

```text
755:         public static string ComputeSha256(byte[] bytes)
756:         {
757:             using (var sha = SHA256.Create())
758:                 return BitConverter.ToString(sha.ComputeHash(bytes ?? Array.Empty<byte>()))
759:                     .Replace("-", string.Empty).ToLowerInvariant();
760:         }
```

## MTTFTest.Watchdog/WatchdogHost.cs

```text
2390: 
2391:         private async Task MonitorAsync(CancellationToken token)
2392:         {
2393:             while (!token.IsCancellationRequested)
2394:             {
2395:                 try
2396:                 {
2397:                     await Task.Delay(250, token).ConfigureAwait(false);
2398:                     if (ObserveSafetyHandoffProgress()) continue;
2399:                     if (ShouldProbeCircuitHalfOpen(IsRecoveryBlocked(), _attached))
2400:                     {
2401:                         TryBeginAutomaticCircuitHalfOpen();
2402:                         continue;
2403:                     }
2404:                     ObserveApplicationExitIntent();
2405:                     if (ObserveDurableSafetyState()) continue;
2406:                     if (!_attached) continue;
2407:                     if (WatchdogRecoveryCommitMarker.TryRead(
```
```text
6757:         private bool ObserveSafetyHandoffProgress()
6758:         {
6759:             WatchdogSafetyHandoffReceipt handoff;
6760:             if (!WatchdogSafetyHandoffReceiptStore.TryRead(
6761:                     _args.JournalDirectory,
6762:                     _args.SessionId,
6763:                     out handoff) || handoff == null)
6764:                 return false;
6765: 
6766:             WatchdogClosingTombstone closing;
6767:             WatchdogClosingTombstoneStore.TryRead(
6768:                 _args.JournalDirectory,
6769:                 _args.SessionId,
6770:                 out closing);
6771:             if (closing != null &&
6772:                 (closing.SessionGeneration != handoff.SessionGeneration ||
6773:                  closing.SessionLease != handoff.SessionLease))
6774:                 return false;
6775: 
6776:             var started = BeginSafetyHandoff(handoff);
6777:             return started || !handoff.IsTerminal;
6778:         }
6779: 
```
```text
7093:         private bool BeginSafetyHandoff(WatchdogSafetyHandoffReceipt receipt)
7094:         {
7095:             if (receipt == null || receipt.IsTerminal ||
7096:                 !receipt.IsValidFor(_args.SessionId))
7097:                 return false;
7098:             if (Interlocked.CompareExchange(ref _safetyHandoffStarted, 1, 0) != 0)
7099:                 return false;
7100: 
7101:             var authorityToken = GetSafetyAuthorityToken(
7102:                 receipt.HandoffId,
7103:                 receipt.RelaunchPermitGeneration,
7104:                 receipt.RelaunchPermitId);
7105:             if (authorityToken != null)
7106:             {
7107:                 WatchdogSafetyHandoffReceipt authoritativeReceipt;
7108:                 string authorityReadFailure;
7109:                 if (!TryReadExactSafetyHandoff(
7110:                         receipt.HandoffId,
7111:                         receipt.Nonce,
7112:                         authorityToken,
7113:                         out authoritativeReceipt,
7114:                         out authorityReadFailure))
7115:                 {
7116:                     RecordRepeatedObservation(
7117:                         "SupervisorSafetyAuthorityReadFailed",
7118:                         receipt.HandoffId,
7119:                         authorityReadFailure);
7120:                     _unattendedAlarmSink.Publish(
7121:                         "P0",
7122:                         "SupervisorSafetyAuthorityReadFailed",
7123:                         authorityReadFailure,
7124:                         RecoveryFailureDomain.EvidenceBinding,
7125:                         receipt);
7126:                     Interlocked.Exchange(ref _safetyHandoffStarted, 0);
7127:                     return true;
7128:                 }
7129: 
7130:                 receipt = authoritativeReceipt;
7131:                 if (receipt.IsTerminal)
7132:                 {
7133:                     RecordRepeatedObservation(
7134:                         "SupervisorSafetyAuthorityTerminalObserved",
7135:                         receipt.HandoffId + "|" +
7136:                         receipt.Revision.ToString(CultureInfo.InvariantCulture),
7137:                         $"State={receipt.State};Revision={receipt.Revision};" +
7138:                         "Project mirror ignored in favor of Supervisor authority.");
7139:                     Interlocked.Exchange(ref _safetyHandoffStarted, 0);
7140:                     return true;
7141:                 }
7142:             }
7143: 
7144:             var snapshot = WatchdogSafetyConfigSnapshotStore.Validate(
```

## MTTfTest/WatchdogRuntime.cs

```text
2730:                 var mainSha256 = DurableJsonFileStore.ComputeSha256(
2731:                     File.ReadAllBytes(mainExecutablePath));
2732:                 var safetyAgentSha256 = DurableJsonFileStore.ComputeSha256(
2733:                     File.ReadAllBytes(safetyAgentPath));
```
```text
3446:                 relaunchDisposition = typedExit.RelaunchDisposition;
3447:             }
3448:             if (hasPrevious &&
3449:                 (!IsExactClosingTombstoneIdentity(context, previous) ||
3450:                  !IsCompatibleClosingTransaction(
3451:                      previous,
3452:                      stopSafetyTransactionId,
3453:                      stopRunId,
3454:                      stopRunEpoch,
3455:                      stopSafetyBoundaryGeneration)))
3456:                 return RejectSessionCloseFence(
3457:                     receipt,
3458:                     context,
3459:                     "DurableCloseFenceIdentityChanged",
3460:                     previous);
3461: 
```
```text
3772:         private static bool IsCompatibleClosingTransaction(
3773:             WatchdogClosingTombstone tombstone,
3774:             Guid stopSafetyTransactionId,
3775:             Guid stopRunId,
3776:             long stopRunEpoch,
3777:             long stopSafetyBoundaryGeneration)
3778:         {
3779:             if (tombstone == null) return false;
3780:             if (stopSafetyTransactionId != Guid.Empty &&
3781:                 !string.Equals(
3782:                     tombstone.StopSafetyTransactionId,
3783:                     stopSafetyTransactionId.ToString("N"),
3784:                     StringComparison.OrdinalIgnoreCase))
3785:                 return false;
3786:             if (stopRunId != Guid.Empty &&
3787:                 !string.Equals(
3788:                     tombstone.StopRunId,
3789:                     stopRunId.ToString("N"),
3790:                     StringComparison.OrdinalIgnoreCase))
3791:                 return false;
3792:             if (stopRunEpoch > 0 && tombstone.StopRunEpoch != stopRunEpoch)
3793:                 return false;
3794:             if (stopSafetyBoundaryGeneration > 0 &&
3795:                 tombstone.StopSafetyBoundaryGeneration != stopSafetyBoundaryGeneration)
3796:                 return false;
3797:             return true;
3798:         }
3799: 
3800:         private static RuntimeSessionCloseFenceReceipt RejectSessionCloseFence(
3801:             RuntimeSessionCloseFenceReceipt receipt,
```

## MTTfTest/FrmEpbMainMonitor.cs

```text
3011: 
3012:         private async void BeginMonitorCloseSequence()
3013:         {
3014:             try
3015:             {
3016:                 var completed = await PrepareAndFinalizeMonitorCloseAsync(
3017:                     closeAfterPreparation: true);
3018:                 if (!completed)
3019:                 {
3020:                     HideCloseOverlay();
3021:                     _isClosing = false;
3022:                     Interlocked.Exchange(ref _formClosedFlag, 0);
3023:                     Interlocked.Exchange(ref _closingReentry, 0);
3024:                     SetMonitorLifecycle(
3025:                         _epb?.IsBatchSessionActive == true
3026:                             ? EpbMonitorLifecycle.Running
3027:                             : EpbMonitorLifecycle.Idle);
3028:                 }
3029:             }
3030:             catch (Exception ex)
3031:             {
3032:                 logger?.Error("实时监控窗口关闭收尾异常，已转入诊断并允许重试：" + ex, "EPB");
3033:                 HideCloseOverlay();
3034:                 _isClosing = false;
3035:                 Interlocked.Exchange(ref _formClosedFlag, 0);
3036:                 Interlocked.Exchange(ref _closingReentry, 0);
3037:                 SetMonitorLifecycle(
3038:                     _epb?.IsBatchSessionActive == true
3039:                         ? EpbMonitorLifecycle.Running
3040:                         : EpbMonitorLifecycle.Idle);
3041:             }
```
```text
3478:             _preparedCloseSafety = safety;
3479:             _preparedCloseContext = _preparedCloseContext ??
3480:                                     WatchdogRuntime.CaptureTransportSnapshot()?.Context;
3481:             if (closeAfterPreparation)
3482:             {
3483:                 var closeReceipt = await AuthorizeApplicationExitAfterPreparationAsync()
3484:                     .ConfigureAwait(true);
3485:                 if (closeReceipt?.CanExit != true)
3486:                 {
3487:                     LogInfo("关闭授权尚未建立：需要完整 Watchdog 终态或已接受的耐久安全交接。");
3488:                     HideCloseOverlay();
3489:                     _isClosing = false;
3490:                     Interlocked.Exchange(ref _closingReentry, 0);
3491:                     return false;
3492:                 }
3493:                 (MdiParent as Main_Frm)?.CacheApplicationCloseReceipt(closeReceipt);
3494:                 Interlocked.Exchange(ref _closingReentry, 3);
3495:                 if (!IsDisposed && !Disposing && IsHandleCreated)
3496:                     BeginInvoke((Action)Close);
```

## MTTfTest/FrmEpbMainMonitor.CloseOverlay.cs

```text
17: 
18:         private async void ScheduleCloseOverlay()
19:         {
20:             if (Interlocked.Exchange(ref _closeOverlayScheduled, 1) != 0) return;
21:             await Task.Delay(250).ConfigureAwait(true);
22:             if (Volatile.Read(ref _closingReentry) == 1 && !IsDisposed)
23:                 ShowCloseOverlay("正在建立安全关闭事务…");
24:         }
```
```text
94:                             ? "正在保存 Raw、SQLite 和最新圈数据…"
95:                             : progress.Stage < StopSafetyStage.Completed
96:                                 ? "正在确认逻辑静默与释放 Watchdog…"
97:                                 : "安全终态已完成，正在关闭窗口…";
98:             ShowCloseOverlay(text);
99:             if (_closeOverlayProgress != null)
100:             {
101:                 var value = progress.Stage < StopSafetyStage.StartPowerDisable
102:                     ? 20
103:                     : progress.Stage < StopSafetyStage.ReleaseHydraulics
104:                         ? 40
105:                         : progress.Stage < StopSafetyStage.ClosePersistenceBoundary
106:                             ? 60
107:                             : progress.Stage < StopSafetyStage.VerifyLogicalQuiescence
108:                                 ? 75
```

## MTTfTest/Main_Frm.WatchdogUi.cs

```text
186:         internal bool HandleWatchdogMainFormClosing(FormClosingEventArgs e)
187:         {
188:             ArmApplicationExitDeadlineOnce(
189:                 "MainFormClosing",
190:                 RuntimeShutdownIntent.ApplicationExit,
191:                 null);
192:             if (HasApplicationCloseReceipt)
193:             {
194:                 Interlocked.Exchange(ref _watchdogAllowClose, 1);
195:                 return false;
196:             }
197:             if (Volatile.Read(ref _watchdogAllowClose) != 0) return false;
198:             var composite = WatchdogRuntime.CaptureTransportSnapshot();
```
