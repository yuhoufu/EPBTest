using Epb.Control;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using IO.NI;
using IAppLogger = Config.IAppLogger;

namespace Controller
{
	/// <summary>
	/// EPB 运行器（partial）：新增“无计时器推进”的接口，由 GroupScheduler 定期调用。
	/// </summary>
	public partial class EpbCycleRunner
	{
		/// <summary>EPB 子状态（含学习）。</summary>
		private enum SubState
		{
			Idle = 0,
			LearnFwdEmpty,
			LearnRevEmpty,
			LearnPolarity,
			LearnPredict,
			Forward,
			Hold,
			Reverse
		}

		private SubState _state = SubState.Idle;

		// —— 根据你的工程接入实际 DO 控制/阈值判据所需的引用与字段 —— //
		private readonly int _epbId;
		//private readonly IAppLogger _log;

		// 配置（示例，按需取自你的 TestConfig）
		private readonly int _holdMs;
		private readonly double _cutCurrentA;
		private readonly double _limitCurrentA;

		// 学习态（可与 EpbLearnStateStore 持久化协同）
		private readonly EpbLearnStateStore.LearnState _learn;

        // ====== 学习期的最小依赖 ======

        // 可选：外部电流读取委托（单位 A）。如果你有现成 DAQ 读取方法，可在构造时赋值：_readCurrentA = id => daq.GetFastCurrent(id);
        private readonly Func<int, double> _readCurrentA;

        // 学习阶段的内部计时/采样
        private DateTime _learnStepStartUtc;
        private double _learnBaselineA;              // 学习时的基线电流
        private double _learnMaxA;                   // 该子阶段内见到的最大电流
        private double _emaSlope;                    // dI/dt 的指数滑动平均（A/ms）
        private double _lastIA;                      // 上一拍电流
        private DateTime _lastSampleUtc;

        // —— 学习与判据参数（若未从外部注入就用这些默认值；可改为从 TestConfig 读取） —— //
        private readonly double _probeRampAps = 0.05;        // 学习期预期的电流爬升速度 (A/ms)（仅用于日志对比）
        private readonly double _probeMaxA = 4.0;         // 学习期最大允许电流（防超流）
        private readonly int _holdProbeMs = 150;         // 学习期短保持
        private readonly int _learnTimeoutMs = 2000;      // 单子阶段超时守护
        private readonly double _slopeMinAps = 0.05;        // 接触区识别的最小斜率
        private readonly double _deltaIThreshA = 0.30;        // 接触识别的瞬变门限

        // ====== 小工具 ======

        /// <summary>读取当前通道电流。如果未设置读取委托，则返回 NaN。</summary>
        private double ReadCurrentA()
        {
            try
            {
                if (_readCurrentA == null) return double.NaN;
                return _readCurrentA(_epbId);
            }
            catch
            {
                return double.NaN;
            }
        }

        /// <summary>开始一个学习子阶段：复位计时与采样状态，并（可选）置指定 DO。</summary>
        private void BeginLearnSubstep(Action doAction = null)
        {
            _learnStepStartUtc = DateTime.UtcNow;
            _lastSampleUtc = _learnStepStartUtc;
            _learnBaselineA = double.NaN;
            _learnMaxA = 0;
            _emaSlope = 0;
            _lastIA = ReadCurrentA();
            doAction?.Invoke();
        }

        /// <summary>是否超过该学习子阶段的超时（守护）。</summary>
        private bool LearnTimedOut(DateTime nowUtc) =>
            (nowUtc - _learnStepStartUtc).TotalMilliseconds >= _learnTimeoutMs;

        /// <summary>对 dI/dt 做一个简易 EMA 平滑。</summary>
        private static double Ema(double prev, double newest, double alpha = 0.2) => alpha * newest + (1 - alpha) * prev;
        
		public EpbCycleRunner(int epbId, int holdMs, double cutA, double limitA, IAppLogger log)
		{
			_epbId = epbId;
			_holdMs = holdMs;
			_cutCurrentA = cutA;
			_limitCurrentA = limitA;
			_log = log ?? new NullAppLogger();

			_learn = EpbLearnStateStore.Instance.GetOrCreate(epbId);
			if (_learn.LearnStatus != "Completed")
			{
				_state = SubState.LearnFwdEmpty;
				_learn.LearnStatus = "InProgress";
				EpbLearnStateStore.Instance.SetAndSave(_epbId, _learn);
			}
			else
			{
				_state = SubState.Forward;
			}
		}
        
        /// <summary>
        /// 由调度器每 Tick 调用：推进子状态机。返回值表示本 Tick 是否发生了有效推进，
        /// <paramref name="completedThisCycle"/> 标识是否恰好在本 Tick 完成了一个循环。
        /// </summary>
        /// <param name="nowUtc">当前 UTC 时间。</param>
        /// <param name="relPhaseMs">相对相位（毫秒）。已包含组内错峰。</param>
        /// <param name="completedThisCycle"></param>
        public bool TryAdvance(DateTime nowUtc, int relPhaseMs, out bool completedThisCycle)
		{
			completedThisCycle = false;

			// TODO: 从 DAQ 快照读取工程值（电流/压力），例如：
			// var iA = _daq.GetFastCurrent(_epbId);

			switch (_state)
			{
				case SubState.LearnFwdEmpty:
					if (LearnStepFwdEmpty(nowUtc, relPhaseMs))
					{
						_state = SubState.LearnRevEmpty;
						SaveLearn("FwdEmpty");
					}
					return true;

				case SubState.LearnRevEmpty:
					if (LearnStepRevEmpty(nowUtc, relPhaseMs))
					{
						_state = SubState.LearnPolarity;
						SaveLearn("RevEmpty");
					}
					return true;

				case SubState.LearnPolarity:
					if (LearnStepPolarity(nowUtc, relPhaseMs))
					{
						_state = SubState.LearnPredict;
						SaveLearn("Polarity");
					}
					return true;

				case SubState.LearnPredict:
					if (LearnStepPredict(nowUtc, relPhaseMs))
					{
						_learn.LearnStatus = "Completed";
						_learn.LastLearnAt = DateTime.Now;
						EpbLearnStateStore.Instance.SetAndSave(_epbId, _learn);

						_state = SubState.Forward;
						_log.Info($"EPB[{_epbId}] Learn completed → Cycle", "EPB");
					}
					return true;

				case SubState.Forward:
					// 示例判据：达到 Cut 电流或时间进入 Hold
					DoForward(); // 置 DO 前进，上电受限电流
					if (PredictOrReachedCut(/*iA*/))
					{
						_state = _holdMs > 0 ? SubState.Hold : SubState.Reverse;
						DoClosePower(); // 提前断电或减少电流
						DoOff(); 
					}
					return true;

				case SubState.Hold:
					if (relPhaseMs >= _holdMs)
					{
						_state = SubState.Reverse;
						DoReverse(); // 置 DO 反向
					}
					return true;

				case SubState.Reverse:
					if (ReverseFinished(/*iA*/))
					{
						// 一圈完成：落安全 DO
						ForceSafeOff();
						completedThisCycle = true;
						_state = SubState.Forward; // 下一圈回到 Forward（相位由调度器重置）
					}
					return true;

				case SubState.Idle:
				default:
					return false;
			}
		}

        /// <summary>
        /// 强制安全断电：任何异常/Stop/E-Stop/优雅暂停完成点调用。
        /// </summary>
        public void ForceSafeOff()
        {
            try
            {
                _do?.SetEpbOff(_epbId); // 直接落硬件 OFF
                _log?.Info($"EPB[{_epbId}] DO → OFF（安全断电）。", "EPB");
            }
            catch (Exception ex)
            {
                _log?.Error($"EPB[{_epbId}] SafeOff 失败：{ex.Message}", "EPB", ex);
                // 不再向外抛出，避免上层被异常中断；此处已在硬件层尽力落态
            }
        }

		// ================= 学习阶段的每步实现（示意，按你的硬件/阈值细化） =================

		/// <summary>
		/// 学习：前进空行程。以小电流爬升寻找接触区拐点，得到 I_empty_fwd 与斜率特征；
		/// 返回 true 表示该子阶段完成，可进入下一步。
		/// </summary>
		private bool LearnStepFwdEmpty(DateTime nowUtc, int relPhaseMs)
		{
			// 第一次进入该子阶段：置 DO 正向、复位计时
			if (relPhaseMs == 0)
			{
				BeginLearnSubstep(() =>
				{
					_do?.SetEpbForward(_epbId); // 小电流前进（若硬件不支持软限流，这里就是普通前进）
					_log?.Info($"EPB[{_epbId}] Learn-FwdEmpty 开始：DO→Forward。", "EPB");
				});
				return false;
			}

			// 超时守护：仍然落下一步，防止卡住
			if (LearnTimedOut(nowUtc))
			{
				_log?.Warn($"EPB[{_epbId}] Learn-FwdEmpty 超时（>{_learnTimeoutMs}ms），使用时间型阈值兜底。", "EPB");
				_learn.IEmptyFwd = Math.Max(0, _learnMaxA);
				_learn.SlopeFwdAps = Math.Max(_learn.SlopeFwdAps, _emaSlope);
				_do?.SetEpbOff(_epbId); // 停一下再进入下一子阶段
				return true;
			}

			// 采样与判据
			var iA = ReadCurrentA();
			if (!double.IsNaN(iA))
			{
				var dt = (nowUtc - _lastSampleUtc).TotalMilliseconds;
				if (dt > 1)
				{
					var di = iA - (double.IsNaN(_lastIA) ? iA : _lastIA);
					var slope = di / dt;                 // A/ms
					_emaSlope = Ema(_emaSlope, slope);   // 平滑斜率
					_lastSampleUtc = nowUtc;
					_lastIA = iA;
					_learnMaxA = Math.Max(_learnMaxA, iA);

					// 建立基线（开始 80ms 内的中值做基线）
					if (double.IsNaN(_learnBaselineA))
					{
						if (relPhaseMs >= 80) _learnBaselineA = iA;
					}

					// 接触区判据：斜率上升到阈值 或 瞬变电流达到门限
					var deltaI = (!double.IsNaN(_learnBaselineA)) ? iA - _learnBaselineA : 0;
					if (_emaSlope >= _slopeMinAps || deltaI >= _deltaIThreshA || iA >= _probeMaxA)
					{
						_learn.IEmptyFwd = Math.Max(0, iA);
						_learn.SlopeFwdAps = Math.Max(_learn.SlopeFwdAps, _emaSlope);

						// 轻微保持，随后断电作为该子阶段结束
						if (relPhaseMs >= _holdProbeMs)
						{
							_do?.SetEpbOff(_epbId);
							_log?.Info($"EPB[{_epbId}] Learn-FwdEmpty 完成：I={_learn.IEmptyFwd:F2}A, slope={_emaSlope:F3}A/ms。", "EPB");
							return true;
						}
					}
				}
			}
			else
			{
				// 没有电流数据时，退化为时间判据：保持一小段时间后结束
				if (relPhaseMs >= 400)
				{
					_do?.SetEpbOff(_epbId);
					_learn.IEmptyFwd = _learnMaxA; // 只能记最大观测
					_log?.Warn($"EPB[{_epbId}] Learn-FwdEmpty 无电流数据，按时间兜底完成。", "EPB");
					return true;
				}
			}

			return false;
		}

        /// <summary>
        /// 学习：反向空行程。以小电流回程，得到 I_empty_rev / 回程死区等；
        /// 返回 true 表示该子阶段完成。
        /// </summary>
        private bool LearnStepRevEmpty(DateTime nowUtc, int relPhaseMs)
        {
            if (relPhaseMs == 0)
            {
                BeginLearnSubstep(() =>
                {
                    _do?.SetEpbReverse(_epbId);
                    _log?.Info($"EPB[{_epbId}] Learn-RevEmpty 开始：DO→Reverse。", "EPB");
                });
                return false;
            }

            if (LearnTimedOut(nowUtc))
            {
                _do?.SetEpbOff(_epbId);
                _learn.IEmptyRev = Math.Max(0, _learnMaxA);
                _log?.Warn($"EPB[{_epbId}] Learn-RevEmpty 超时（>{_learnTimeoutMs}ms），按时间兜底完成。", "EPB");
                return true;
            }

            var iA = ReadCurrentA();
            if (!double.IsNaN(iA))
            {
                var dt = (nowUtc - _lastSampleUtc).TotalMilliseconds;
                if (dt > 1)
                {
                    var di = iA - (double.IsNaN(_lastIA) ? iA : _lastIA);
                    var slope = di / dt;
                    _emaSlope = Ema(_emaSlope, slope);
                    _lastSampleUtc = nowUtc;
                    _lastIA = iA;
                    _learnMaxA = Math.Max(_learnMaxA, iA);

                    // 回程识别：电流下降到接近基线，或达到最小时长
                    if (relPhaseMs >= _holdProbeMs && (_emaSlope <= -_slopeMinAps || iA <= (_learnBaselineA + 0.1)))
                    {
                        _learn.IEmptyRev = Math.Max(0, iA);
                        _do?.SetEpbOff(_epbId);
                        _log?.Info($"EPB[{_epbId}] Learn-RevEmpty 完成：I={_learn.IEmptyRev:F2}A。", "EPB");
                        return true;
                    }
                }
            }
            else
            {
                if (relPhaseMs >= 300)
                {
                    _do?.SetEpbOff(_epbId);
                    _learn.IEmptyRev = _learnMaxA;
                    _log?.Warn($"EPB[{_epbId}] Learn-RevEmpty 无电流数据，按时间兜底完成。", "EPB");
                    return true;
                }
            }

            return false;
        }


        /// <summary>
        /// 学习：极性自检。快速切换正/反，验证实际电流响应是否与配置方向一致；
        /// 返回 true 表示该子阶段完成。
        /// </summary>
        private bool LearnStepPolarity(DateTime nowUtc, int relPhaseMs)
        {
            // 0~60ms：Forward；60~120ms：Off；120~180ms：Reverse；然后结束。
            if (relPhaseMs == 0)
            {
                _do?.SetEpbForward(_epbId);
                _log?.Info($"EPB[{_epbId}] Learn-Polarity：阶段1 DO→Forward。", "EPB");
                _learnBaselineA = ReadCurrentA(); // 记录一份基线
                return false;
            }
            if (relPhaseMs == 60)
            {
                _do?.SetEpbOff(_epbId);
                _log?.Info($"EPB[{_epbId}] Learn-Polarity：阶段2 DO→Off。", "EPB");
                return false;
            }
            if (relPhaseMs == 120)
            {
                _do?.SetEpbReverse(_epbId);
                _log?.Info($"EPB[{_epbId}] Learn-Polarity：阶段3 DO→Reverse。", "EPB");
                return false;
            }

            // 到 180ms 结束；如有电流数据，可粗检一致性
            if (relPhaseMs >= 180)
            {
                _do?.SetEpbOff(_epbId);

                var iA = ReadCurrentA();
                if (!double.IsNaN(iA) && !double.IsNaN(_learnBaselineA))
                {
                    var delta = Math.Abs(iA - _learnBaselineA);
                    if (delta < 0.05)
                    {
                        _log?.Warn($"EPB[{_epbId}] Learn-Polarity：电流变化过小，建议人工检查继电器极性。", "EPB");
                    }
                }

                _log?.Info($"EPB[{_epbId}] Learn-Polarity 完成。", "EPB");
                return true;
            }

            return false;
        }

        /// <summary>
        /// 学习：预测提前断电复核。用一次低负载夹紧，验证“提前关断”是否能贴近目标 Cut 电流；
        /// 返回 true 表示该子阶段完成。
        /// </summary>
        private bool LearnStepPredict(DateTime nowUtc, int relPhaseMs)
        {
            // 策略：Forward → 观察电流斜率 → 当预测到 t_to_cut 很短时先 DoOff → 检查 overshoot。
            if (relPhaseMs == 0)
            {
                BeginLearnSubstep(() =>
                {
                    _do?.SetEpbForward(_epbId);
                    _log?.Info($"EPB[{_epbId}] Learn-Predict 开始：DO→Forward。", "EPB");
                });
                return false;
            }

            if (LearnTimedOut(nowUtc))
            {
                _do?.SetEpbOff(_epbId);
                _log?.Warn($"EPB[{_epbId}] Learn-Predict 超时，结束。", "EPB");
                return true;
            }

            var iA = ReadCurrentA();
            if (!double.IsNaN(iA))
            {
                var dt = (nowUtc - _lastSampleUtc).TotalMilliseconds;
                if (dt > 1)
                {
                    var di = iA - (double.IsNaN(_lastIA) ? iA : _lastIA);
                    var slope = Math.Max(1e-6, di / dt);     // A/ms，避免除零
                    _emaSlope = Ema(_emaSlope, slope);
                    _lastSampleUtc = nowUtc;
                    _lastIA = iA;

                    // 目标 cut 电流，用你的运行配置（这里以 _cutCurrentA 为例）
                    var cut = _cutCurrentA <= 0 ? 8.0 : _cutCurrentA;

                    // 估算到达 cut 的剩余时间：t = (cut - I) / slope
                    var remainMs = (cut - iA) / Math.Max(1e-6, _emaSlope);

                    // 当 remain 很短（例如 < 50ms）时，提前断电
                    if (remainMs <= 50)
                    {
                        _do?.SetEpbOff(_epbId);
                        _log?.Info($"EPB[{_epbId}] Learn-Predict：提前断电，remain≈{remainMs:F1}ms，I≈{iA:F2}A。", "EPB");
                        return true;
                    }
                }
            }
            else
            {
                // 无电流数据：时间兜底（保持 200ms 后断电）
                if (relPhaseMs >= 200)
                {
                    _do?.SetEpbOff(_epbId);
                    _log?.Warn($"EPB[{_epbId}] Learn-Predict 无电流数据，按时间兜底完成。", "EPB");
                    return true;
                }
            }

            return false;
        }


		private void SaveLearn(string stage)
		{
			_learn.LearnStage = stage;
			EpbLearnStateStore.Instance.SetAndSave(_epbId, _learn);
		}

		// ================= 正式动作的硬件调用（示意） =================

        /// <summary>
        /// 使能当前 EPB 通道的正向上电动作（前进）。
        /// </summary>
        /// <remarks>
        /// 内部统一加日志与异常保护；若需要互斥（例如同通道不能同时正、反），可在此处加入互锁检查。
        /// </remarks>
        private void DoForward()
        {
            try
            {
                _do.SetEpbForward(_channel); // 你项目里已有的 DO 控制
                _log.Info($"EPB[{_channel}] DO → Forward（正向上电）。", "EPB");
            }
            catch (Exception ex)
            {
                _log.Error($"EPB[{_channel}] DO Forward 失败：{ex.Message}", "EPB", ex);
                throw;
            }
        }

        /// <summary>
        /// （可选）在正向爬升接近阈值时做一次“限流/降压”的软保护，减少超冲；
        /// 如果你的电源没有软限流接口，这里就仅做日志（保持兼容）。
        /// </summary>
        /// <param name="note">触发原因（便于日志研判）。</param>
        private void DoClosePower(string note = null)
        {
            try
            {
                // 如果你的 DoController 有更细粒度的接口（例如 SetEpbForwardLow、SetEpbCurrentLimit 等），
                // 在这里调用并返回；没有的话就只打日志。
                _log.Warn($"EPB[{_channel}] 进入限流/降压保护（{note ?? "接近阈值"}），如需硬件支持可在此处接限流 DO。", "EPB");
                // 示例（若存在接口）：_do.SetEpbForwardLow(_channel);
                // 或：_do.SetCurrentLimit(_channel, _limitCurrentA);
            }
            catch (Exception ex)
            {
                _log.Error($"EPB[{_channel}] DoClosePower 异常：{ex.Message}", "EPB", ex);
                // 降级处理：至少保证不断电，由上层的阈值判据继续控制
            }
        }


		/// <summary>
		/// 使能当前 EPB 通道的反向上电动作（回程/释放）。
		/// </summary>
		private void DoReverse()
        {
            try
            {
                _do.SetEpbReverse(_channel);
                _log.Info($"EPB[{_channel}] DO → Reverse（反向上电）。", "EPB");
            }
            catch (Exception ex)
            {
                _log.Error($"EPB[{_channel}] DO Reverse 失败：{ex.Message}", "EPB", ex);
                throw;
            }
        }

        /// <summary>
        /// 关闭当前 EPB 通道的上电（断电）。
        /// </summary>
        /// <remarks>
        /// 所有“到达阈值/平台/预测触发”的场景，应**立即**调用该方法避免超调。
        /// </remarks>
        private void DoOff()
        {
            try
            {
                _do.SetEpbOff(_channel);
                _log.Info($"EPB[{_channel}] DO → OFF（断电）。", "EPB");
            }
            catch (Exception ex)
            {
                _log.Error($"EPB[{_channel}] DO Off 失败：{ex.Message}", "EPB", ex);
                throw;
            }
        }

        /// <summary>
        /// 前进阶段的“到达 Cut 电流”的综合判据：
        /// 1) 直接达阈（I ≥ _cutCurrentA）；
        /// 2) 预测到达（根据学习得到的斜率 _learn.SlopeFwdAps 估算 (Cut - I)/slope ≤ 预设提前量）。
        /// </summary>
        private bool PredictOrReachedCut(/*double iA*/)
        {
            try
            {
                // 当前电流（A）
                var iAcur = _readCurrent != null ? _readCurrent(_channel) : double.NaN;

                // 运行阈值（如果配置缺失，给一个保守默认值）
                var cut = (_cutCurrentA > 0 ? _cutCurrentA : 8.0);

                // ① 直接达到/超过阈值：立即判定为到达
                if (!double.IsNaN(iAcur) && iAcur >= cut)
                    return true;

                // ② 预测提前到达：使用学习期得到的前进斜率（A/ms）
                var slopeAps = (_learn != null && _learn.SlopeFwdAps > 1e-6) ? _learn.SlopeFwdAps : 0.03; // 缺省 0.03A/ms

                if (!double.IsNaN(iAcur))
                {
                    var remainMs = (cut - iAcur) / Math.Max(1e-6, slopeAps);  // 剩余时间（ms）
                    // 留 40~60ms 的提前量，减少过冲；这里取 50ms
                    if (remainMs <= 50.0)
                        return true;
                }

                // 没有电流数据则只能继续运行，由上层时间窗限制
                return false;
            }
            catch (Exception ex)
            {
                _log?.Warn($"EPB[{_epbId}] PredictOrReachedCut 判据异常：{ex.Message}", "EPB");
                return false;
            }
        }


        /// <summary>
        /// 反向阶段的“回程完成”判据：
        /// 1) 最小反向时间门槛（避免过快抖动误判）；
        /// 2) 电流回到空行程带（|I - I_empty_rev| ≤ _emptyBandA），并在 _stableWinMs 内稳定；
        /// 若没有电流数据，则退化为纯时间门槛（默认 300ms）。
        /// </summary>
        private DateTime _revInBandSinceUtc = DateTime.MinValue;
        private bool ReverseFinished(/*double iA*/)
        {
            try
            {
                // 先保证一个最小反向时间（防抖）
                const int minReverseMs = 120; // 可按需调整或改为配置
                if (!relTimeExceeded(minReverseMs))
                    return false;

                var iAcur = _readCurrent != null ? _readCurrent(_channel) : double.NaN;

                // 没有电流数据：退化为纯时间判据（与原示例保持一致）
                if (double.IsNaN(iAcur))
                    return relTimeExceeded(300);

                // 学习得到的回程空行程电流（若无学习值，则以 0A 作为基线）
                var baseA = (_learn != null && _learn.IEmptyRev > 0) ? _learn.IEmptyRev : 0.0;
                var bandA = (_emptyBandA > 0 ? _emptyBandA : 0.2); // 容差带，默认 0.2A

                var inBand = Math.Abs(iAcur - baseA) <= bandA;

                if (inBand)
                {
                    if (_revInBandSinceUtc == DateTime.MinValue)
                        _revInBandSinceUtc = DateTime.UtcNow;

                    // 需要在“空行程带”内保持稳定一段时间（_stableWinMs）才算真正完成
                    var stableMs = Math.Max(20, _stableWinMs); // 给个下限，避免 0 值
                    if ((DateTime.UtcNow - _revInBandSinceUtc).TotalMilliseconds >= stableMs)
                        return true;
                }
                else
                {
                    // 离开带内则重置稳定计时
                    _revInBandSinceUtc = DateTime.MinValue;
                }

                return false;
            }
            catch (Exception ex)
            {
                _log?.Warn($"EPB[{_epbId}] ReverseFinished 判据异常：{ex.Message}", "EPB");
                // 异常时保守处理：按时间兜底
                return relTimeExceeded(300);
            }
        }


		private bool relTimeExceeded(int ms) => false; // 由调度器传入 relPhaseMs 管控；此处示意
	}
}
