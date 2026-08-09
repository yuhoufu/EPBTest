#!/usr/bin/env python3
"""Self-contained regression tests for validate_epb_field_gate.py."""

from __future__ import annotations

import json
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from datetime import datetime, timedelta
from pathlib import Path


SCRIPT = Path(__file__).with_name("validate_epb_field_gate.py")


class FieldGateValidatorTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory(prefix="epb-field-gate-test-")
        self.root = Path(self.temp.name)
        log = self.root / "log"
        log.mkdir()
        sha = "a" * 64
        commit = "b" * 40
        (log / "run.log").write_text(
            "2026-08-08 10:00:00.000\tINFO\t启动\tProductVersion=V2.12.0.27\n"
            f"2026-08-08 10:00:00.000\tINFO\tFIELD\tFieldMetric SESSION Phase=Start RunId=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa Channels=4,9 Closed=False Detail=BatchFormal ProductVersion=V2.12.0.27 AssemblyVersion=2.12.0.27 ProcessId=1234 ExecutablePath=D:\\EPBTest\\MTTFTest.exe ExeSha256={sha} ConfigSha256={sha} GitCommit={commit} GitDirty=False BuildUtc=2026-08-08T00:00:00Z AsyncLogDropped=0 PackageVerified=True PackageCode=Verified PackageFiles=96\n"
            "2026-08-08 10:00:00.100\tINFO\tFIELD\tFieldMetric STATE RunId=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa Device=Dev1 Channel=4 State=Running Reason=Running Revision=1 CorrelationId=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa RunEpoch=1 Enabled=True Formal=True Timer=True Runner=True Energized=False\n"
            "2026-08-08 10:00:00.100\tINFO\tFIELD\tFieldMetric STATE RunId=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa Device=Dev2 Channel=9 State=Running Reason=Running Revision=1 CorrelationId=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa RunEpoch=1 Enabled=True Formal=True Timer=True Runner=True Energized=False\n"
            "2026-08-08 10:00:01.000\tINFO\tFIELD\tFieldMetric DAQ Phase=Running Device=Dev1 ControlDepth=0 ControlOldestMs=0.0 ControlProcessMs=1.0 SubscriberMaxMs=1.0 ProcessingDepth=0 ProcessingCapacity=64 ProcessingOldestMs=0.0 ProcessingInFlight=0 RawDepth=0 RawCapacity=64 RawInFlight=0 PersistenceState=Recovered PersistenceDepth=1 PersistenceOldestMs=5.0 CallbackAgeMs=10.0 ControlProcessedAgeMs=10.0 Produced=100 Processed=100 Allocated=100 Accepted=100 Observed=100 Published=100 RawTransferred=100 Persisted=99 TerminallyHandled=99 SuppressBoundary=0 SuppressThrough=0 Suppressed=0 SuppressedCumulative=0 SuppressedFirst=0 SuppressedLast=0 SuppressedRanges=0 FirstPermanentGap=0 PendingProcessingGap=0 PendingRawGap=0 Discontinuities=0 DurabilityBlocked=False Discarded=0 OverCapacityDropped=0 AsyncLogDropped=0\n"
            "2026-08-08 10:00:01.000\tINFO\tFIELD\tFieldMetric DAQ Phase=Running Device=Dev2 ControlDepth=0 ControlOldestMs=0.0 ControlProcessMs=1.0 SubscriberMaxMs=1.0 ProcessingDepth=0 ProcessingCapacity=64 ProcessingOldestMs=0.0 ProcessingInFlight=0 RawDepth=0 RawCapacity=64 RawInFlight=0 PersistenceState=Recovered PersistenceDepth=1 PersistenceOldestMs=5.0 CallbackAgeMs=10.0 ControlProcessedAgeMs=10.0 Produced=100 Processed=100 Allocated=100 Accepted=100 Observed=100 Published=100 RawTransferred=100 Persisted=99 TerminallyHandled=99 SuppressBoundary=0 SuppressThrough=0 Suppressed=0 SuppressedCumulative=0 SuppressedFirst=0 SuppressedLast=0 SuppressedRanges=0 FirstPermanentGap=0 PendingProcessingGap=0 PendingRawGap=0 Discontinuities=0 DurabilityBlocked=False Discarded=0 OverCapacityDropped=0 AsyncLogDropped=0\n"
            "2026-08-08 10:00:02.000\tINFO\tFIELD\tFieldMetric DO_OFF Device=Dev1 Channel=4 CommandId=a Result=True Late=False QueueWaitMs=1 NIWriteMs=5 WorkerMs=6 TotalMs=7\n"
            "2026-08-08 10:00:02.000\tINFO\tFIELD\tFieldMetric DO_OFF Device=Dev2 Channel=9 CommandId=b Result=True Late=False QueueWaitMs=1 NIWriteMs=5 WorkerMs=6 TotalMs=7\n"
            "2026-08-08 10:00:10.000\tINFO\tFIELD\tFieldMetric UI DelayP95Ms=5 DelayMaxMs=10 FlushP95Ms=2 FlushMaxMs=3 AppendMaxMs=1 TrimMaxMs=1 ScrollMaxMs=1 RenderedBatches=2 RenderedLines=4 Pending=0 Dropped=0 FilePending=0 FileDropped=0\n"
            "2026-08-08 10:00:10.100\tINFO\tFIELD\tFieldMetric CYCLE RunId=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa Channel=4 Cycle=1 Phase=Formal Peak=15.1 Target=15.0 Floor=14.2 Ceiling=15.8 Qualified=True Result=Success\n"
            "2026-08-08 10:00:10.200\tINFO\tFIELD\tFieldMetric CYCLE RunId=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa Channel=9 Cycle=1 Phase=Formal Peak=15.2 Target=15.0 Floor=14.2 Ceiling=15.8 Qualified=True Result=Success\n"
            "2026-08-08 10:00:11.000\tINFO\tHOST\tHostRuntime PID=1 Bitness=32 ProcessCpu=8.0% SystemCpu=30.0% OtherCpu=22.0% WorkingSet=200MiB\n"
            "2026-08-08 10:59:58.000\tINFO\tFIELD\tFieldMetric STATE RunId=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa Device=Dev1 Channel=4 State=ManualStopped Reason=StopAll Revision=2 CorrelationId=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa RunEpoch=1 Enabled=True Formal=False Timer=False Runner=False Energized=False\n"
            "2026-08-08 10:59:58.000\tINFO\tFIELD\tFieldMetric STATE RunId=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa Device=Dev2 Channel=9 State=ManualStopped Reason=StopAll Revision=2 CorrelationId=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa RunEpoch=1 Enabled=True Formal=False Timer=False Runner=False Energized=False\n"
            "2026-08-08 10:59:59.000\tINFO\tFIELD\tFieldMetric STOP_PERSISTENCE Device=Dev1 RawDrained=True Boundary=100 FinalBoundary=100 BoundaryStable=True Published=100 Persisted=100 Depth=0 State=Recovered RequireRecovered=True DurabilityBlocked=False Discarded=0 OverCapacityDropped=0 Closed=True\n"
            "2026-08-08 10:59:59.000\tINFO\tFIELD\tFieldMetric STOP_PERSISTENCE Device=Dev2 RawDrained=True Boundary=100 FinalBoundary=100 BoundaryStable=True Published=100 Persisted=100 Depth=0 State=Recovered RequireRecovered=True DurabilityBlocked=False Discarded=0 OverCapacityDropped=0 Closed=True\n"
            "2026-08-08 11:00:00.000\tINFO\tFIELD\tFieldMetric SESSION Phase=Stop RunId=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa Channels=4,9 Closed=True Detail=ManualUi ProductVersion=V2.12.0.27 ExeSha256=unused ConfigSha256=unused GitCommit=unused GitDirty=False BuildUtc=unused AsyncLogDropped=0\n"
            "2026-08-08 11:00:00.001\tINFO\t停止\tNormal Stop\n",
            encoding="utf-8",
        )
        heartbeat_lines: list[str] = []
        started = datetime(2026, 8, 8, 10, 0, 0)
        daq_payload = (
            "ControlDepth=0 ControlOldestMs=0.0 ControlProcessMs=1.0 "
            "SubscriberMaxMs=1.0 PersistenceState=Recovered PersistenceDepth=1 "
            "PersistenceOldestMs=5.0 CallbackAgeMs=10.0 ControlProcessedAgeMs=10.0 "
            "ProcessingDepth=0 ProcessingCapacity=64 ProcessingOldestMs=0.0 "
            "ProcessingInFlight=0 RawDepth=0 RawCapacity=64 RawInFlight=0 "
            "Produced=100 Processed=100 Allocated=100 Accepted=100 Observed=100 "
            "Published=100 RawTransferred=100 Persisted=99 TerminallyHandled=99 "
            "SuppressBoundary=0 SuppressThrough=0 Suppressed=0 SuppressedCumulative=0 "
            "SuppressedFirst=0 SuppressedLast=0 SuppressedRanges=0 FirstPermanentGap=0 "
            "PendingProcessingGap=0 PendingRawGap=0 Discontinuities=0 "
            "DurabilityBlocked=False Discarded=0 OverCapacityDropped=0 "
            "AsyncLogDropped=0 "
            "Evidence=FixtureHeartbeat"
        )
        for second in range(3, 3600, 2):
            timestamp = (started + timedelta(seconds=second)).strftime("%Y-%m-%d %H:%M:%S.%f")[:-3]
            for device in ("Dev1", "Dev2"):
                heartbeat_lines.append(
                    f"{timestamp}\tINFO\tFIELD\tFieldMetric DAQ Phase=Running "
                    f"Device={device} {daq_payload}\n"
                )
        ui_payload = (
            "DelayP95Ms=5 DelayMaxMs=10 FlushP95Ms=2 FlushMaxMs=3 "
            "AppendMaxMs=1 TrimMaxMs=1 ScrollMaxMs=1 RenderedBatches=2 "
            "RenderedLines=4 Pending=0 Dropped=0 FilePending=0 FileDropped=0 "
            "Evidence=FixtureHeartbeat"
        )
        for second in range(20, 3600, 10):
            timestamp = (started + timedelta(seconds=second)).strftime("%Y-%m-%d %H:%M:%S.%f")[:-3]
            heartbeat_lines.append(
                f"{timestamp}\tINFO\tFIELD\tFieldMetric UI {ui_payload}\n"
            )
        for second in (value for value in range(1, 3600) if value != 11):
            timestamp = (started + timedelta(seconds=second)).strftime("%Y-%m-%d %H:%M:%S.%f")[:-3]
            heartbeat_lines.append(
                f"{timestamp}\tINFO\tHOST\tHostRuntime PID=1 Bitness=32 "
                "ProcessCpu=8.0% SystemCpu=30.0% OtherCpu=22.0% "
                "WorkingSet=200MiB Evidence=FixtureHeartbeat\n"
            )
        with (log / "run.log").open("a", encoding="utf-8") as stream:
            stream.writelines(heartbeat_lines)
        for name in ("warning.log", "error.log", "ui-info.log"):
            (log / name).write_text("", encoding="utf-8")
        connection = sqlite3.connect(self.root / "index.db")
        try:
            connection.execute(
                "CREATE TABLE epb_cycles (epb_id INTEGER, cycle_number INTEGER, status TEXT)"
            )
            connection.execute("INSERT INTO epb_cycles VALUES (4, 1, 'completed')")
            connection.execute("INSERT INTO epb_cycles VALUES (9, 1, 'completed')")
            connection.commit()
        finally:
            connection.close()

    def tearDown(self) -> None:
        self.temp.cleanup()

    def run_gate(
        self,
        extra_arguments: list[str] | None = None,
    ) -> tuple[subprocess.CompletedProcess[str], dict]:
        output_json = self.root / "gate.json"
        arguments = [
            sys.executable,
            str(SCRIPT),
            str(self.root),
            "--minimum-hours",
            "1",
            "--performance-gates",
            "required",
            "--expected-exe-sha256",
            "a" * 64,
            "--expected-config-sha256",
            "a" * 64,
            "--expected-git-commit",
            "b" * 40,
            "--expected-build-utc",
            "2026-08-08T00:00:00Z",
        ]
        arguments.extend(extra_arguments or [])
        arguments.extend([
            "--output-md",
            str(self.root / "gate.md"),
            "--output-json",
            str(output_json),
        ])
        process = subprocess.run(
            arguments,
            text=True,
            capture_output=True,
            check=False,
        )
        return process, json.loads(output_json.read_text(encoding="utf-8-sig"))

    def test_clean_sealed_project_passes(self) -> None:
        process, result = self.run_gate()
        self.assertEqual(0, process.returncode, process.stderr + process.stdout)
        self.assertEqual("PASS", result["status"])
        self.assertEqual("Staged", result["acceptance_stage"])
        self.assertFalse(result["production_release_approved"])
        report = (self.root / "gate.md").read_text(encoding="utf-8-sig")
        self.assertIn("阶段性现场验证：PASS", report)
        self.assertIn("阶段性结果不得作为最终生产放行", report)

    def test_final_production_stage_rejects_smoke_thresholds_before_validation(self) -> None:
        process = subprocess.run(
            [
                sys.executable,
                str(SCRIPT),
                str(self.root),
                "--acceptance-stage", "FinalProduction",
                "--minimum-hours", "2",
                "--minimum-formal-cycles-per-channel", "1",
            ],
            text=True,
            capture_output=True,
            check=False,
        )
        self.assertEqual(2, process.returncode)
        self.assertIn("FinalProduction要求--minimum-hours>=72", process.stderr)

    def test_final_production_stage_rejects_partial_scans(self) -> None:
        process = subprocess.run(
            [
                sys.executable,
                str(SCRIPT),
                str(self.root),
                "--acceptance-stage", "FinalProduction",
                "--minimum-hours", "72",
                "--minimum-formal-cycles-per-channel", "100000",
                "--log-scan", "quick",
            ],
            text=True,
            capture_output=True,
            check=False,
        )
        self.assertEqual(2, process.returncode)
        self.assertIn("FinalProduction要求log/artifact/database全部使用full扫描", process.stderr)

    def test_formal_release_identity_mismatch_fails(self) -> None:
        run_log = self.root / "log" / "run.log"
        text = run_log.read_text(encoding="utf-8")
        text = text.replace("ExeSha256=" + "a" * 64, "ExeSha256=" + "c" * 64, 1)
        run_log.write_text(text, encoding="utf-8")
        process, result = self.run_gate()
        self.assertEqual(2, process.returncode)
        failures = {item["name"] for item in result["checks"] if not item["passed"]}
        self.assertIn("运行身份与已校验正式候选完全一致", failures)

    def test_formal_gate_rejects_partial_scan_modes(self) -> None:
        process, result = self.run_gate([
            "--log-scan", "quick",
            "--artifact-scan", "none",
            "--database-scan", "none",
        ])
        self.assertEqual(2, process.returncode)
        failures = {item["name"] for item in result["checks"] if not item["passed"]}
        self.assertIn("正式放行使用完整日志扫描", failures)
        self.assertIn("正式放行使用完整事故证据扫描", failures)
        self.assertIn("正式放行使用完整数据库扫描", failures)

    def test_ui_single_flush_over_200ms_fails(self) -> None:
        run_log = self.root / "log" / "run.log"
        text = run_log.read_text(encoding="utf-8")
        text = text.replace("FlushMaxMs=3", "FlushMaxMs=250", 1)
        run_log.write_text(text, encoding="utf-8")
        process, result = self.run_gate()
        self.assertEqual(2, process.returncode)
        failures = {item["name"] for item in result["checks"] if not item["passed"]}
        self.assertIn("UI单次刷新及分阶段操作均<200ms", failures)

    def test_formal_session_missing_process_identity_fails(self) -> None:
        run_log = self.root / "log" / "run.log"
        text = run_log.read_text(encoding="utf-8")
        text = text.replace("ProcessId=1234 ", "", 1)
        run_log.write_text(text, encoding="utf-8")
        process, result = self.run_gate()
        self.assertEqual(2, process.returncode)
        failures = {item["name"] for item in result["checks"] if not item["passed"]}
        self.assertIn("正式会话构建身份完整且GitDirty=false", failures)

    def test_formal_session_with_unverified_package_fails(self) -> None:
        run_log = self.root / "log" / "run.log"
        text = run_log.read_text(encoding="utf-8")
        text = text.replace(
            "PackageVerified=True PackageCode=Verified PackageFiles=96",
            "PackageVerified=False PackageCode=PackageFileHashMismatch PackageFiles=0",
            1,
        )
        run_log.write_text(text, encoding="utf-8")
        process, result = self.run_gate()
        self.assertEqual(2, process.returncode)
        failures = {item["name"] for item in result["checks"] if not item["passed"]}
        self.assertIn("正式会话从完整已批准发布包启动", failures)

    def test_incident_phase_without_build_identity_fails(self) -> None:
        phase = self.root / "IncidentSnapshots" / "incident-a" / "00-trigger"
        phase.mkdir(parents=True)
        (phase / "incident.json").write_text(
            json.dumps({"correlationId": "c" * 32}),
            encoding="utf-8",
        )
        process, result = self.run_gate()
        self.assertEqual(2, process.returncode)
        failures = {item["name"] for item in result["checks"] if not item["passed"]}
        self.assertIn("每个事故阶段构建身份完整", failures)

    def test_incident_phases_with_complete_build_identity_pass(self) -> None:
        identity = {
            "productVersion": "V2.12.0.27",
            "assemblyVersion": "2.12.0.27",
            "executablePath": r"D:\EPBTest\MTTFTest.exe",
            "executableSha256": "a" * 64,
            "configSha256": "d" * 64,
            "releaseConfigSha256": "a" * 64,
            "processBitness": 32,
            "processId": 1234,
            "gitCommit": "b" * 40,
            "gitDirty": "False",
            "buildUtc": "2026-08-08T00:00:00Z",
        }
        for phase_name in ("00-trigger", "90-terminal"):
            phase = self.root / "IncidentSnapshots" / "incident-a" / phase_name
            phase.mkdir(parents=True)
            (phase / "incident.json").write_text(
                json.dumps({"correlationId": "c" * 32}),
                encoding="utf-8",
            )
            (phase / "build-identity.json").write_text(
                json.dumps(identity),
                encoding="utf-8",
            )
        process, result = self.run_gate()
        self.assertEqual(0, process.returncode, process.stderr + process.stdout)
        self.assertEqual("PASS", result["status"])

    def test_redline_and_running_cycle_fail(self) -> None:
        (self.root / "log" / "error.log").write_text(
            "2026-08-08 10:30:00.000\tERROR\t写盘\t无法访问已关闭的访问器\n",
            encoding="utf-8",
        )
        connection = sqlite3.connect(self.root / "index.db")
        try:
            connection.execute("INSERT INTO epb_cycles VALUES (5, 2, 'running')")
            connection.commit()
        finally:
            connection.close()
        process, result = self.run_gate()
        self.assertEqual(2, process.returncode)
        self.assertEqual("FAIL", result["status"])
        failures = {item["name"] for item in result["checks"] if not item["passed"]}
        self.assertIn("红线：已关闭访问器=0", failures)
        self.assertIn("停止后无running圈", failures)

    def test_empty_hard_alarm_cycle_directory_fails(self) -> None:
        snapshot = self.root / "AlarmSnapshots" / "20260808_231726-EPB08"
        (snapshot / "EPB08_ALARM").mkdir(parents=True)
        (snapshot / "snapshot-manifest.json").write_text(
            json.dumps({
                "AlarmChannel": 8,
                "AlarmCycle": 17787,
                "RequestedCycles": 10,
                "ExportedCycles": 0,
                "ExportedCycleEvidenceChannels": [],
                "SkippedCycleEvidenceChannels": [8],
            }),
            encoding="utf-8",
        )
        process, result = self.run_gate()
        self.assertEqual(2, process.returncode)
        failures = {item["name"] for item in result["checks"] if not item["passed"]}
        self.assertIn("硬报警最近圈证据完整", failures)

    def test_warning_scalar_queue_drop_is_redline(self) -> None:
        (self.root / "log" / "warning.log").write_text(
            "2026-08-08 10:30:00.000\tWARN\t落盘\t"
            "WarningScalarQueueDropped=1 Capacity=4096\n",
            encoding="utf-8",
        )
        process, result = self.run_gate()
        self.assertEqual(2, process.returncode)
        failures = {item["name"] for item in result["checks"] if not item["passed"]}
        self.assertIn("红线：软预警标量证据丢弃=0", failures)

    def test_ui_file_queue_drop_is_redline(self) -> None:
        run_log = self.root / "log" / "run.log"
        text = run_log.read_text(encoding="utf-8")
        text = text.replace("FilePending=0 FileDropped=0", "FilePending=512 FileDropped=1")
        run_log.write_text(text, encoding="utf-8")
        process, result = self.run_gate()
        self.assertEqual(2, process.returncode)
        failures = {item["name"] for item in result["checks"] if not item["passed"]}
        self.assertIn("UI文件队列FileDropped=0", failures)

    def test_foreign_run_cycle_is_redline(self) -> None:
        run_log = self.root / "log" / "run.log"
        text = run_log.read_text(encoding="utf-8")
        text = text.replace(
            "FieldMetric CYCLE RunId=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa Channel=9",
            "FieldMetric CYCLE RunId=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb Channel=9",
            1,
        )
        run_log.write_text(text, encoding="utf-8")
        process, result = self.run_gate()
        self.assertEqual(2, process.returncode)
        failures = {item["name"] for item in result["checks"] if not item["passed"]}
        self.assertIn("正式圈证据全部属于当前RunId", failures)

    def test_second_unsealed_run_inside_older_window_blocks_fallback(self) -> None:
        run_log = self.root / "log" / "run.log"
        text = run_log.read_text(encoding="utf-8")
        second = (
            "2026-08-08 10:30:00.000\tINFO\tFIELD\t"
            "FieldMetric SESSION Phase=Start RunId=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb Channels=4,9 Closed=False "
            "Detail=BatchFormal ProductVersion=V2.12.0.27 ExeSha256=" + "a" * 64 +
            " ConfigSha256=" + "a" * 64 + " GitCommit=" + "b" * 40 +
            " GitDirty=False BuildUtc=2026-08-08T00:30:00Z\n"
        )
        text = text.replace(
            "2026-08-08 10:59:58.000\tINFO\tFIELD",
            second + "2026-08-08 10:59:58.000\tINFO\tFIELD",
            1,
        )
        run_log.write_text(text, encoding="utf-8")
        process, result = self.run_gate()
        self.assertEqual(2, process.returncode)
        failures = {item["name"] for item in result["checks"] if not item["passed"]}
        self.assertIn("连续运行会话有唯一Start和终态", failures)

    def test_latest_unsealed_run_cannot_fall_back_to_older_session(self) -> None:
        run_log = self.root / "log" / "run.log"
        text = run_log.read_text(encoding="utf-8")
        latest = (
            "2026-08-08 11:30:00.000\tINFO\tFIELD\t"
            "FieldMetric SESSION Phase=Start RunId=latest-unsealed Channels=4,9 Closed=False "
            "Detail=BatchFormal ProductVersion=V2.12.0.27 ExeSha256=" + "a" * 64 +
            " ConfigSha256=" + "a" * 64 + " GitCommit=" + "b" * 40 +
            " GitDirty=False BuildUtc=2026-08-08T01:30:00Z\n"
        )
        run_log.write_text(text + latest, encoding="utf-8")
        process, result = self.run_gate()
        self.assertEqual(2, process.returncode)
        self.assertIsNone(result["metrics"]["selected_run_id"])
        failures = {item["name"] for item in result["checks"] if not item["passed"]}
        self.assertIn("连续运行会话有唯一Start和终态", failures)

    def test_duplicate_terminal_for_same_run_is_redline(self) -> None:
        run_log = self.root / "log" / "run.log"
        text = run_log.read_text(encoding="utf-8")
        duplicate = (
            "2026-08-08 11:00:00.500\tINFO\tFIELD\t"
            "FieldMetric SESSION Phase=Complete RunId=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa Channels=4,9 Closed=True "
            "Detail=Duplicate ProductVersion=V2.12.0.27 ExeSha256=unused "
            "ConfigSha256=unused GitCommit=unused GitDirty=False BuildUtc=unused\n"
        )
        run_log.write_text(text + duplicate, encoding="utf-8")
        process, result = self.run_gate()
        self.assertEqual(2, process.returncode)
        failures = {item["name"] for item in result["checks"] if not item["passed"]}
        self.assertIn("同一RunId只有一个会话终态", failures)

    def test_recovery_storm_is_redline(self) -> None:
        run_log = self.root / "log" / "run.log"
        text = run_log.read_text(encoding="utf-8")
        events = "".join(
            f"2026-08-08 10:0{minute}:00.000\tINFO\tFIELD\t"
            f"FieldMetric STATE RunId=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa Device=Dev1 Channel=4 State=Recovering "
            f"Reason=DaqRecovery Revision={minute + 2} CorrelationId={minute + 1:032x} "
            "RunEpoch=1 Enabled=True Formal=True Timer=False Runner=True Energized=False\n"
            f"2026-08-08 10:0{minute}:01.000\tINFO\tFIELD\t"
            f"FieldMetric STATE RunId=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa Device=Dev1 Channel=4 State=Running "
            f"Reason=Recovered Revision={minute + 3} CorrelationId={minute + 1:032x} "
            "RunEpoch=1 Enabled=True Formal=True Timer=True Runner=True Energized=False\n"
            for minute in range(1, 5)
        )
        text = text.replace(
            "2026-08-08 10:59:58.000\tINFO\tFIELD",
            events + "2026-08-08 10:59:58.000\tINFO\tFIELD",
            1,
        )
        run_log.write_text(text, encoding="utf-8")
        process, result = self.run_gate()
        self.assertEqual(2, process.returncode)
        failures = {item["name"] for item in result["checks"] if not item["passed"]}
        self.assertIn("自维护无风暴（每设备10分钟根事故≤3）", failures)

    def test_unresolved_recovery_at_terminal_fails(self) -> None:
        run_log = self.root / "log" / "run.log"
        text = run_log.read_text(encoding="utf-8")
        stuck = (
            "2026-08-08 10:59:58.500\tINFO\tFIELD\t"
            "FieldMetric STATE RunId=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa Device=Dev1 Channel=4 State=Recovering "
            "Reason=RecoveryStillPending Revision=99 CorrelationId=cccccccccccccccccccccccccccccccc "
            "RunEpoch=1 Enabled=True Formal=True Timer=False Runner=True Energized=False\n"
        )
        text = text.replace(
            "2026-08-08 10:59:59.000\tINFO\tFIELD",
            stuck + "2026-08-08 10:59:59.000\tINFO\tFIELD",
            1,
        )
        run_log.write_text(text, encoding="utf-8")
        process, result = self.run_gate()
        self.assertEqual(2, process.returncode)
        failures = {item["name"] for item in result["checks"] if not item["passed"]}
        self.assertIn("会话终态无通道滞留自维护", failures)

    def test_recovery_without_root_correlation_is_redline(self) -> None:
        run_log = self.root / "log" / "run.log"
        text = run_log.read_text(encoding="utf-8")
        events = (
            "2026-08-08 10:30:00.000\tINFO\tFIELD\t"
            "FieldMetric STATE RunId=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa Device=Dev1 Channel=4 State=Recovering "
            "Reason=DaqRecovery Revision=3 CorrelationId=00000000-0000-0000-0000-000000000000 "
            "RunEpoch=1 Enabled=True Formal=True Timer=False Runner=True Energized=False\n"
            "2026-08-08 10:30:01.000\tINFO\tFIELD\t"
            "FieldMetric STATE RunId=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa Device=Dev1 Channel=4 State=Running "
            "Reason=Recovered Revision=4 CorrelationId=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa "
            "RunEpoch=1 Enabled=True Formal=True Timer=True Runner=True Energized=False\n"
        )
        text = text.replace(
            "2026-08-08 10:59:58.000\tINFO\tFIELD",
            events + "2026-08-08 10:59:58.000\tINFO\tFIELD",
            1,
        )
        run_log.write_text(text, encoding="utf-8")
        process, result = self.run_gate()
        self.assertEqual(2, process.returncode)
        self.assertEqual(1, result["metrics"]["recovery_uncorrelated_transition_count"])
        failures = {item["name"] for item in result["checks"] if not item["passed"]}
        self.assertIn("自维护状态必须携带非空根事故关联号", failures)

    def test_performance_limit_and_stop_boundary_fail(self) -> None:
        run_log = self.root / "log" / "run.log"
        text = run_log.read_text(encoding="utf-8")
        text = text.replace("SubscriberMaxMs=1.0", "SubscriberMaxMs=25.0", 1)
        text = text.replace(
            "Device=Dev2 RawDrained=True Boundary=100 FinalBoundary=100 BoundaryStable=True Published=100 Persisted=100 Depth=0 State=Recovered RequireRecovered=True DurabilityBlocked=False Discarded=0 OverCapacityDropped=0 Closed=True",
            "Device=Dev2 RawDrained=False Boundary=100 FinalBoundary=101 BoundaryStable=False Published=99 Persisted=99 Depth=1 State=Failed RequireRecovered=True DurabilityBlocked=True Discarded=1 OverCapacityDropped=1 Closed=False",
        )
        run_log.write_text(text, encoding="utf-8")
        process, result = self.run_gate()
        self.assertEqual(2, process.returncode)
        failures = {item["name"] for item in result["checks"] if not item["passed"]}
        self.assertIn("控制订阅者P99<5ms且最大<20ms", failures)
        self.assertIn("停止Raw发布与持久化边界闭合", failures)

    def test_missing_daq_pipeline_field_fails(self) -> None:
        run_log = self.root / "log" / "run.log"
        text = run_log.read_text(encoding="utf-8")
        text = text.replace("RawTransferred=100 ", "", 1)
        run_log.write_text(text, encoding="utf-8")
        process, result = self.run_gate()
        self.assertEqual(2, process.returncode)
        failures = {item["name"] for item in result["checks"] if not item["passed"]}
        self.assertIn("DAQ管线指标字段完整且数值合法", failures)

    def test_daq_depth_equal_to_capacity_fails(self) -> None:
        run_log = self.root / "log" / "run.log"
        text = run_log.read_text(encoding="utf-8")
        text = text.replace(
            "ProcessingDepth=0 ProcessingCapacity=64",
            "ProcessingDepth=64 ProcessingCapacity=64",
            1,
        )
        run_log.write_text(text, encoding="utf-8")
        process, result = self.run_gate()
        self.assertEqual(2, process.returncode)
        failures = {item["name"] for item in result["checks"] if not item["passed"]}
        self.assertIn("DAQ处理与Raw队列深度严格低于容量", failures)

    def test_daq_watermark_regression_fails(self) -> None:
        run_log = self.root / "log" / "run.log"
        lines = run_log.read_text(encoding="utf-8").splitlines(keepends=True)
        for index, line in enumerate(lines):
            if line.startswith("2026-08-08 10:30:01.000") and "Device=Dev1" in line:
                lines[index] = line.replace("Allocated=100", "Allocated=90", 1)
                break
        run_log.write_text("".join(lines), encoding="utf-8")
        process, result = self.run_gate()
        self.assertEqual(2, process.returncode)
        failures = {item["name"] for item in result["checks"] if not item["passed"]}
        self.assertIn("DAQ耐久水位按设备非递减", failures)

    def test_observed_below_accepted_fails(self) -> None:
        run_log = self.root / "log" / "run.log"
        text = run_log.read_text(encoding="utf-8")
        text = text.replace("Accepted=100 Observed=100", "Accepted=100 Observed=99", 1)
        run_log.write_text(text, encoding="utf-8")
        process, result = self.run_gate()
        self.assertEqual(2, process.returncode)
        failures = {item["name"] for item in result["checks"] if not item["passed"]}
        self.assertIn("DAQ Observed始终覆盖Allocated和Accepted", failures)

    def test_suppression_ledger_cannot_fake_physical_persistence(self) -> None:
        run_log = self.root / "log" / "run.log"
        text = run_log.read_text(encoding="utf-8")
        text = text.replace(
            "Persisted=99 TerminallyHandled=99 SuppressBoundary=0 SuppressThrough=0 Suppressed=0 "
            "SuppressedCumulative=0 SuppressedFirst=0 SuppressedLast=0 SuppressedRanges=0",
            "Persisted=100 TerminallyHandled=99 SuppressBoundary=99 SuppressThrough=100 Suppressed=1 "
            "SuppressedCumulative=0 SuppressedFirst=100 SuppressedLast=100 SuppressedRanges=0",
            1,
        )
        run_log.write_text(text, encoding="utf-8")
        process, result = self.run_gate()
        self.assertEqual(2, process.returncode)
        failures = {item["name"] for item in result["checks"] if not item["passed"]}
        self.assertIn("DAQ显式排除账本与物理持久化水位一致", failures)

    def test_permanent_or_final_pending_gap_fails(self) -> None:
        run_log = self.root / "log" / "run.log"
        lines = run_log.read_text(encoding="utf-8").splitlines(keepends=True)
        first_daq_changed = False
        for index, line in enumerate(lines):
            if not first_daq_changed and "FieldMetric DAQ" in line:
                lines[index] = line.replace("FirstPermanentGap=0", "FirstPermanentGap=88", 1)
                first_daq_changed = True
            if line.startswith("2026-08-08 10:59:59.000") and "Device=Dev2" in line and "FieldMetric DAQ" in line:
                lines[index] = lines[index].replace("PendingRawGap=0", "PendingRawGap=101", 1)
        run_log.write_text("".join(lines), encoding="utf-8")
        process, result = self.run_gate()
        self.assertEqual(2, process.returncode)
        failures = {item["name"] for item in result["checks"] if not item["passed"]}
        self.assertIn("DAQ无永久空洞且终态无待决gap", failures)

    def test_async_project_log_drop_in_session_or_daq_fails(self) -> None:
        run_log = self.root / "log" / "run.log"
        text = run_log.read_text(encoding="utf-8")
        text = text.replace("AsyncLogDropped=0", "AsyncLogDropped=1", 1)
        run_log.write_text(text, encoding="utf-8")
        process, result = self.run_gate()
        self.assertEqual(2, process.returncode)
        failures = {item["name"] for item in result["checks"] if not item["passed"]}
        self.assertIn("SESSION与DAQ异步项目日志丢弃始终为0", failures)

    def test_sparse_heartbeat_evidence_fails_all_coverage_gates(self) -> None:
        run_log = self.root / "log" / "run.log"
        lines = [
            line for line in run_log.read_text(encoding="utf-8").splitlines(keepends=True)
            if "Evidence=FixtureHeartbeat" not in line
        ]
        run_log.write_text("".join(lines), encoding="utf-8")
        process, result = self.run_gate()
        self.assertEqual(2, process.returncode)
        failures = {item["name"] for item in result["checks"] if not item["passed"]}
        self.assertIn("DAQ心跳覆盖率与最大缺口达标", failures)
        self.assertIn("UI心跳覆盖率与最大缺口达标", failures)
        self.assertIn("HostRuntime心跳覆盖率与最大缺口达标", failures)

    def test_host_heartbeat_large_gap_fails_even_when_coverage_exceeds_95_percent(self) -> None:
        run_log = self.root / "log" / "run.log"
        lines = [
            line for line in run_log.read_text(encoding="utf-8").splitlines(keepends=True)
            if not (
                line.startswith("2026-08-08 10:20:") and
                "HostRuntime " in line and
                "Evidence=FixtureHeartbeat" in line
            )
        ]
        run_log.write_text("".join(lines), encoding="utf-8")
        process, result = self.run_gate()
        self.assertEqual(2, process.returncode)
        host = result["metrics"]["heartbeat_evidence"]["HostRuntime"]["Host"]
        self.assertGreaterEqual(host["coverage"], 0.95)
        self.assertGreater(host["maximum_gap_seconds"], 5.0)
        failures = {item["name"] for item in result["checks"] if not item["passed"]}
        self.assertIn("HostRuntime心跳覆盖率与最大缺口达标", failures)

    def test_configured_minimum_cycles_is_enforced_in_log_and_sqlite(self) -> None:
        process, result = self.run_gate([
            "--minimum-formal-cycles-per-channel", "2",
        ])
        self.assertEqual(2, process.returncode)
        failures = {item["name"] for item in result["checks"] if not item["passed"]}
        self.assertIn("正式耐久完成圈数达到每通道门槛", failures)
        self.assertIn("SQLite正式完成圈数达到每通道门槛", failures)

    def test_success_with_warning_is_counted_as_completed_but_not_hidden_from_quality(self) -> None:
        run_log = self.root / "log" / "run.log"
        text = run_log.read_text(encoding="utf-8")
        text = text.replace(
            "Channel=9 Cycle=1 Phase=Formal Peak=15.2 Target=15.0 Floor=14.2 Ceiling=15.8 Qualified=True Result=Success",
            "Channel=9 Cycle=1 Phase=Formal Peak=15.2 Target=15.0 Floor=14.2 Ceiling=15.8 Qualified=True Result=SuccessWithWarning",
            1,
        )
        run_log.write_text(text, encoding="utf-8")
        process, result = self.run_gate()
        self.assertEqual(0, process.returncode, process.stderr + process.stdout)
        self.assertEqual(1, result["metrics"]["formal_cycle_quality_by_channel"]["9"]["count"])

    def test_closed_stop_claim_with_mismatched_final_boundary_fails(self) -> None:
        run_log = self.root / "log" / "run.log"
        text = run_log.read_text(encoding="utf-8")
        text = text.replace(
            "Device=Dev2 RawDrained=True Boundary=100 FinalBoundary=100 BoundaryStable=True",
            "Device=Dev2 RawDrained=True Boundary=100 FinalBoundary=101 BoundaryStable=True",
            1,
        )
        run_log.write_text(text, encoding="utf-8")
        process, result = self.run_gate()
        self.assertEqual(2, process.returncode)
        failures = {item["name"] for item in result["checks"] if not item["passed"]}
        self.assertIn("停止Raw发布与持久化边界闭合", failures)

    def test_running_terminal_state_fails_strict_terminal_gate(self) -> None:
        run_log = self.root / "log" / "run.log"
        text = run_log.read_text(encoding="utf-8")
        text = text.replace(
            "State=ManualStopped Reason=StopAll Revision=2",
            "State=Running Reason=StopAll Revision=2",
            1,
        )
        run_log.write_text(text, encoding="utf-8")
        process, result = self.run_gate()
        self.assertEqual(2, process.returncode)
        failures = {item["name"] for item in result["checks"] if not item["passed"]}
        self.assertIn("会话终态通道严格闭合", failures)

    def test_incident_identity_must_match_selected_session(self) -> None:
        phase = self.root / "IncidentSnapshots" / "incident-a" / "00-trigger"
        phase.mkdir(parents=True)
        (phase / "incident.json").write_text(
            json.dumps({"correlationId": "c" * 32}),
            encoding="utf-8",
        )
        (phase / "build-identity.json").write_text(
            json.dumps({
                "productVersion": "V2.12.0.27",
                "assemblyVersion": "2.12.0.27",
                "executablePath": r"D:\EPBTest\MTTFTest.exe",
                "executableSha256": "c" * 64,
                "configSha256": "a" * 64,
                "releaseConfigSha256": "a" * 64,
                "processBitness": 32,
                "processId": 1234,
                "gitCommit": "b" * 40,
                "gitDirty": "False",
                "buildUtc": "2026-08-08T00:00:00Z",
            }),
            encoding="utf-8",
        )
        process, result = self.run_gate()
        self.assertEqual(2, process.returncode)
        failures = {item["name"] for item in result["checks"] if not item["passed"]}
        self.assertIn("事故身份与所选正式会话一致", failures)

    def test_permanent_data_continuity_fault_is_redline(self) -> None:
        (self.root / "log" / "error.log").write_text(
            "2026-08-08 10:30:00.000\tERROR\tDAQ\t"
            "Code=DaqPersistenceWriteStall OldestMs=10000\n",
            encoding="utf-8",
        )
        process, result = self.run_gate()
        self.assertEqual(2, process.returncode)
        failures = {item["name"] for item in result["checks"] if not item["passed"]}
        self.assertIn("红线：永久数据连续性缺口=0", failures)

    def test_control_quality_and_rotated_logs_are_enforced(self) -> None:
        run_log = self.root / "log" / "run.log"
        text = run_log.read_text(encoding="utf-8")
        text = text.replace(
            "Channel=9 Cycle=1 Phase=Formal Peak=15.2 Target=15.0 Floor=14.2 Ceiling=15.8 Qualified=True",
            "Channel=9 Cycle=1 Phase=Formal Peak=17.1 Target=15.0 Floor=14.2 Ceiling=15.8 Qualified=False",
        )
        rotated = self.root / "log" / "run.20260808.001.log"
        rotated.write_text(text, encoding="utf-8")
        run_log.write_text(
            "2026-08-08 11:00:01.000\tINFO\t停止\trotated marker\n",
            encoding="utf-8",
        )
        process, result = self.run_gate()
        self.assertEqual(2, process.returncode)
        self.assertEqual(5, result["metrics"]["log_file_count"])
        failures = {item["name"] for item in result["checks"] if not item["passed"]}
        self.assertIn("正式圈各通道≥99%在合格带且峰值≤17A", failures)


if __name__ == "__main__":
    unittest.main(verbosity=2)
