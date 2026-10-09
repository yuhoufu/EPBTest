import datetime as dt
import hashlib
import json
import sqlite3
from pathlib import Path

root = Path(r'D:\EPB_Data\Backups\10243-028_V2.16.0.0_I0037_0905_1653')
out = Path(__file__).parent
session = 'session-0ccbc780120e4d7db7053446724797f1'
selected = ['backup-manifest.json', 'Config/runtime-build-identity.json', 'log/run.log',
            'log/error.log', 'log/warning.log', 'log/ui-info.log',
            f'WatchdogSessions/{session}.application-exit.json', f'WatchdogSessions/{session}.closing.json',
            f'WatchdogSessions/{session}.sidecar-events.jsonl', f'WatchdogSessions/{session}.client-events.jsonl',
            'index.db']
def read_json(p):
    return json.loads((root / p).read_text(encoding='utf-8-sig'))
def local_time(ticks):
    value = dt.datetime(1, 1, 1) + dt.timedelta(microseconds=ticks // 10, hours=8)
    return value.isoformat(timespec='microseconds') + '+08:00'
exit_receipt = read_json(f'WatchdogSessions/{session}.application-exit.json')
tombstone = read_json(f'WatchdogSessions/{session}.closing.json')
connection = sqlite3.connect((root / 'index.db').as_uri() + '?mode=ro&immutable=1', uri=True)
check = connection.execute('PRAGMA quick_check').fetchall()
tables = [row[0] for row in connection.execute("SELECT name FROM sqlite_master WHERE type='table'")]
connection.close()
patterns = [
    '圈内DAQ批次序号不连续', 'DaqCallbackGapUnenergized', '回调空窗',
    'RecoveryContractRegistrationFailed', 'Recovering publication did not commit',
    'FormalSlotSafetyBoundaryFailed', 'DurableCloseFenceIdentityChanged',
    'StopAllAdmissionApplicationClosing', 'StopAllAdmissionRejected',
    'StopAll检测到液压组', '数据耐久边界仍未确认', '数据安全边界',
    'SemanticSnapshotStalled', 'ExactMainProcessTerminatedAt30SecondDeadline']
excerpts = []
for rel in ['log/run.log', 'log/error.log', 'log/warning.log', 'log/ui-info.log']:
    for number, line in enumerate((root / rel).read_text(encoding='utf-8-sig').splitlines(), 1):
        if any(p in line for p in patterns):
            excerpts.append({'file': rel, 'line': number, 'text': line})
result = {
    'backup': str(root), 'read_only': True, 'sqlite_quick_check': check, 'sqlite_tables': tables,
    'files': [{'path': p, 'bytes': (root / p).stat().st_size,
               'sha256': hashlib.file_digest((root / p).open('rb'), 'sha256').hexdigest()} for p in selected],
    'exit': {k: exit_receipt.get(k) for k in ['State', 'MainProcessId', 'StopSafetyTransactionId', 'Detail', 'MotorsOff',
             'PowerOff', 'PressureSafe', 'PersistenceDrained', 'LogicalQuiescent', 'DataContinuityVerified']},
    'exit_times': {k: local_time(exit_receipt[k]) for k in ['RequestedUtcTicks', 'HardDeadlineUtcTicks', 'UpdatedUtcTicks']},
    'exit_elapsed_ms': (exit_receipt['UpdatedUtcTicks'] - exit_receipt['RequestedUtcTicks']) / 10000,
    'previous_closing': {k: tombstone.get(k) for k in ['SessionId','SessionGeneration','SessionLease','CloseIntent',
                         'OldProcessId','StopSafetyTransactionId','StopRunId','StopRunEpoch','StopSafetyBoundaryGeneration']},
    'excerpts': excerpts
}
(out / 'evidence-index.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps({k: v for k, v in result.items() if k not in ['files','excerpts']}, ensure_ascii=False, indent=2))
print('Indexed excerpts:', len(excerpts))
