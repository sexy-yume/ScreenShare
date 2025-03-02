using System;
using System.Diagnostics;
using System.Threading;
using System.Collections.Concurrent;
using ScreenShare.Common.Utils;

namespace ScreenShare.Client.Network
{
    /// <summary>
    /// Manages adaptive frame rate and bitrate control based on network conditions
    /// and host feedback to minimize latency and optimize quality.
    /// </summary>
    public class AdaptiveFrameManager : IDisposable
    {
        // 성능 추적
        private readonly Stopwatch _performanceTimer = new Stopwatch();
        private long _totalFramesSent = 0;
        private long _lastAckFrameId = 0;
        private long _lastFrameId = 0;
        private long _totalLatency = 0;
        private readonly object _syncLock = new object();

        // GOP 추적 및 키프레임 관리
        private readonly ConcurrentDictionary<long, DateTime> _sentFrames = new ConcurrentDictionary<long, DateTime>();
        private long _lastKeyframeId = 0;
        private DateTime _lastKeyframeSent = DateTime.MinValue;
        private int _framesSinceKeyframe = 0;
        private readonly ConcurrentDictionary<long, bool> _keyframeMap = new ConcurrentDictionary<long, bool>();

        // 네트워크 통계
        private int _currentPing = 0;
        private int _packetLoss = 0;
        private int _hostQueueLength = 0;
        private int _currentBitrate;
        private int _targetFps;
        private int _currentQuality;
        private bool _isRemoteControlActive = false;

        // 적응형 조정 설정
        private int _minBitrate = 500_000;      // 500 Kbps
        private int _maxBitrate = 20_000_000;   // 20 Mbps
        private int _minFps = 5;
        private int _maxFps = 60;
        private int _minQuality = 30;
        private int _maxQuality = 95;
        private int _keyframeInterval = 10;      // 기본 10초마다 키프레임
        private int _maxGopSize = 300;           // 최대 GOP 크기 (프레임 수)

        // 흐름 제어
        private int _pendingFrames = 0;
        private int _maxPendingFrames = 2;
        private readonly System.Timers.Timer _adjustmentTimer;
        private bool _frameThrottling = false;
        private DateTime _lastFrameSentTime = DateTime.MinValue;
        private TimeSpan _minFrameInterval = TimeSpan.Zero;

        // 키프레임 제어
        private bool _forceKeyframe = false;
        private DateTime? _lastKeyframeRequest = null;
        private TimeSpan _minKeyframeInterval = TimeSpan.FromSeconds(2); // 최소 2초 간격으로 키프레임 요청
        private int _keyframeRequestCounter = 0;  // 키프레임 요청 카운터
        private int _consecutiveFrameDrops = 0;   // 연속 프레임 드롭 수

        // 네트워크 상태 이력
        private readonly CircularBuffer<NetworkCondition> _networkHistory = new CircularBuffer<NetworkCondition>(20);
        private bool _recentNetworkDegradation = false;

        public event EventHandler<AdaptiveSettingsChangedEventArgs> SettingsChanged;

        public int TargetFps
        {
            get => _targetFps;
            private set
            {
                if (_targetFps != value)
                {
                    _targetFps = value;
                    _minFrameInterval = TimeSpan.FromSeconds(1.0 / _targetFps);
                    OnSettingsChanged();
                }
            }
        }

        public int CurrentBitrate => _currentBitrate;
        public int CurrentQuality => _currentQuality;
        public int PendingFrames => _pendingFrames;
        public TimeSpan AverageLatency => _totalFramesSent > 0
            ? TimeSpan.FromMilliseconds(_totalLatency / _totalFramesSent)
            : TimeSpan.Zero;

        /// <summary>
        /// 키프레임 강제 생성이 필요한지 여부
        /// </summary>
        public bool ShouldForceKeyframe
        {
            get
            {
                lock (_syncLock)
                {
                    if (!_forceKeyframe)
                        return false;

                    // 최소 간격 체크
                    if (_lastKeyframeRequest.HasValue &&
                        (DateTime.UtcNow - _lastKeyframeRequest.Value) < _minKeyframeInterval)
                    {
                        return false;
                    }

                    // 플래그 초기화 및 시간 기록
                    _forceKeyframe = false;
                    _lastKeyframeRequest = DateTime.UtcNow;
                    EnhancedLogger.Instance.Info("키프레임 생성 요청 (네트워크 상태 악화)");
                    return true;
                }
            }
        }

        /// <summary>
        /// 마지막으로 보낸 키프레임 ID
        /// </summary>
        public long LastKeyframeId => _lastKeyframeId;

        /// <summary>
        /// 마지막 키프레임 이후 프레임 수
        /// </summary>
        public int FramesSinceKeyframe => _framesSinceKeyframe;

        /// <summary>
        /// Creates a new instance of the AdaptiveFrameManager
        /// </summary>
        /// <param name="initialFps">Initial frames per second</param>
        /// <param name="initialQuality">Initial quality (1-100)</param>
        /// <param name="initialBitrate">Initial bitrate in bits per second</param>
        public AdaptiveFrameManager(int initialFps, int initialQuality, int initialBitrate)
        {
            _targetFps = Math.Clamp(initialFps, _minFps, _maxFps);
            _currentQuality = Math.Clamp(initialQuality, _minQuality, _maxQuality);
            _currentBitrate = Math.Clamp(initialBitrate, _minBitrate, _maxBitrate);
            _minFrameInterval = TimeSpan.FromSeconds(1.0 / _targetFps);

            // Setup periodic adjustment timer (adjust every 1 second)
            _adjustmentTimer = new System.Timers.Timer(1000);
            _adjustmentTimer.Elapsed += (s, e) => AdjustSettings();
            _adjustmentTimer.AutoReset = true;
            _adjustmentTimer.Start();

            _performanceTimer.Start();
            EnhancedLogger.Instance.Info(
                $"AdaptiveFrameManager initialized: FPS={_targetFps}, " +
                $"Quality={_currentQuality}, " +
                $"Bitrate={_currentBitrate/1000}kbps");
        }

        /// <summary>
        /// 프레임이 키프레임인지 등록
        /// </summary>
        public void RegisterKeyframe(long frameId)
        {
            lock (_syncLock)
            {
                _keyframeMap[frameId] = true;
                _lastKeyframeId = frameId;
                _lastKeyframeSent = DateTime.UtcNow;
                _framesSinceKeyframe = 0;

                EnhancedLogger.Instance.Info($"키프레임 등록: ID={frameId}");
            }
        }

        /// <summary>
        /// Called when a new frame is about to be sent. Returns true if the frame should be sent,
        /// or false if it should be skipped based on adaptive rate control.
        /// </summary>
        /// <param name="isKeyFrame">프레임이 키프레임인지 여부</param>
        /// <returns>프레임을 전송해야 하면 true, 그렇지 않으면 false</returns>
        public bool BeginFrameSend(bool isKeyFrame = false)
        {
            lock (_syncLock)
            {
                if (_totalFramesSent < 10)
                {
                    _lastFrameSentTime = DateTime.Now;
                    _lastFrameId++;
                    _pendingFrames++;
                    _sentFrames[_lastFrameId] = DateTime.UtcNow;

                    if (isKeyFrame)
                    {
                        RegisterKeyframe(_lastFrameId);
                    }
                    else
                    {
                        _framesSinceKeyframe++;
                    }

                    return true;
                }

                TimeSpan timeSinceLastFrame = DateTime.Now - _lastFrameSentTime;
                if (timeSinceLastFrame < _minFrameInterval)
                {
                    return false;
                }

                if (!isKeyFrame &&
                    (DateTime.UtcNow - _lastKeyframeSent).TotalSeconds > _keyframeInterval ||
                    _framesSinceKeyframe >= _maxGopSize)
                {
                    _forceKeyframe = true;
                    EnhancedLogger.Instance.Info(
                        $"주기적 키프레임 요청: 마지막 키프레임으로부터 " +
                        $"{(DateTime.UtcNow - _lastKeyframeSent).TotalSeconds:F1}초, " +
                        $"프레임 수={_framesSinceKeyframe}");
                    return false;
                }

                bool queueGrowing = _pendingFrames > _pendingFramesLastCheck;
                _pendingFramesLastCheck = _pendingFrames;

                if (!isKeyFrame && _frameThrottling && (_pendingFrames >= _maxPendingFrames || (queueGrowing && _pendingFrames >= _maxPendingFrames / 2)))
                {
                    _consecutiveFrameDrops++;
                    EnhancedLogger.Instance.Debug(
                        $"프레임 제한: 대기={_pendingFrames}, 최대={_maxPendingFrames}, " +
                        $"연속 드롭={_consecutiveFrameDrops}, 큐 증가 중={queueGrowing}");

                    if (_consecutiveFrameDrops >= 10)
                    {
                        _forceKeyframe = true;
                        _consecutiveFrameDrops = 0;
                        EnhancedLogger.Instance.Warning("연속 프레임 드롭으로 인한 키프레임 요청");
                    }

                    return false;
                }

                _lastFrameSentTime = DateTime.Now;
                _lastFrameId++;
                _pendingFrames++;
                _sentFrames[_lastFrameId] = DateTime.UtcNow;
                _consecutiveFrameDrops = 0;

                if (isKeyFrame)
                {
                    RegisterKeyframe(_lastFrameId);
                }
                else
                {
                    _framesSinceKeyframe++;
                }

                if (_lastFrameId % 30 == 0)
                {
                    EnhancedLogger.Instance.Debug(
                        $"프레임 흐름: id={_lastFrameId}, 대기={_pendingFrames}, " +
                        $"응답={_lastAckFrameId}, fps={_targetFps}, 키프레임이후={_framesSinceKeyframe}");
                }

                return true;
            }
        }

        private int _pendingFramesLastCheck = 0;

        /// <summary>
        /// Called when an acknowledgment is received for a sent frame
        /// </summary>
        /// <param name="frameId">The ID of the acknowledged frame</param>
        /// <param name="roundTripTime">The measured round-trip time in milliseconds</param>
        /// <param name="hostQueueLength">The host's queue length for this client</param>
        /// <param name="processingTime">The host's processing time for this frame in microseconds</param>
        public void FrameAcknowledged(long frameId, int roundTripTime, int hostQueueLength, long processingTime)
        {
            lock (_syncLock)
            {
                EnhancedLogger.Instance.Debug(
                    $"프레임 응답 수신: id={frameId}, rtt={roundTripTime}ms, " +
                    $"호스트큐={hostQueueLength}, 처리시간={processingTime/1000.0}ms, 대기={_pendingFrames}");

                // 마지막 응답 ID 업데이트
                if (frameId > _lastAckFrameId)
                {
                    _lastAckFrameId = frameId;
                }

                // 대기 프레임 수 조정
                _pendingFrames = Math.Max(0, _pendingFrames - 1);

                // 네트워크 메트릭 저장
                _currentPing = roundTripTime;
                _totalLatency += roundTripTime;
                _totalFramesSent++;
                _hostQueueLength = hostQueueLength;

                // 현재 네트워크 상태 업데이트
                _networkHistory.Add(new NetworkCondition
                {
                    Timestamp = DateTime.UtcNow,
                    Rtt = roundTripTime,
                    HostQueueLength = hostQueueLength,
                    PacketLoss = _packetLoss
                });

                // 전송 시간 추적 및 제거
                if (_sentFrames.TryRemove(frameId, out DateTime sentTime))
                {
                    TimeSpan latency = DateTime.UtcNow - sentTime;

                    // 총 지연 시간이 너무 길면 네트워크 상태가 좋지 않은 것으로 간주
                    if (latency.TotalMilliseconds > 500)
                    {
                        EnhancedLogger.Instance.Warning(
                            $"높은 프레임 지연: id={frameId}, " +
                            $"지연={latency.TotalMilliseconds:F1}ms, rtt={roundTripTime}ms");

                        // 네트워크 상태 악화 감지
                        _recentNetworkDegradation = true;

                        // 즉시 설정 조정
                        AdjustSettings(true);
                    }
                }

                // 주기적 통계 기록
                if (_totalFramesSent % 30 == 0)
                {
                    EnhancedLogger.Instance.Info(
                        $"프레임 통계: 전송={_totalFramesSent}, " +
                        $"평균 지연={AverageLatency.TotalMilliseconds:F1}ms, " +
                        $"현재 RTT={_currentPing}ms, 대기={_pendingFrames}, " +
                        $"FPS={_targetFps}, 품질={_currentQuality}, " +
                        $"비트레이트={_currentBitrate/1000}kbps");
                }
            }
        }

        /// <summary>
        /// Update packet loss estimate from the network layer
        /// </summary>
        public void UpdatePacketLoss(int percentLoss)
        {
            lock (_syncLock)
            {
                int oldLoss = _packetLoss;
                _packetLoss = percentLoss;

                // 패킷 손실이 급격히 증가하면 패킷 손실 기반 품질 조정 트리거
                if (percentLoss > oldLoss + 10)
                {
                    EnhancedLogger.Instance.Warning($"패킷 손실 증가: {oldLoss}% -> {percentLoss}%");
                    AdjustSettings(true);
                }
            }
        }

        /// <summary>
        /// Set the remote control mode status which affects adaptation strategies
        /// </summary>
        public void SetRemoteControlMode(bool active)
        {
            lock (_syncLock)
            {
                if (_isRemoteControlActive != active)
                {
                    _isRemoteControlActive = active;

                    // 원격 제어 모드 변경 시 빠른 적응
                    if (active)
                    {
                        // 원격 제어는 응답성 우선
                        TargetFps = Math.Min(_maxFps, _targetFps + 10);
                        _maxPendingFrames = 1; // 낮은 지연시간을 위한 더 엄격한 흐름 제어
                        _frameThrottling = true;
                        _keyframeInterval = 5; // 더 빈번한 키프레임 (5초마다)
                    }
                    else
                    {
                        // 뷰잉 모드는 더 여유로운 설정
                        _maxPendingFrames = 2;
                        TargetFps = Math.Max(_minFps, _targetFps - 5);
                        _keyframeInterval = 10; // 일반 키프레임 간격 (10초마다)
                    }

                    // 강제 키프레임 요청
                    _forceKeyframe = true;

                    // 즉시 설정 조정
                    AdjustSettings(true);
                }
            }
        }

        /// <summary>
        /// 호스트로부터 키프레임 요청을 처리합니다.
        /// </summary>
        public void HandleKeyframeRequest()
        {
            lock (_syncLock)
            {
                // 키프레임 요청 카운터 증가
                _keyframeRequestCounter++;

                // 빠른 요청 다수 = 심각한 네트워크 문제
                if (_keyframeRequestCounter >= 3 &&
                    (DateTime.UtcNow - _lastKeyframeRequest.GetValueOrDefault(DateTime.MinValue)).TotalSeconds < 10)
                {
                    EnhancedLogger.Instance.Warning(
                        $"빈번한 키프레임 요청: {_keyframeRequestCounter}회 - 품질 대폭 감소");

                    // 품질과 비트레이트 대폭 감소
                    _currentQuality = Math.Max(_minQuality, _currentQuality / 2);
                    _currentBitrate = Math.Max(_minBitrate, _currentBitrate / 2);

                    // 키프레임 카운터 리셋
                    _keyframeRequestCounter = 0;

                    // 설정 변경 알림
                    OnSettingsChanged();
                }

                // 키프레임 강제 설정
                _forceKeyframe = true;
                _lastKeyframeRequest = DateTime.UtcNow;

                EnhancedLogger.Instance.Info(
                    $"호스트로부터 키프레임 요청 처리 (요청 #{_keyframeRequestCounter})");
            }
        }

        /// <summary>
        /// Periodically adjust settings based on network conditions
        /// </summary>
        private void AdjustSettings(bool immediate = false)
        {
            lock (_syncLock)
            {
                if (!immediate && !_recentNetworkDegradation)
                    return;

                bool changed = false;
                int newFps = _targetFps;
                int newQuality = _currentQuality;
                int newBitrate = _currentBitrate;

                bool criticalNetwork = AnalyzeNetworkCondition(out NetworkSeverity severity);

                if (criticalNetwork || immediate || _recentNetworkDegradation)
                {
                    EnhancedLogger.Instance.Info(
                        $"네트워크 상태: 심각도={severity}, " +
                        $"RTT={_currentPing}ms, 패킷 손실={_packetLoss}%, " +
                        $"호스트 큐={_hostQueueLength}, 대기={_pendingFrames}");
                }

                switch (severity)
                {
                    case NetworkSeverity.Critical:
                        EnhancedLogger.Instance.Warning("심각한 네트워크 상태 감지 - 품질 대폭 저하");

                        _forceKeyframe = true;

                        if (_isRemoteControlActive)
                        {
                            newFps = Math.Max(_minFps + 5, _targetFps / 2);
                            newQuality = Math.Max(_minQuality, _currentQuality / 2);
                            newBitrate = Math.Max(_minBitrate, _currentBitrate / 3);
                        }
                        else
                        {
                            newFps = Math.Max(_minFps, _targetFps / 2);
                            newQuality = Math.Max(_minQuality, _currentQuality / 2);
                            newBitrate = Math.Max(_minBitrate, _currentBitrate / 3);
                        }

                        _frameThrottling = true;
                        _maxPendingFrames = 1;
                        break;

                    case NetworkSeverity.Bad:
                        if (_isRemoteControlActive)
                        {
                            // 원격 제어 우선: FPS 유지, 품질/비트레이트 감소
                            newQuality = Math.Max(_minQuality, _currentQuality - 10);
                            newBitrate = Math.Max(_minBitrate, (int)(_currentBitrate * 0.7));

                            // 심각한 경우만 FPS 감소
                            if (_pendingFrames > _maxPendingFrames * 1.5)
                            {
                                newFps = Math.Max(_minFps + 5, _targetFps - 5);
                            }
                        }
                        else
                        {
                            newFps = Math.Max(_minFps, _targetFps - 5);
                            newQuality = Math.Max(_minQuality, _currentQuality - 8);
                            newBitrate = Math.Max(_minBitrate, (int)(_currentBitrate * 0.7));
                        }

                        _frameThrottling = true;
                        _maxPendingFrames = Math.Max(1, _maxPendingFrames - 1);
                        break;

                    case NetworkSeverity.Moderate:
                        if (_isRemoteControlActive)
                        {
                            // 원격 제어는 약간 품질 조정만
                            newQuality = Math.Max(_minQuality, _currentQuality - 5);
                            newBitrate = Math.Max(_minBitrate, (int)(_currentBitrate * 0.85));
                        }
                        else
                        {
                            newFps = Math.Max(_minFps, _targetFps - 2);
                            newQuality = Math.Max(_minQuality, _currentQuality - 3);
                            newBitrate = Math.Max(_minBitrate, (int)(_currentBitrate * 0.9));
                        }

                        _frameThrottling = true;
                        break;

                    case NetworkSeverity.Good:
                        // 좋은 상태에서 점진적 품질 향상
                        if (_isRemoteControlActive)
                        {
                            // 원격 제어는 fps 우선
                            newFps = Math.Min(_maxFps, _targetFps + 2);
                            newQuality = Math.Min(_maxQuality, _currentQuality + 1);
                            newBitrate = Math.Min(_maxBitrate, (int)(_currentBitrate * 1.05));
                        }
                        else
                        {
                            // 일반 화면 공유는 품질 우선
                            newFps = Math.Min(_maxFps, _targetFps + 1);
                            newQuality = Math.Min(_maxQuality, _currentQuality + 3);
                            newBitrate = Math.Min(_maxBitrate, (int)(_currentBitrate * 1.05));
                        }

                        // 지속적으로 좋은 상태면 프레임 제한 완화
                        if (_pendingFrames < _maxPendingFrames / 2)
                        {
                            _maxPendingFrames = Math.Min(3, _maxPendingFrames + 1);
                        }
                        break;

                    case NetworkSeverity.Excellent:
                        // 최상의 상태에서 빠르게 품질 향상
                        if (_isRemoteControlActive)
                        {
                            newFps = Math.Min(_maxFps, _targetFps + 5);
                            newQuality = Math.Min(_maxQuality, _currentQuality + 5);
                            newBitrate = Math.Min(_maxBitrate, (int)(_currentBitrate * 1.1));
                        }
                        else
                        {
                            newFps = Math.Min(_maxFps, _targetFps + 2);
                            newQuality = Math.Min(_maxQuality, _currentQuality + 8);
                            newBitrate = Math.Min(_maxBitrate, (int)(_currentBitrate * 1.1));
                        }

                        _frameThrottling = false;
                        _maxPendingFrames = 3;
                        break;
                }

                // 급격한 변화 방지 - 변화량 제한
                if (newFps != _targetFps)
                {
                    // 25% 이상의 급격한 변화 방지
                    if (newFps < _targetFps * 0.75) newFps = (int)(_targetFps * 0.75);
                    if (newFps > _targetFps * 1.25) newFps = (int)(_targetFps * 1.25);

                    TargetFps = newFps;
                    changed = true;
                }

                if (newQuality != _currentQuality)
                {
                    // 품질은 한 번에 최대 20% 변경
                    if (newQuality < _currentQuality * 0.8) newQuality = (int)(_currentQuality * 0.8);
                    if (newQuality > _currentQuality * 1.2) newQuality = (int)(_currentQuality * 1.2);

                    _currentQuality = newQuality;
                    changed = true;
                }

                if (newBitrate != _currentBitrate)
                {
                    // 비트레이트는 한 번에 최대 30% 변경
                    if (newBitrate < _currentBitrate * 0.7) newBitrate = (int)(_currentBitrate * 0.7);
                    if (newBitrate > _currentBitrate * 1.3) newBitrate = (int)(_currentBitrate * 1.3);

                    _currentBitrate = newBitrate;
                    changed = true;
                }

                if (changed)
                {
                    EnhancedLogger.Instance.Info(
                        $"적응형 설정 조정: FPS={_targetFps}, 품질={_currentQuality}, " +
                        $"비트레이트={_currentBitrate/1000}kbps, 네트워크 심각도={severity}");
                    OnSettingsChanged();
                }

                _recentNetworkDegradation = false;
            }
        }

        /// <summary>
        /// 네트워크 상태 분석
        /// </summary>
        private bool AnalyzeNetworkCondition(out NetworkSeverity severity)
        {
            int recentCount = Math.Min(5, _networkHistory.Count);
            if (recentCount == 0)
            {
                severity = NetworkSeverity.Good;
                return false;
            }

            int totalRtt = 0;
            int totalPacketLoss = 0;
            int totalQueueLength = 0;
            int highRttCount = 0;
            int highPacketLossCount = 0;
            int queueGrowthTrend = 0;
            int prevQueueLength = -1;

            for (int i = 0; i < recentCount; i++)
            {
                var condition = _networkHistory[i];
                totalRtt += condition.Rtt;
                totalPacketLoss += condition.PacketLoss;
                totalQueueLength += condition.HostQueueLength;

                if (condition.Rtt > 150) highRttCount++;
                if (condition.PacketLoss > 5) highPacketLossCount++;

                // 큐 증가 추세 확인
                if (prevQueueLength >= 0)
                {
                    if (condition.HostQueueLength > prevQueueLength)
                        queueGrowthTrend++;
                    else if (condition.HostQueueLength < prevQueueLength)
                        queueGrowthTrend--;
                }

                prevQueueLength = condition.HostQueueLength;
            }

            int avgRtt = totalRtt / recentCount;
            int avgPacketLoss = totalPacketLoss / recentCount;
            int avgQueueLength = totalQueueLength / recentCount;
            bool queueIncreasing = queueGrowthTrend > 0;

            bool highBitrate = _currentBitrate > 8_000_000;
            bool highFps = _targetFps > 30;

            // 심각도 결정에 큐 증가 추세 반영
            if ((avgRtt > 200 && avgPacketLoss > 10) ||
                avgPacketLoss > 15 ||
                avgRtt > 300 ||
                (highBitrate && avgPacketLoss > 8) ||
                _pendingFrames > _maxPendingFrames * 2 ||
                (avgQueueLength > 3 && queueIncreasing) ||
                avgQueueLength > 5)
            {
                severity = NetworkSeverity.Critical;
                return true;
            }
            else if ((avgRtt > 150 && avgPacketLoss > 5) ||
                     avgPacketLoss > 8 ||
                     avgRtt > 200 ||
                     (highBitrate && avgPacketLoss > 5) ||
                     _pendingFrames > _maxPendingFrames ||
                     (avgQueueLength > 2 && queueIncreasing) ||
                     avgQueueLength > 3)
            {
                severity = NetworkSeverity.Bad;
                return true;
            }
            else if ((avgRtt > 100 && avgPacketLoss > 2) ||
                     avgPacketLoss > 5 ||
                     avgRtt > 150 ||
                     _pendingFrames > _maxPendingFrames * 0.8 ||
                     (avgQueueLength > 1 && queueIncreasing) ||
                     avgQueueLength > 2)
            {
                severity = NetworkSeverity.Moderate;
                return false;
            }
            else if (avgRtt < 50 && avgPacketLoss < 1 && _pendingFrames == 0 && avgQueueLength == 0)
            {
                severity = NetworkSeverity.Excellent;
                return false;
            }
            else
            {
                severity = NetworkSeverity.Good;
                return false;
            }
        }

        private void OnSettingsChanged()
        {
            var args = new AdaptiveSettingsChangedEventArgs
            {
                TargetFps = _targetFps,
                Quality = _currentQuality,
                Bitrate = _currentBitrate
            };

            EnhancedLogger.Instance.Info(
                $"적응형 설정 변경: FPS={_targetFps}, 품질={_currentQuality}, " +
                $"비트레이트={_currentBitrate/1000}kbps");
            SettingsChanged?.Invoke(this, args);
        }

        public void Dispose()
        {
            _adjustmentTimer?.Stop();
            _adjustmentTimer?.Dispose();
            _performanceTimer.Stop();
        }
    }

    /// <summary>
    /// 네트워크 상태 심각도
    /// </summary>
    public enum NetworkSeverity
    {
        Excellent,  // 최상
        Good,       // 좋음
        Moderate,   // 보통
        Bad,        // 나쁨
        Critical    // 심각
    }

    /// <summary>
    /// 네트워크 상태 정보
    /// </summary>
    public class NetworkCondition
    {
        public DateTime Timestamp { get; set; }
        public int Rtt { get; set; }
        public int PacketLoss { get; set; }
        public int HostQueueLength { get; set; }
    }

    /// <summary>
    /// 원형 버퍼 구현
    /// </summary>
    public class CircularBuffer<T>
    {
        private readonly T[] _buffer;
        private int _start;
        private int _end;
        private int _count;

        public CircularBuffer(int capacity)
        {
            _buffer = new T[capacity];
            _start = 0;
            _end = 0;
            _count = 0;
        }

        public void Add(T item)
        {
            if (_count == _buffer.Length)
            {
                _buffer[_end] = item;
                _end = (_end + 1) % _buffer.Length;
                _start = (_start + 1) % _buffer.Length;
            }
            else
            {
                _buffer[_end] = item;
                _end = (_end + 1) % _buffer.Length;
                _count++;
            }
        }

        public T this[int index]
        {
            get
            {
                if (index < 0 || index >= _count)
                    throw new IndexOutOfRangeException();

                return _buffer[(_start + index) % _buffer.Length];
            }
        }

        public int Count => _count;
    }

    public class AdaptiveSettingsChangedEventArgs : EventArgs
    {
        public int TargetFps { get; set; }
        public int Quality { get; set; }
        public int Bitrate { get; set; }
    }
}