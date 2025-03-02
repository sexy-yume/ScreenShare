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
            FileLogger.Instance.WriteInfo($"AdaptiveFrameManager initialized: FPS={_targetFps}, Quality={_currentQuality}, Bitrate={_currentBitrate/1000}kbps");
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

                // Process network information to determine adjustment strategy
                bool poorNetwork = _currentPing > 100 || _packetLoss > 5 || _pendingFrames > _maxPendingFrames;
                bool goodNetwork = _currentPing < 50 && _packetLoss < 2 && _pendingFrames == 0;

                // Adapt settings based on network conditions
                if (poorNetwork)
                {
                    // Reduce quality to maintain responsiveness
                    if (_isRemoteControlActive)
                    {
                        // In remote control mode, maintain FPS but reduce quality/bitrate
                        newQuality = Math.Max(_minQuality, _currentQuality - 5);
                        newBitrate = Math.Max(_minBitrate, (int)(_currentBitrate * 0.8));
                    }
                    else
                    {
                        // In viewing mode, reduce both FPS and quality
                        newFps = Math.Max(_minFps, _targetFps - 2);
                        newQuality = Math.Max(_minQuality, _currentQuality - 3);
                        newBitrate = Math.Max(_minBitrate, (int)(_currentBitrate * 0.9));
                    }

                    // Increase flow control tightness
                    _frameThrottling = true;
                }
                else if (goodNetwork && (_isRemoteControlActive || immediate))
                {
                    // Gradually improve quality on good network
                    if (_isRemoteControlActive)
                    {
                        // In remote control, prefer higher FPS
                        newFps = Math.Min(_maxFps, _targetFps + 2);
                        newBitrate = Math.Min(_maxBitrate, (int)(_currentBitrate * 1.1));
                    }
                    else
                    {
                        // In viewing mode, prefer higher quality
                        newQuality = Math.Min(_maxQuality, _currentQuality + 2);
                        newBitrate = Math.Min(_maxBitrate, (int)(_currentBitrate * 1.05));
                    }
                }

                // Apply changes if needed
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

                // Notify if settings changed
                if (changed)
                {
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

            FileLogger.Instance.WriteInfo($"Adaptive settings changed: FPS={_targetFps}, Quality={_currentQuality}, Bitrate={_currentBitrate/1000}kbps");
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