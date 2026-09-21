#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Sophon.Core.Alarm
{
    /// <summary>
    /// 统一报警中心（线程安全）。
    /// 支持定义注册、主动触发/消除、事件通知、联动触发与历史持久化。
    /// </summary>
    public class AlarmCenter
    {
        private readonly ConcurrentDictionary<string, AlarmDefinition> _definitions = new();
        private readonly ConcurrentDictionary<string, ActiveAlarm> _activeAlarms = new();
        private readonly object _historyLock = new();
        private readonly List<AlarmHistoryRecord> _historyRecords = new();
        private readonly IAlarmLinkageHandler? _linkageHandler;
        private readonly string _historyFilePath;

        /// <summary>
        /// 报警触发事件。
        /// </summary>
        public event Action<ActiveAlarm>? AlarmRaised;

        /// <summary>
        /// 报警消除事件。
        /// </summary>
        public event Action<string>? AlarmCleared;

        /// <summary>
        /// 当前所有活动报警快照（线程安全）。
        /// </summary>
        public IReadOnlyList<ActiveAlarm> ActiveAlarms => _activeAlarms.Values.ToList();

        /// <summary>
        /// 报警定义集合。
        /// </summary>
        public IReadOnlyDictionary<string, AlarmDefinition> Definitions => _definitions;

        public AlarmCenter(IAlarmLinkageHandler? linkageHandler = null, string? historyFilePath = null)
        {
            _linkageHandler = linkageHandler;
            _historyFilePath = historyFilePath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SophonData", "alarm_history.json");
            LoadHistory();
        }

        /// <summary>
        /// 注册报警定义。
        /// </summary>
        public void Register(AlarmDefinition definition)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            _definitions[definition.Code] = definition;
        }

        /// <summary>
        /// 触发报警。
        /// </summary>
        /// <param name="code">报警代码</param>
        /// <param name="detail">报警明细或上下文（如轴编号、限位名）</param>
        /// <param name="source">报警来源（如 Motion, LimitMonitor, Watchdog 等）</param>
        public ActiveAlarm Raise(string code, string? detail = null, string source = "System")
        {
            if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("Alarm code cannot be null or empty", nameof(code));

            if (!_definitions.TryGetValue(code, out var def))
            {
                // 未预注册的报警默认赋予 Error 等级
                def = new AlarmDefinition(code, $"未注册报警: {code}", AlarmSeverity.Error, LinkageMode.None);
                _definitions[code] = def;
            }

            var active = new ActiveAlarm(
                Code: def.Code,
                Message: def.Message,
                Severity: def.Severity,
                LinkageMode: def.LinkageMode,
                Detail: detail ?? string.Empty,
                Source: source,
                TriggerTime: DateTime.Now);

            _activeAlarms[code] = active;

            // 写入历史
            lock (_historyLock)
            {
                _historyRecords.Add(new AlarmHistoryRecord
                {
                    Id = _historyRecords.Count + 1,
                    Code = active.Code,
                    Message = active.Message,
                    Severity = active.Severity,
                    LinkageMode = active.LinkageMode,
                    Detail = active.Detail,
                    Source = active.Source,
                    TriggerTime = active.TriggerTime
                });
                SaveHistory();
            }

            // 触发联动动作
            try
            {
                _linkageHandler?.Handle(def, active.Detail);
            }
            catch { }

            // 触发外部事件
            try
            {
                AlarmRaised?.Invoke(active);
            }
            catch { }

            // 若配置了自动复位
            if (def.AutoReset)
            {
                Clear(code);
            }

            return active;
        }

        /// <summary>
        /// 消除报警。
        /// </summary>
        /// <param name="code">报警代码</param>
        public bool Clear(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return false;

            if (_activeAlarms.TryRemove(code, out _))
            {
                lock (_historyLock)
                {
                    var record = _historyRecords.LastOrDefault(r => r.Code == code && r.ClearTime == null);
                    if (record != null)
                    {
                        record.ClearTime = DateTime.Now;
                    }
                    SaveHistory();
                }

                try
                {
                    AlarmCleared?.Invoke(code);
                }
                catch { }

                return true;
            }

            return false;
        }

        /// <summary>
        /// 获取历史报警记录列表。
        /// </summary>
        public IReadOnlyList<AlarmHistoryRecord> GetHistory(DateTime? start = null, DateTime? end = null)
        {
            lock (_historyLock)
            {
                var query = _historyRecords.AsEnumerable();
                if (start.HasValue)
                {
                    query = query.Where(r => r.TriggerTime >= start.Value);
                }
                if (end.HasValue)
                {
                    query = query.Where(r => r.TriggerTime <= end.Value);
                }
                return query.ToList();
            }
        }

        /// <summary>
        /// 清空全部历史记录。
        /// </summary>
        public void ClearHistory()
        {
            lock (_historyLock)
            {
                _historyRecords.Clear();
                SaveHistory();
            }
        }

        private void SaveHistory()
        {
            try
            {
                string? dir = Path.GetDirectoryName(_historyFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(_historyRecords, options);
                File.WriteAllText(_historyFilePath, json);
            }
            catch { }
        }

        private void LoadHistory()
        {
            try
            {
                if (File.Exists(_historyFilePath))
                {
                    string json = File.ReadAllText(_historyFilePath);
                    var list = JsonSerializer.Deserialize<List<AlarmHistoryRecord>>(json);
                    if (list != null)
                    {
                        lock (_historyLock)
                        {
                            _historyRecords.Clear();
                            _historyRecords.AddRange(list);
                        }
                    }
                }
            }
            catch { }
        }
    }
}
