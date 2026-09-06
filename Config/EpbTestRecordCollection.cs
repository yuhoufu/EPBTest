using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Config
{
    /// <summary>
    /// EPB 记录的并发安全、按通道唯一集合。
    /// 同一 Id 的 Add/Insert/索引赋值均执行单调证据合并；枚举器基于快照，
    /// 避免保存与 UI 回写并发时出现重复键或“集合已修改”异常。
    /// </summary>
    public sealed class EpbTestRecordCollection : IEnumerable<EpbTestRecord>
    {
        private readonly object _gate = new object();
        private readonly List<EpbTestRecord> _items = new List<EpbTestRecord>();

        public int Count
        {
            get { lock (_gate) return _items.Count; }
        }

        public EpbTestRecord this[int index]
        {
            get { lock (_gate) return _items[index]; }
            set
            {
                if (value == null) throw new ArgumentNullException(nameof(value));
                lock (_gate)
                {
                    if (index < 0 || index >= _items.Count)
                        throw new ArgumentOutOfRangeException(nameof(index));
                    _items.RemoveAt(index);
                    AddCore(value);
                    SortCore();
                }
            }
        }

        public void Add(EpbTestRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            lock (_gate)
            {
                AddCore(record);
                SortCore();
            }
        }

        public void AddRange(IEnumerable<EpbTestRecord> records)
        {
            if (records == null) return;
            var snapshot = records.Where(record => record != null).ToArray();
            lock (_gate)
            {
                foreach (var record in snapshot) AddCore(record);
                SortCore();
            }
        }

        public void ReplaceAll(IEnumerable<EpbTestRecord> records)
        {
            var snapshot = (records ?? Enumerable.Empty<EpbTestRecord>())
                .Where(record => record != null)
                .ToArray();
            lock (_gate)
            {
                _items.Clear();
                foreach (var record in snapshot) AddCore(record);
                SortCore();
            }
        }

        public void Clear()
        {
            lock (_gate) _items.Clear();
        }

        public EpbTestRecord Find(Predicate<EpbTestRecord> predicate)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));
            lock (_gate) return _items.Find(predicate);
        }

        public void Sort(Comparison<EpbTestRecord> comparison)
        {
            if (comparison == null) throw new ArgumentNullException(nameof(comparison));
            lock (_gate) _items.Sort(comparison);
        }

        public EpbTestRecord[] Snapshot()
        {
            lock (_gate) return _items.ToArray();
        }

        public IEnumerator<EpbTestRecord> GetEnumerator() =>
            ((IEnumerable<EpbTestRecord>)Snapshot()).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private void AddCore(EpbTestRecord record)
        {
            var existingIndex = _items.FindIndex(item => item.Id == record.Id);
            if (existingIndex < 0)
            {
                _items.Add(record);
                return;
            }

            _items[existingIndex] = TestConfig.MergeDuplicateEpbRecords(
                new[] { _items[existingIndex], record });
        }

        private void SortCore() => _items.Sort((left, right) => left.Id.CompareTo(right.Id));
    }
}
