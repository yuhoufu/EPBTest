#!/usr/bin/env python3
"""Self-contained regression tests for validate_epb_field_gate.py."""

from __future__ import annotations

import json
import sqlite3
import subprocess
import sys
import tempfile
import unittest
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
            "2026-08-08 10:00:00.000\tINFO\t启动\tProductVersion=V2.12.0.23\n"
            f"2026-08-08 10:00:00.000\tINFO\tFIELD\tFieldMetric SESSION Phase=Start RunId=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa Channels=4,9 Closed=False Detail=BatchFormal ProductVersion=V2.12.0.23 AssemblyVersion=2.12.0.23 ProcessId=1234 ExecutablePath=D:\\EPBTest\\MTTFTest.exe ExeSha256={sha} ConfigSha256={sha} GitCommit={commit} GitDirty=False BuildUtc=2026-08-08T00:00:00Z\n"
            "2026-08-08 10:00:00.100\tINFO\tFIELD\tFieldMetric STATE RunId=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa Device=Dev1 Channel=4 State=Running Reason=Running Revision=1 CorrelationId=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa RunEpoch=1 Enabled=True Formal=True Timer=True Runner=True Energized=False\n"
            "2026-08-08 10:00:00.100\tINFO\tFIELD\tFieldMetric STATE RunId=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa Device=Dev2 Channel=9 State=Running Reason=Running Revision=1 CorrelationId=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa RunEpoch=1 Enabled=True Formal=True Timer=True Runner=True Energized=False\n"
            "2026-08-08 10:00:01.000\tINFO\tFIELD\tFieldMetric DAQ Phase=Running Device=Dev1 ControlDepth=0 ControlOldestMs=0.0 ControlProcessMs=1.0 SubscriberMaxMs=1.0 PersistenceState=Recovered PersistenceDepth=1 PersistenceOldestMs=5.0 CallbackAgeMs=10.0 ControlProcessedAgeMs=10.0 Produced=100 Processed=100 Published=100 Persisted=99 Discontinuities=0 DurabilityBlocked=False Suppressed=0 Discarded=0 OverCapacityDropped=0\n"
            "2026-08-08 10:00:01.000\tINFO\tFIELD\tFieldMetric DAQ Phase=Running Device=Dev2 ControlDepth=0 ControlOldestMs=0.0 ControlProcessMs=1.0 SubscriberMaxMs=1.0 PersistenceState=Recovered PersistenceDepth=1 PersistenceOldestMs=5.0 CallbackAgeMs=10.0 ControlProcessedAgeMs=10.0 Produced=100 Processed=100 Published=100 Persisted=99 Discontinuities=0 DurabilityBlocked=False Suppressed=0 Discarded=0 OverCapacityDropped=0\n"
            "2026-08-08 10:00:02.000\tINFO\tFIELD\tFieldMetric DO_OFF Device=Dev1 Channel=4 CommandId=a Result=True Late=False QueueWaitMs=1 NIWriteMs=5 WorkerMs=6 TotalMs=7\n"
            "2026-08-08 10:00:02.000\tINFO\tFIELD\tFieldMetric DO_OFF Device=Dev2 Channel=9 CommandId=b Result=True Late=False QueueWaitMs=1 NIWriteMs=5 WorkerMs=6 TotalMs=7\n"
            "2026-08-08 10:00:10.000\tINFO\tFIELD\tFieldMetric UI DelayP95Ms=5 DelayMaxMs=10 FlushP95Ms=2 FlushMaxMs=3 AppendMaxMs=1 TrimMaxMs=1 ScrollMaxMs=1 RenderedBatches=2 RenderedLines=4 Pending=0 Dropped=0 FilePending=0 FileDropped=0\n"
            "2026-08-08 10:00:10.100\tINFO\tFIELD\tFieldMetric CYCLE RunId=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa Channel=4 Cycle=1 Phase=Formal Peak=15.1 Target=15.0 Floor=14.2 Ceiling=15.8 Qualified=True Result=Success\n"
            "2026-08-08 10:00:10.200\tINFO\tFIELD\tFieldMetric CYCLE RunId=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa Channel=9 Cycle=1 Phase=Formal Peak=15.2 Target=15.0 Floor=14.2 Ceiling=15.8 Qualified=True Result=Success\n"
            "2026-08-08 10:00:11.000\tINFO\tHOST\tHostRuntime PID=1 Bitness=32 ProcessCpu=8.0% SystemCpu=30.0% OtherCpu=22.0% WorkingSet=200MiB\n"
            "2026-08-08 10:59:58.000\tINFO\tFIELD\tFieldMetric STATE RunId=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa Device=Dev1 Channel=4 State=ManualStopped Reason=StopAll Revision=2 CorrelationId=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa RunEpoch=1 Enabled=True Formal=False Timer=False Runner=False Energized=False\n"
            "2026-08-08 10:59:58.000\tINFO\tFIELD\tFieldMetric STATE RunId=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa Device=Dev2 Channel=9 State=ManualStopped Reason=StopAll Revision=2 CorrelationId=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa RunEpoch=1 Enabled=True Formal=False Timer=False Runner=False Energized=False\n"
            "2026-08-08 10:59:59.000\tINFO\tFIELD\tFieldMetric STOP_PERSISTENCE Device=Dev1 RawDrained=True Boundary=100 Published=100 Persisted=100 Depth=0 State=Recovered Closed=True\n"
            "2026-08-08 10:59:59.000\tINFO\tFIELD\tFieldMetric STOP_PERSISTENCE Device=Dev2 RawDrained=True Boundary=100 Published=100 Persisted=100 Depth=0 State=Recovered Closed=True\n"
            "2026-08-08 11:00:00.000\tINFO\tFIELD\tFieldMetric SESSION Phase=Stop RunId=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa Channels=4,9 Closed=True Detail=ManualUi ProductVersion=V2.12.0.23 ExeSha256=unused ConfigSha256=unused GitCommit=unused GitDirty=False BuildUtc=unused\n"
            "2026-08-08 11:00:00.001\tINFO\t停止\tNormal Stop\n",
            encoding="utf-8",
        )
        for name in ("warning.log", "error.log", "ui-info.log"):
            (log / name).write_text("", encoding="utf-8")
        connection = sqlite3.connect(self.root / "index.db")
        try:
            connection.execute(
                "CREATE TABLE epb_cycles (epb_id INTEGER, cycle_number INTEGER, status TEXT)"
            )
            connection.execute("INSERT INTO epb_cycles VALUES (4, 1, 'completed')")
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
            "productVersion": "V2.12.0.23",
            "assemblyVersion": "2.12.0.23",
            "executablePath": r"D:\EPBTest\MTTFTest.exe",
            "executableSha256": "a" * 64,
            "configSha256": "a" * 64,
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
            "Detail=BatchFormal ProductVersion=V2.12.0.23 ExeSha256=" + "a" * 64 +
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
            "Detail=BatchFormal ProductVersion=V2.12.0.23 ExeSha256=" + "a" * 64 +
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
            "Detail=Duplicate ProductVersion=V2.12.0.23 ExeSha256=unused "
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
            "Device=Dev2 RawDrained=True Boundary=100 Published=100 Persisted=100 Depth=0 State=Recovered Closed=True",
            "Device=Dev2 RawDrained=False Boundary=100 Published=99 Persisted=99 Depth=1 State=Failed Closed=False",
        )
        run_log.write_text(text, encoding="utf-8")
        process, result = self.run_gate()
        self.assertEqual(2, process.returncode)
        failures = {item["name"] for item in result["checks"] if not item["passed"]}
        self.assertIn("控制订阅者P99<5ms且最大<20ms", failures)
        self.assertIn("停止Raw发布与持久化边界闭合", failures)

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
