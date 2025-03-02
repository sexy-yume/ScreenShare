using System;
using System.Threading;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using System.Linq;
using ScreenShare.Common.Models;
using ScreenShare.Common.Utils;
using ScreenShare.Host.Decoder;
using ScreenShare.Host.Network;

namespace ScreenShare.Host.Processing
{
    /// <summary>
    /// Manages asynchronous frame processing to ensure UI responsiveness and
    /// prevent frame processing bottlenecks.
    /// </summary>
    public class FrameProcessingManager : IDisposable
    {
        // Components
        private NetworkServer _networkServer;
        private FFmpegDecoder _decoder;

        // Queue and processing state
        private readonly ConcurrentDictionary<int, ConcurrentQueue<FrameData>> _frameQueues;
        private readonly ConcurrentDictionary<int, bool> _processingFlags;
        private readonly ConcurrentDictionary<int, DateTime> _lastFrameTimeByClient;
        private readonly ConcurrentDictionary<int, Stopwatch> _clientProcessingTimers;
        private readonly ManualResetEvent _shutdownEvent = new ManualResetEvent(false);

        // Configuration settings
        private bool _dropOutdatedFrames = true;
        private TimeSpan _frameExpirationTime = TimeSpan.FromSeconds(1);
        private int _maxQueueSizePerClient = 5;
        private bool _useParallelProcessing = true;

        // Performance metrics
        private readonly Stopwatch _uptimeTimer = new Stopwatch();
        private long _totalFramesReceived = 0;
        private long _totalFramesProcessed = 0;
        private long _totalFramesDropped = 0;
        private long _totalProcessingTime = 0;
        private readonly TimeSpan _reportInterval = TimeSpan.FromSeconds(10);
        private DateTime _lastReportTime = DateTime.MinValue;

        // GOP(Group of Pictures) 트래킹 및 키프레임 관리
        private ConcurrentDictionary<int, GopTracker> _gopTrackers = new ConcurrentDictionary<int, GopTracker>();
        private ConcurrentDictionary<int, int> _decodingErrorCounters = new ConcurrentDictionary<int, int>();
        private int _maxDecodeErrorsBeforeReset = 3;

        // 통계 로깅
        private FrameStatisticsLogger _frameStats;

        // Events
        public event EventHandler<FrameProcessedEventArgs> FrameProcessed;
        public event EventHandler<ProcessingMetricsEventArgs> MetricsUpdated;

        public FrameProcessingManager(NetworkServer networkServer, FFmpegDecoder decoder)
        {
            _networkServer = networkServer;
            _decoder = decoder;

            _frameQueues = new ConcurrentDictionary<int, ConcurrentQueue<FrameData>>();
            _processingFlags = new ConcurrentDictionary<int, bool>();
            _lastFrameTimeByClient = new ConcurrentDictionary<int, DateTime>();
            _clientProcessingTimers = new ConcurrentDictionary<int, Stopwatch>();

            // 프레임 통계 로거 초기화
            _frameStats = new FrameStatisticsLogger("Processing");

            // Subscribe to network events
            _networkServer.ScreenDataReceived += OnScreenDataReceived;

            _uptimeTimer.Start();

            EnhancedLogger.Instance.Info("Frame processing manager initialized");
        }

        /// <summary>
        /// Configure frame processing options
        /// </summary>
        public void Configure(
            bool dropOutdatedFrames = true,
            int maxQueueSize = 5,
            int frameExpirationMs = 1000,
            bool useParallelProcessing = true)
        {
            _dropOutdatedFrames = dropOutdatedFrames;
            _maxQueueSizePerClient = maxQueueSize;
            _frameExpirationTime = TimeSpan.FromMilliseconds(frameExpirationMs);
            _useParallelProcessing = useParallelProcessing;

            EnhancedLogger.Instance.Info(
                $"Processing configuration: DropOutdated={_dropOutdatedFrames}, " +
                $"MaxQueue={_maxQueueSizePerClient}, " +
                $"ExpirationMs={frameExpirationMs}, " +
                $"Parallel={_useParallelProcessing}");
        }

        /// <summary>
        /// Called when a new frame data packet is received from the network
        /// </summary>
        private void OnScreenDataReceived(object sender, ScreenDataEventArgs e)
        {
            if (e == null || e.ScreenData == null)
                return;

            Interlocked.Increment(ref _totalFramesReceived);

            // GOP 트래커 가져오기
            var gopTracker = _gopTrackers.GetOrAdd(e.ClientNumber, _ => new GopTracker());

            // GOP 트래킹 업데이트
            if (e.IsKeyFrame)
            {
                EnhancedLogger.Instance.Debug($"클라이언트 {e.ClientNumber}의 키프레임 수신, frameId={e.FrameId}");
                gopTracker.RegisterKeyframe(e.FrameId);
                _frameStats.LogFrameReceived(e.ClientNumber, e.FrameId, true, e.ScreenData.Length);
            }
            else
            {
                gopTracker.RegisterFrame();
                _frameStats.LogFrameReceived(e.ClientNumber, e.FrameId, false, e.ScreenData.Length);
            }

            // Get or create queue for this client
            var queue = _frameQueues.GetOrAdd(e.ClientNumber, _ => new ConcurrentQueue<FrameData>());

            // Create frame data object
            var frameData = new FrameData
            {
                ClientNumber = e.ClientNumber,
                ScreenData = e.ScreenData,
                Width = e.Width,
                Height = e.Height,
                FrameId = e.FrameId,
                ReceivedTime = DateTime.UtcNow,
                IsKeyFrame = e.IsKeyFrame,
                LastKeyframeId = gopTracker.LastKeyframeId
            };

            // Keep track of the last frame time
            _lastFrameTimeByClient[e.ClientNumber] = DateTime.UtcNow;

            // 큐가 너무 커진 경우 키프레임 인지도를 고려한 프레임 관리
            if (queue.Count >= _maxQueueSizePerClient)
            {
                if (_dropOutdatedFrames)
                {
                    // 키프레임을 보존하는 더 현명한 삭제 전략 적용
                    var queuedFrames = queue.ToList();
                    var newQueue = new ConcurrentQueue<FrameData>();
                    int droppedCount = 0;

                    // 먼저 키프레임 식별 (반드시 유지)
                    var keyframes = queuedFrames.Where(f => f.IsKeyFrame).ToList();

                    // 키프레임이 없으면 가장 최근 프레임만 유지
                    if (keyframes.Count == 0)
                    {
                        // 가장 최근 절반 유지, 오래된 절반 삭제
                        var sortedFrames = queuedFrames.OrderByDescending(f => f.ReceivedTime).ToList();
                        int toKeep = Math.Max(1, sortedFrames.Count / 2);

                        for (int i = 0; i < sortedFrames.Count; i++)
                        {
                            if (i < toKeep)
                            {
                                newQueue.Enqueue(sortedFrames[i]);
                            }
                            else
                            {
                                droppedCount++;
                                _frameStats.LogFrameDropped(sortedFrames[i].ClientNumber, sortedFrames[i].FrameId,
                                    sortedFrames[i].IsKeyFrame, "큐 오버플로우");
                            }
                        }
                    }
                    else
                    {
                        // 가장 최근 키프레임과 그 의존 프레임 유지
                        var latestKeyframe = keyframes.OrderByDescending(f => f.FrameId).First();

                        // 가장 최근 키프레임은 항상 유지
                        newQueue.Enqueue(latestKeyframe);

                        // 최신 키프레임에 의존하는 프레임 유지
                        foreach (var frame in queuedFrames.Where(f => !f.IsKeyFrame && f.FrameId > latestKeyframe.FrameId))
                        {
                            newQueue.Enqueue(frame);
                        }

                        // 나머지 프레임 폐기
                        droppedCount = queuedFrames.Count - newQueue.Count;

                        // 삭제된 프레임들 로깅
                        foreach (var frame in queuedFrames)
                        {
                            if (!newQueue.Contains(frame))
                            {
                                _frameStats.LogFrameDropped(frame.ClientNumber, frame.FrameId,
                                    frame.IsKeyFrame, "키프레임 의존성 따른 폐기");
                            }
                        }
                    }

                    // 큐 교체
                    _frameQueues[e.ClientNumber] = newQueue;

                    // 삭제된 프레임 수 업데이트
                    Interlocked.Add(ref _totalFramesDropped, droppedCount);

                    if (droppedCount > 0)
                    {
                        EnhancedLogger.Instance.Debug($"스마트 프레임 삭제: 클라이언트 {e.ClientNumber}의 {droppedCount}개 프레임 제거 (키프레임 보존)");
                    }
                }
            }

            // Add new frame to queue
            queue.Enqueue(frameData);

            // Begin processing if not already in progress
            ProcessNextFrame(e.ClientNumber);
        }

        /// <summary>
        /// Start or continue processing frames for a client
        /// </summary>
        private void ProcessNextFrame(int clientNumber)
        {
            // Check if we're already processing for this client
            if (_processingFlags.TryGetValue(clientNumber, out bool isProcessing) && isProcessing)
                return;

            // Set processing flag
            _processingFlags[clientNumber] = true;

            // Use thread pool for processing
            if (_useParallelProcessing)
            {
                Task.Run(() => ProcessFramesForClient(clientNumber));
            }
            else
            {
                ProcessFramesForClient(clientNumber);
            }
        }

        /// <summary>
        /// Process frames for a specific client
        /// </summary>
        private void ProcessFramesForClient(int clientNumber)
        {
            try
            {
                // Make sure we have a queue for this client
                if (!_frameQueues.TryGetValue(clientNumber, out var queue))
                {
                    _processingFlags[clientNumber] = false;
                    return;
                }

                // Get processing timer for this client
                var timer = _clientProcessingTimers.GetOrAdd(clientNumber, _ => new Stopwatch());

                // Process frames from the queue
                int framesProcessed = 0;

                while (!_shutdownEvent.WaitOne(0) && queue.TryDequeue(out var frame))
                {
                    framesProcessed++;

                    // 클라이언트의 GOP 트래커 가져오기
                    var gopTracker = _gopTrackers.GetOrAdd(clientNumber, _ => new GopTracker());

                    // 키프레임을 기다리는 중이면 키프레임이 아닌 프레임은 건너뛰기
                    if (gopTracker.WaitingForKeyframe && !frame.IsKeyFrame)
                    {
                        EnhancedLogger.Instance.Debug($"클라이언트 {clientNumber}의 프레임 {frame.FrameId} 건너뛰기 - 키프레임 대기 중");
                        Interlocked.Increment(ref _totalFramesDropped);
                        _frameStats.LogFrameDropped(clientNumber, frame.FrameId, false, "키프레임 대기 중");
                        continue;
                    }

                    // Check if frame is too old to process
                    if (_dropOutdatedFrames && (DateTime.UtcNow - frame.ReceivedTime) > _frameExpirationTime)
                    {
                        EnhancedLogger.Instance.Debug($"클라이언트 {clientNumber}의 오래된 프레임 {frame.FrameId} 폐기, 경과시간={(DateTime.UtcNow - frame.ReceivedTime).TotalMilliseconds:F0}ms");
                        Interlocked.Increment(ref _totalFramesDropped);
                        _frameStats.LogFrameDropped(clientNumber, frame.FrameId, frame.IsKeyFrame, "만료됨");
                        continue;
                    }

                    Bitmap decodedFrame = null;
                    long startTicks = Stopwatch.GetTimestamp();

                    try
                    {
                        // Track processing time for this client
                        timer.Restart();

                        // Process and decode frame
                        _networkServer.BeginFrameProcessing(clientNumber, frame.FrameId);

                        // 키프레임은 자세히 로깅
                        if (frame.IsKeyFrame)
                        {
                            EnhancedLogger.Instance.Info($"클라이언트 {clientNumber}의 키프레임 {frame.FrameId} 디코딩 중, 크기={frame.ScreenData.Length}");
                        }
                        else
                        {
                            EnhancedLogger.Instance.Debug($"클라이언트 {clientNumber}의 프레임 {frame.FrameId} 디코딩 중, 크기={frame.ScreenData.Length}, 키프레임={frame.LastKeyframeId}");
                        }

                        decodedFrame = _decoder.DecodeFrame(frame.ScreenData, frame.Width, frame.Height, frame.FrameId);

                        if (decodedFrame != null)
                        {
                            // Successful decode
                            EnhancedLogger.Instance.Debug($"클라이언트 {clientNumber}의 프레임 {frame.FrameId} 디코딩 성공");

                            // 성공 시 오류 카운터 초기화
                            _decodingErrorCounters[clientNumber] = 0;

                            // 대기 중이던 키프레임이면 대기 플래그 제거
                            if (frame.IsKeyFrame && gopTracker.WaitingForKeyframe)
                            {
                                gopTracker.WaitingForKeyframe = false;
                            }

                            // Add call to our debug method
                            LogFrameDebugInfo(decodedFrame, clientNumber);

                            // 통계 기록
                            timer.Stop();
                            _frameStats.LogDecodingSuccess(clientNumber, frame.FrameId, frame.IsKeyFrame, timer.Elapsed.TotalMilliseconds);

                            Interlocked.Increment(ref _totalFramesProcessed);

                            // Notify listeners
                            FrameProcessed?.Invoke(this, new FrameProcessedEventArgs
                            {
                                ClientNumber = clientNumber,
                                Bitmap = decodedFrame,
                                FrameId = frame.FrameId,
                                ProcessingTime = timer.Elapsed,
                                IsKeyFrame = frame.IsKeyFrame
                            });
                        }
                        else
                        {
                            EnhancedLogger.Instance.Warning($"클라이언트 {clientNumber}의 프레임 {frame.FrameId} 디코딩 실패");

                            // 오류 카운터 증가
                            int errorCount = _decodingErrorCounters.AddOrUpdate(clientNumber, 1, (k, v) => v + 1);

                            // 통계 기록
                            _frameStats.LogDecodingFailure(clientNumber, frame.FrameId, frame.IsKeyFrame);

                            // 오류가 너무 많으면 키프레임 요청
                            if (errorCount >= _maxDecodeErrorsBeforeReset)
                            {
                                EnhancedLogger.Instance.Warning($"클라이언트 {clientNumber} 디코딩 오류 과다 - 키프레임 요청");
                                gopTracker.RequestKeyframe();
                                _networkServer.RequestKeyframe(clientNumber);
                                _frameStats.LogKeyframeRequested(clientNumber, "디코딩 오류 과다");
                                _decodingErrorCounters[clientNumber] = 0;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        EnhancedLogger.Instance.Error($"Error processing frame for client {clientNumber}", ex);
                        if (decodedFrame != null)
                        {
                            decodedFrame.Dispose();
                        }
                    }
                    finally
                    {
                        // Finalize processing and send acknowledgment
                        long endTicks = Stopwatch.GetTimestamp();
                        long frameTicks = endTicks - startTicks;
                        Interlocked.Add(ref _totalProcessingTime, frameTicks);

                        timer.Stop();
                        _networkServer.EndFrameProcessing(clientNumber, frame.FrameId);
                    }

                    // If we've processed 5 frames at once, yield to let UI update
                    if (framesProcessed >= 5)
                    {
                        break;
                    }
                }

                // Report metrics if needed
                UpdateMetrics();

                // If we still have frames in the queue, continue processing
                if (!queue.IsEmpty)
                {
                    // Schedule more processing on thread pool
                    if (_useParallelProcessing)
                    {
                        Task.Run(() => ProcessFramesForClient(clientNumber));
                    }
                    else
                    {
                        ProcessFramesForClient(clientNumber);
                    }
                }
                else
                {
                    // No more frames, clear processing flag
                    _processingFlags[clientNumber] = false;
                }
            }
            catch (Exception ex)
            {
                EnhancedLogger.Instance.Error($"Error in frame processing loop for client {clientNumber}: {ex.Message}", ex);
                _processingFlags[clientNumber] = false;

                // Check if more frames arrived during error handling
                if (_frameQueues.TryGetValue(clientNumber, out var queue) && !queue.IsEmpty)
                {
                    // Continue processing
                    ProcessNextFrame(clientNumber);
                }
            }
        }

        private void LogFrameDebugInfo(Bitmap bitmap, int clientNumber)
        {
            if (bitmap == null)
            {
                EnhancedLogger.Instance.Warning($"Null bitmap for client {clientNumber}");
                return;
            }

            EnhancedLogger.Instance.Debug(
                $"Decoded frame details: " +
                $"Client={clientNumber}, " +
                $"Size={bitmap.Width}x{bitmap.Height}, " +
                $"Format={bitmap.PixelFormat}, " +
                $"HRes={bitmap.HorizontalResolution}, " +
                $"VRes={bitmap.VerticalResolution}");

            try
            {
                // Check if image has valid data by examining a few pixels
                using (var locker = new BitmapDataLocker(bitmap))
                {
                    bool hasData = false;

                    // Check some pixels
                    for (int x = 0; x < bitmap.Width; x += bitmap.Width / 10)
                    {
                        for (int y = 0; y < bitmap.Height; y += bitmap.Height / 10)
                        {
                            Color pixel = locker.GetPixel(x, y);
                            if (pixel.R > 0 || pixel.G > 0 || pixel.B > 0)
                            {
                                hasData = true;
                                break;
                            }
                        }
                        if (hasData) break;
                    }

                    EnhancedLogger.Instance.Debug($"Bitmap has visible data: {hasData}");
                }
            }
            catch (Exception ex)
            {
                EnhancedLogger.Instance.Error($"Error checking bitmap data: {ex.Message}", ex);
            }
        }

        private void UpdateMetrics()
        {
            DateTime now = DateTime.UtcNow;

            if ((now - _lastReportTime) >= _reportInterval)
            {
                double uptimeSeconds = _uptimeTimer.Elapsed.TotalSeconds;
                double avgFps = uptimeSeconds > 0 ? _totalFramesProcessed / uptimeSeconds : 0;

                // Calculate average processing time
                double avgProcessingMs = _totalFramesProcessed > 0
                    ? (_totalProcessingTime * 1000.0 / Stopwatch.Frequency) / _totalFramesProcessed
                    : 0;

                var metrics = new ProcessingMetrics
                {
                    TotalFramesReceived = _totalFramesReceived,
                    TotalFramesProcessed = _totalFramesProcessed,
                    TotalFramesDropped = _totalFramesDropped,
                    AverageProcessingTimeMs = avgProcessingMs,
                    ProcessingFps = avgFps,
                    Uptime = _uptimeTimer.Elapsed
                };

                EnhancedLogger.Instance.Info(
                    $"Processing metrics: Received={_totalFramesReceived}, " +
                    $"Processed={_totalFramesProcessed}, " +
                    $"Dropped={_totalFramesDropped}, " +
                    $"AvgTime={avgProcessingMs:F2}ms, " +
                    $"FPS={avgFps:F1}");

                MetricsUpdated?.Invoke(this, new ProcessingMetricsEventArgs { Metrics = metrics });

                _lastReportTime = now;
            }
        }

        public void Dispose()
        {
            _shutdownEvent.Set();
            _networkServer.ScreenDataReceived -= OnScreenDataReceived;

            // Clean up resources
            foreach (var queue in _frameQueues.Values)
            {
                while (queue.TryDequeue(out _)) { }
            }

            _frameQueues.Clear();
            _processingFlags.Clear();
            _lastFrameTimeByClient.Clear();
            _clientProcessingTimers.Clear();

            // 통계 로거 정리
            _frameStats?.Dispose();

            _shutdownEvent.Dispose();

            EnhancedLogger.Instance.Info("Frame processing manager disposed");
        }
    }

    /// <summary>
    /// GOP 트래커 - 프레임 의존성 관리
    /// </summary>
    public class GopTracker
    {
        public long LastKeyframeId { get; set; } = -1;
        public DateTime LastKeyframeTime { get; set; } = DateTime.MinValue;
        public int FramesSinceKeyframe { get; set; } = 0;
        public bool WaitingForKeyframe { get; set; } = false;

        public void RegisterKeyframe(long frameId)
        {
            LastKeyframeId = frameId;
            LastKeyframeTime = DateTime.UtcNow;
            FramesSinceKeyframe = 0;
            WaitingForKeyframe = false;
        }

        public void RegisterFrame()
        {
            FramesSinceKeyframe++;
        }

        public void RequestKeyframe()
        {
            WaitingForKeyframe = true;
        }
    }

    /// <summary>
    /// Class to hold frame data for processing
    /// </summary>
    public class FrameData
    {
        public int ClientNumber { get; set; }
        public byte[] ScreenData { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public long FrameId { get; set; }
        public DateTime ReceivedTime { get; set; }
        public bool IsKeyFrame { get; set; }
        public long LastKeyframeId { get; set; }
    }

    /// <summary>
    /// Event args for frame processed event
    /// </summary>
    public class FrameProcessedEventArgs : EventArgs
    {
        public int ClientNumber { get; set; }
        public Bitmap Bitmap { get; set; }
        public long FrameId { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public bool IsKeyFrame { get; set; }
    }

    /// <summary>
    /// Processing metrics for reporting
    /// </summary>
    public class ProcessingMetrics
    {
        public long TotalFramesReceived { get; set; }
        public long TotalFramesProcessed { get; set; }
        public long TotalFramesDropped { get; set; }
        public double AverageProcessingTimeMs { get; set; }
        public double ProcessingFps { get; set; }
        public TimeSpan Uptime { get; set; }
    }

    /// <summary>
    /// Event args for metrics updated event
    /// </summary>
    public class ProcessingMetricsEventArgs : EventArgs
    {
        public ProcessingMetrics Metrics { get; set; }
    }

    /// <summary>
    /// Helper class for bitmap data checking
    /// </summary>
    internal class BitmapDataLocker : IDisposable
    {
        private Bitmap _bitmap;
        private System.Drawing.Imaging.BitmapData _bitmapData;
        private IntPtr _scan0;
        private int _stride;
        private bool _isDisposed = false;

        public BitmapDataLocker(Bitmap bitmap)
        {
            _bitmap = bitmap;
            _bitmapData = _bitmap.LockBits(
                new Rectangle(0, 0, _bitmap.Width, _bitmap.Height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                _bitmap.PixelFormat);
            _scan0 = _bitmapData.Scan0;
            _stride = _bitmapData.Stride;
        }

        public Color GetPixel(int x, int y)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(BitmapDataLocker));

            if (x < 0 || x >= _bitmap.Width || y < 0 || y >= _bitmap.Height)
                throw new ArgumentOutOfRangeException();

            unsafe
            {
                byte* ptr = (byte*)_scan0.ToPointer();
                int bpp = System.Drawing.Image.GetPixelFormatSize(_bitmap.PixelFormat) / 8;

                ptr += y * _stride + x * bpp;

                if (bpp == 4) // 32bpp ARGB
                {
                    byte b = ptr[0];
                    byte g = ptr[1];
                    byte r = ptr[2];
                    byte a = ptr[3];
                    return Color.FromArgb(a, r, g, b);
                }
                else if (bpp == 3) // 24bpp RGB
                {
                    byte b = ptr[0];
                    byte g = ptr[1];
                    byte r = ptr[2];
                    return Color.FromArgb(255, r, g, b);
                }

                return Color.Black; // Default
            }
        }

        public void Dispose()
        {
            if (!_isDisposed)
            {
                _bitmap.UnlockBits(_bitmapData);
                _isDisposed = true;
            }
        }
    }
}