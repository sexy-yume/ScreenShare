using System;
using System.Diagnostics;
using System.Threading;
using ScreenShare.Common.Utils;

namespace ScreenShare.Client.Network
{
    /// <summary>
    /// Manages adaptive frame rate and bitrate control based on network conditions
    /// and host feedback to minimize latency and optimize quality.
    /// </summary>
    public class AdaptiveFrameManager
    {
        // Performance tracking
        private readonly Stopwatch _performanceTimer = new Stopwatch();
        private long _totalFramesSent = 0;
        private long _lastAckFrameId = 0;
        private long _lastFrameId = 0;
        private long _totalLatency = 0;
        private readonly object _syncLock = new object();

        // Network statistics
        private int _currentPing = 0;
        private int _packetLoss = 0;
        private int _currentBitrate;
        private int _targetFps;
        private int _currentQuality;
        private bool _isRemoteControlActive = false;

        // Rate adaptation settings
        private int _minBitrate = 500_000;      // 500 Kbps
        private int _maxBitrate = 20_000_000;   // 20 Mbps
        private int _minFps = 5;
        private int _maxFps = 60;
        private int _minQuality = 30;
        private int _maxQuality = 95;

        // Flow control
        private int _pendingFrames = 0;
        private int _maxPendingFrames = 2;
        private readonly System.Timers.Timer _adjustmentTimer;
        private bool _frameThrottling = false;
        private DateTime _lastFrameSentTime = DateTime.MinValue;
        private TimeSpan _minFrameInterval = TimeSpan.Zero;

        // Keyframe control
        private bool _forceKeyframe = false;
        private DateTime? _lastKeyframeRequest = null;
        private TimeSpan _minKeyframeInterval = TimeSpan.FromSeconds(5); // 최소 5초 간격으로 키프레임 요청

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

            // Setup periodic adjustment timer (adjust every 2 seconds)
            _adjustmentTimer = new System.Timers.Timer(2000);
            _adjustmentTimer.Elapsed += (s, e) => AdjustSettings();
            _adjustmentTimer.AutoReset = true;
            _adjustmentTimer.Start();

            _performanceTimer.Start();
            EnhancedLogger.Instance.Info($"AdaptiveFrameManager initialized: FPS={_targetFps}, Quality={_currentQuality}, Bitrate={_currentBitrate/1000}kbps");
        }

        /// <summary>
        /// Called when a new frame is about to be sent. Returns true if the frame should be sent,
        /// or false if it should be skipped based on adaptive rate control.
        /// </summary>
        public bool BeginFrameSend()
        {
            lock (_syncLock)
            {
                // Initially allow frames to flow without restriction (first 10 frames)
                if (_totalFramesSent < 10)
                {
                    _lastFrameSentTime = DateTime.Now;
                    _lastFrameId++;
                    _pendingFrames++;
                    return true;
                }

                // After initial frames, apply normal flow control
                if (_frameThrottling && _pendingFrames >= _maxPendingFrames)
                {
                    // Skip frame if we have too many pending frames
                    EnhancedLogger.Instance.Debug($"Throttling frame: pending={_pendingFrames}, max={_maxPendingFrames}");
                    return false;
                }

                // Enforce frame rate limit
                TimeSpan timeSinceLastFrame = DateTime.Now - _lastFrameSentTime;
                if (timeSinceLastFrame < _minFrameInterval)
                {
                    // Too soon for next frame
                    return false;
                }

                // Track metrics
                _lastFrameSentTime = DateTime.Now;
                _lastFrameId++;
                _pendingFrames++;

                // Log periodically
                if (_lastFrameId % 30 == 0)
                {
                    EnhancedLogger.Instance.Debug($"Frame flow: id={_lastFrameId}, pending={_pendingFrames}, ack={_lastAckFrameId}, fps={_targetFps}");
                }

                return true;
            }
        }

        /// <summary>
        /// Called when an acknowledgment is received for a sent frame
        /// </summary>
        /// <param name="frameId">The ID of the acknowledged frame</param>
        /// <param name="roundTripTime">The measured round-trip time in milliseconds</param>
        public void FrameAcknowledged(long frameId, int roundTripTime)
        {
            lock (_syncLock)
            {
                EnhancedLogger.Instance.Debug($"Frame ack received: id={frameId}, rtt={roundTripTime}ms, pending={_pendingFrames}");

                if (frameId > _lastAckFrameId)
                {
                    _lastAckFrameId = frameId;
                }

                _pendingFrames = Math.Max(0, _pendingFrames - 1);
                _currentPing = roundTripTime;
                _totalLatency += roundTripTime;
                _totalFramesSent++;

                // Log statistics periodically
                if (_totalFramesSent % 30 == 0)
                {
                    EnhancedLogger.Instance.Info(
                        $"Frame statistics: Sent={_totalFramesSent}, Avg Latency={AverageLatency.TotalMilliseconds:F1}ms, " +
                        $"Current RTT={_currentPing}ms, Pending={_pendingFrames}, " +
                        $"FPS={_targetFps}, Quality={_currentQuality}, Bitrate={_currentBitrate/1000}kbps");
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
                _packetLoss = percentLoss;
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

                    // Quick adaptation for remote control mode changes
                    if (active)
                    {
                        // Favor responsiveness for remote control
                        TargetFps = Math.Min(_maxFps, _targetFps + 10);
                        _maxPendingFrames = 1; // Tighter flow control for lower latency
                        _frameThrottling = true;
                    }
                    else
                    {
                        // Return to more relaxed settings for viewing only
                        _maxPendingFrames = 2;
                        TargetFps = Math.Max(_minFps, _targetFps - 5);
                    }

                    AdjustSettings(true); // Force immediate adjustment
                }
            }
        }

        /// <summary>
        /// Periodically adjust settings based on network conditions
        /// </summary>
        private void AdjustSettings(bool immediate = false)
        {
            lock (_syncLock)
            {
                bool changed = false;
                int newFps = _targetFps;
                int newQuality = _currentQuality;
                int newBitrate = _currentBitrate;

                // 네트워크 상태 분석 향상
                bool criticalNetwork = _currentPing > 200 || _packetLoss > 15 || _pendingFrames > _maxPendingFrames * 2;
                bool poorNetwork = _currentPing > 100 || _packetLoss > 5 || _pendingFrames > _maxPendingFrames;
                bool goodNetwork = _currentPing < 50 && _packetLoss < 2 && _pendingFrames == 0;

                // 네트워크 상태에 따른 설정 조정
                if (criticalNetwork)
                {
                    // 심각한 네트워크 상태일 때 더 적극적으로 품질 저하
                    EnhancedLogger.Instance.Warning("심각한 네트워크 상태 감지 - 품질 대폭 저하");

                    // 키프레임 강제 요청
                    _forceKeyframe = true;

                    if (_isRemoteControlActive)
                    {
                        // 원격 제어 모드에서는 최소 프레임율 유지하면서 품질/비트레이트 대폭 감소
                        newFps = Math.Max(_minFps + 5, _targetFps / 2);  // FPS 절반으로 줄이되 최소 상호작용 속도 유지
                        newQuality = Math.Max(_minQuality, _currentQuality / 2);  // 품질 절반으로
                        newBitrate = Math.Max(_minBitrate, _currentBitrate / 3);  // 비트레이트 66% 감소
                    }
                    else
                    {
                        // 뷰잉 모드에서는 모든 설정 대폭 감소
                        newFps = Math.Max(_minFps, _targetFps / 2);
                        newQuality = Math.Max(_minQuality, _currentQuality / 2);
                        newBitrate = Math.Max(_minBitrate, _currentBitrate / 3);
                    }

                    // 흐름 제어 강화
                    _frameThrottling = true;
                    _maxPendingFrames = 1;
                }
                else if (poorNetwork)
                {
                    // 좋지 않은 네트워크에서 더 적극적인 품질 조정
                    if (_isRemoteControlActive)
                    {
                        // 원격 제어 모드에서는 FPS 유지하면서 품질/비트레이트 적극 감소
                        newQuality = Math.Max(_minQuality, _currentQuality - 10);
                        newBitrate = Math.Max(_minBitrate, (int)(_currentBitrate * 0.7));
                    }
                    else
                    {
                        // 뷰잉 모드에서는 FPS와 품질 모두 적극 감소
                        newFps = Math.Max(_minFps, _targetFps - 5);
                        newQuality = Math.Max(_minQuality, _currentQuality - 8);
                        newBitrate = Math.Max(_minBitrate, (int)(_currentBitrate * 0.7));
                    }

                    // 흐름 제어 강화
                    _frameThrottling = true;
                    _maxPendingFrames = Math.Max(1, _maxPendingFrames - 1);
                }
                else if (goodNetwork && (_isRemoteControlActive || immediate))
                {
                    // 좋은 네트워크에서 점진적 품질 향상
                    if (_isRemoteControlActive)
                    {
                        // 원격 제어 모드에서는 높은 FPS 선호
                        newFps = Math.Min(_maxFps, _targetFps + 2);
                        newQuality = Math.Min(_maxQuality, _currentQuality + 1);
                        newBitrate = Math.Min(_maxBitrate, (int)(_currentBitrate * 1.1));
                    }
                    else
                    {
                        // 뷰잉 모드에서는 높은 품질 선호
                        newFps = Math.Min(_maxFps, _targetFps + 1);
                        newQuality = Math.Min(_maxQuality, _currentQuality + 3);
                        newBitrate = Math.Min(_maxBitrate, (int)(_currentBitrate * 1.05));
                    }

                    // 흐름 제어 완화
                    _maxPendingFrames = Math.Min(3, _maxPendingFrames + 1);
                }

                // 필요시 변경사항 적용
                if (newFps != _targetFps)
                {
                    TargetFps = newFps;
                    changed = true;
                }

                if (newQuality != _currentQuality)
                {
                    _currentQuality = newQuality;
                    changed = true;
                }

                if (newBitrate != _currentBitrate)
                {
                    _currentBitrate = newBitrate;
                    changed = true;
                }

                // 설정 변경사항 통지
                if (changed)
                {
                    EnhancedLogger.Instance.Info($"적응형 설정 조정: FPS={_targetFps}, 품질={_currentQuality}, 비트레이트={_currentBitrate/1000}kbps");
                    OnSettingsChanged();
                }
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

            EnhancedLogger.Instance.Info($"Adaptive settings changed: FPS={_targetFps}, Quality={_currentQuality}, Bitrate={_currentBitrate/1000}kbps");
            SettingsChanged?.Invoke(this, args);
        }

        public void Dispose()
        {
            _adjustmentTimer?.Stop();
            _adjustmentTimer?.Dispose();
            _performanceTimer.Stop();
        }
    }

    public class AdaptiveSettingsChangedEventArgs : EventArgs
    {
        public int TargetFps { get; set; }
        public int Quality { get; set; }
        public int Bitrate { get; set; }
    }
}