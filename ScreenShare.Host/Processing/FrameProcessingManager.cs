using System;
using System.Threading;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using ScreenShare.Common.Models;
using ScreenShare.Common.Utils;
using ScreenShare.Host.Decoder;
using ScreenShare.Host.Network;

namespace ScreenShare.Host.Processing
{
    /// <summary>
    /// Manages asynchronous frame processing to ensure UI responsiveness and
    /// prevent frame processing bottlenecks with improved GOP handling.
    /// </summary>
    /// 
    public class ProcessingTask
    {
        public long FrameId { get; set; }
        public long StartTime { get; set; }
        public DateTime EnqueueTime { get; set; } = DateTime.UtcNow;
    }

    public class FrameProcessingManager : IDisposable
    {
        // Components
        private NetworkServer _networkServer;
        private FFmpegDecoder _decoder;

        // Queue and processing state - GOP 관리 개선을 위한 구조 변경
        private readonly ConcurrentDictionary<int, ConcurrentDictionary<long, GopState>> _gopStateByClient;
        private readonly ConcurrentDictionary<int, bool> _processingFlags;
        private readonly ConcurrentDictionary<int, DateTime> _lastFrameTimeByClient;
        private readonly ConcurrentDictionary<int, Stopwatch> _clientProcessingTimers;
        private readonly ManualResetEvent _shutdownEvent = new ManualResetEvent(false);
        private readonly ConcurrentDictionary<int, Bitmap> _lastSuccessfulFrameByClient;
        private readonly System.Threading.Timer _cleanupTimer;
        private readonly ConcurrentDictionary<int, ConcurrentQueue<ProcessingTask>> _processingQueues =
    new ConcurrentDictionary<int, ConcurrentQueue<ProcessingTask>>();

        // Configuration settings
        private bool _dropOutdatedFrames = true;
        private TimeSpan _frameExpirationTime = TimeSpan.FromSeconds(1);
        private int _maxQueueSizePerClient = 10;
        private bool _useParallelProcessing = true;
        private int _maxGopsPerClient = 10; // 클라이언트당 유지할 최대 GOP 수

        // Performance metrics
        private readonly Stopwatch _uptimeTimer = new Stopwatch();
        private long _totalFramesReceived = 0;
        private long _totalFramesProcessed = 0;
        private long _totalFramesDropped = 0;
        private long _totalProcessingTime = 0;
        private readonly TimeSpan _reportInterval = TimeSpan.FromSeconds(10);
        private DateTime _lastReportTime = DateTime.MinValue;

        // 디코딩 실패 추적
        private ConcurrentDictionary<int, int> _decodingErrorCounters = new ConcurrentDictionary<int, int>();
        private ConcurrentDictionary<int, int> _consecutiveErrorCounters = new ConcurrentDictionary<int, int>();
        private int _maxDecodeErrorsBeforeReset = 3;
        private int _maxConsecutiveErrorsBeforeReset = 5;

        // 통계 로깅
        private FrameStatisticsLogger _frameStats;

        // Events
        public event EventHandler<FrameProcessedEventArgs> FrameProcessed;
        public event EventHandler<ProcessingMetricsEventArgs> MetricsUpdated;

        public FrameProcessingManager(NetworkServer networkServer, FFmpegDecoder decoder)
        {
            _networkServer = networkServer;
            _decoder = decoder;

            _gopStateByClient = new ConcurrentDictionary<int, ConcurrentDictionary<long, GopState>>();
            _processingFlags = new ConcurrentDictionary<int, bool>();
            _lastFrameTimeByClient = new ConcurrentDictionary<int, DateTime>();
            _clientProcessingTimers = new ConcurrentDictionary<int, Stopwatch>();
            _lastSuccessfulFrameByClient = new ConcurrentDictionary<int, Bitmap>();

            _frameStats = new FrameStatisticsLogger("Processing");

            _networkServer.ScreenDataReceived += OnScreenDataReceived;

            _uptimeTimer.Start();

            _cleanupTimer = new System.Threading.Timer(CleanupOutdatedFrames, null, 500, 500);

            EnhancedLogger.Instance.Info("Frame processing manager initialized with improved GOP management");
        }

        private void CleanupOutdatedFrames(object state)
        {
            try
            {
                DateTime now = DateTime.UtcNow;

                foreach (var clientEntry in _gopStateByClient)
                {
                    int clientId = clientEntry.Key;
                    var gopMap = clientEntry.Value;

                    foreach (var gopEntry in gopMap.ToList())
                    {
                        long keyFrameId = gopEntry.Key;
                        var gop = gopEntry.Value;

                        if ((now - gop.StartTime) > _frameExpirationTime * 2)
                        {
                            if (gopMap.TryRemove(keyFrameId, out var removedGop))
                            {
                                EnhancedLogger.Instance.Info($"오래된 GOP {keyFrameId} 제거 (클라이언트 {clientId})");

                                int removedFrames = removedGop.Frames.Count;
                                Interlocked.Add(ref _totalFramesDropped, removedFrames);

                                removedGop.Frames.Clear();
                            }
                            continue;
                        }

                        var outdatedFrames = gop.Frames
                            .Where(f => !f.Value.IsProcessed && (now - f.Value.ReceivedTime) > _frameExpirationTime)
                            .Select(f => f.Key)
                            .ToList();

                        foreach (var frameId in outdatedFrames)
                        {
                            if (gop.Frames.TryRemove(frameId, out var frame))
                            {
                                EnhancedLogger.Instance.Debug($"오래된 프레임 {frameId} 제거 (클라이언트 {clientId})");
                                Interlocked.Increment(ref _totalFramesDropped);
                                _frameStats.LogFrameDropped(clientId, frameId, frame.IsKeyFrame, "타임아웃 정리");
                            }
                        }

                        if (gop.Frames.Count == 0)
                        {
                            gopMap.TryRemove(keyFrameId, out _);
                        }
                    }
                }

                foreach (var queueEntry in _processingQueues)
                {
                    int clientId = queueEntry.Key;
                    var queue = queueEntry.Value;

                    int originalCount = queue.Count;
                    var tempQueue = new ConcurrentQueue<ProcessingTask>();

                    while (queue.TryDequeue(out var task))
                    {
                        long elapsedTicks = Stopwatch.GetTimestamp() - task.StartTime;
                        double elapsedMs = elapsedTicks * 1000.0 / Stopwatch.Frequency;

                        if (elapsedMs < _frameExpirationTime.TotalMilliseconds)
                        {
                            tempQueue.Enqueue(task);
                        }
                        else
                        {
                            Interlocked.Increment(ref _totalFramesDropped);
                        }
                    }

                    while (tempQueue.TryDequeue(out var task))
                    {
                        queue.Enqueue(task);
                    }

                    int newCount = queue.Count;
                    if (originalCount - newCount > 5)
                    {
                        EnhancedLogger.Instance.Info($"클라이언트 {clientId} 큐에서 {originalCount - newCount}개의 오래된 작업 정리");
                    }
                }
            }
            catch (Exception ex)
            {
                EnhancedLogger.Instance.Error($"프레임 정리 중 오류: {ex.Message}", ex);
            }
        }
        private void AddToProcessingQueue(int clientNumber, long frameId)
        {
            var queue = _processingQueues.GetOrAdd(clientNumber, _ => new ConcurrentQueue<ProcessingTask>());

            var task = new ProcessingTask
            {
                FrameId = frameId,
                StartTime = Stopwatch.GetTimestamp(),
                EnqueueTime = DateTime.UtcNow
            };

            queue.Enqueue(task);

            // 큐 크기 제한
            while (queue.Count > _maxQueueSizePerClient * 2)
            {
                if (queue.TryDequeue(out _))
                {
                    EnhancedLogger.Instance.Warning($"큐 오버플로우로 인해 클라이언트 {clientNumber}의 작업 제거");
                    Interlocked.Increment(ref _totalFramesDropped);
                }
            }
        }

        // 처리 완료 메서드 (NetworkServer와 연결)
        private void CompleteProcessing(int clientNumber, long frameId, TimeSpan processingTime)
        {
            try
            {
                // 네트워크 서버에 처리 완료 알림
                _networkServer.EndFrameProcessing(clientNumber, frameId);
            }
            catch (Exception ex)
            {
                EnhancedLogger.Instance.Error($"처리 완료 알림 오류: {ex.Message}", ex);
            }
        }
        /// <summary>
        /// Configure frame processing options
        /// </summary>
        public void Configure(
            bool dropOutdatedFrames = true,
            int maxQueueSize = 10,
            int frameExpirationMs = 1000,
            bool useParallelProcessing = true,
            int maxGopsPerClient = 10)
        {
            _dropOutdatedFrames = dropOutdatedFrames;
            _maxQueueSizePerClient = maxQueueSize;
            _frameExpirationTime = TimeSpan.FromMilliseconds(frameExpirationMs);
            _useParallelProcessing = useParallelProcessing;
            _maxGopsPerClient = maxGopsPerClient;

            EnhancedLogger.Instance.Info(
                $"Processing configuration: DropOutdated={_dropOutdatedFrames}, " +
                $"MaxQueue={_maxQueueSizePerClient}, " +
                $"ExpirationMs={frameExpirationMs}, " +
                $"Parallel={_useParallelProcessing}, " +
                $"MaxGOPs={_maxGopsPerClient}");
        }

        /// <summary>
        /// Called when a new frame data packet is received from the network
        /// </summary>
        private void OnScreenDataReceived(object sender, ScreenDataEventArgs e)
        {
            if (e == null || e.ScreenData == null)
                return;

            Interlocked.Increment(ref _totalFramesReceived);

            try
            {
                var gopStateMap = _gopStateByClient.GetOrAdd(e.ClientNumber,
                    _ => new ConcurrentDictionary<long, GopState>());

                var frameData = new FrameData
                {
                    ClientNumber = e.ClientNumber,
                    ScreenData = e.ScreenData,
                    Width = e.Width,
                    Height = e.Height,
                    FrameId = e.FrameId,
                    ReceivedTime = DateTime.UtcNow,
                    IsKeyFrame = e.IsKeyFrame
                };

                if (e.IsKeyFrame)
                {
                    EnhancedLogger.Instance.Debug($"클라이언트 {e.ClientNumber}의 키프레임 수신, frameId={e.FrameId}, 크기={e.ScreenData.Length}");

                    var gopState = new GopState
                    {
                        KeyFrameId = e.FrameId,
                        StartTime = DateTime.UtcNow,
                        IsComplete = false
                    };

                    gopState.AddFrame(frameData);
                    gopStateMap[e.FrameId] = gopState;

                    CleanupOldGops(gopStateMap, e.FrameId);

                    _frameStats.LogFrameReceived(e.ClientNumber, e.FrameId, true, e.ScreenData.Length);
                }
                else
                {
                    long keyFrameId = FindOwnerKeyFrameId(gopStateMap, e.FrameId);

                    if (keyFrameId > 0 && gopStateMap.TryGetValue(keyFrameId, out var gopState))
                    {
                        gopState.AddFrame(frameData);
                        frameData.LastKeyframeId = keyFrameId;

                        EnhancedLogger.Instance.Debug(
                            $"클라이언트 {e.ClientNumber}의 프레임 추가: id={e.FrameId}, 키프레임={keyFrameId}, " +
                            $"GOP 내 프레임 수={gopState.Frames.Count}");

                        _frameStats.LogFrameReceived(e.ClientNumber, e.FrameId, false, e.ScreenData.Length);
                    }
                    else
                    {
                        EnhancedLogger.Instance.Warning(
                            $"클라이언트 {e.ClientNumber}의 프레임 {e.FrameId}에 대한 GOP를 찾을 수 없음 - 키프레임 요청");

                        RequestKeyframe(e.ClientNumber, "GOP 대응 없음");

                        _frameStats.LogFrameDropped(e.ClientNumber, e.FrameId, false, "GOP 없음");
                        Interlocked.Increment(ref _totalFramesDropped);

                        return;
                    }
                }

                _lastFrameTimeByClient[e.ClientNumber] = DateTime.UtcNow;

                ProcessNextFrame(e.ClientNumber);
            }
            catch (Exception ex)
            {
                EnhancedLogger.Instance.Error($"프레임 수신 처리 중 오류: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 주어진 프레임 ID가 속한 키프레임 ID를 찾습니다.
        /// </summary>
        private long FindOwnerKeyFrameId(ConcurrentDictionary<long, GopState> gopStateMap, long frameId)
        {
            // 이 프레임이 이미 GOP에 등록되어 있는지 확인
            foreach (var gopEntry in gopStateMap)
            {
                if (gopEntry.Value.Frames.ContainsKey(frameId))
                {
                    return gopEntry.Key;
                }
            }

            // 찾지 못한 경우 프레임 ID보다 작은 가장 큰 키프레임 ID 찾기
            long bestKeyFrameId = 0;

            foreach (var keyFrameId in gopStateMap.Keys)
            {
                if (keyFrameId < frameId && keyFrameId > bestKeyFrameId)
                {
                    bestKeyFrameId = keyFrameId;
                }
            }

            return bestKeyFrameId;
        }

        /// <summary>
        /// 오래된 GOP를 정리합니다.
        /// </summary>
        private void CleanupOldGops(ConcurrentDictionary<long, GopState> gopStateMap, long currentKeyFrameId)
        {
            try
            {
                // 허용된 최대 GOP 수를 초과하는 경우 가장 오래된 GOP 제거
                if (gopStateMap.Count > _maxGopsPerClient)
                {
                    // 현재 키프레임보다 오래된 것들 중에서 가장 최근의 N개만 유지
                    var oldKeyFrameIds = gopStateMap.Keys
                        .Where(id => id < currentKeyFrameId)
                        .OrderByDescending(id => id)
                        .Skip(_maxGopsPerClient - 1)
                        .ToList();

                    foreach (var oldKeyFrameId in oldKeyFrameIds)
                    {
                        if (gopStateMap.TryRemove(oldKeyFrameId, out var removedGop))
                        {
                            EnhancedLogger.Instance.Debug(
                                $"오래된 GOP 제거: 키프레임={oldKeyFrameId}, 프레임 수={removedGop.Frames.Count}");

                            // 제거된 프레임 수 추적
                            Interlocked.Add(ref _totalFramesDropped, removedGop.Frames.Count);

                            // 메모리 해제를 위해 프레임 데이터 명시적 정리
                            removedGop.Frames.Clear();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                EnhancedLogger.Instance.Error($"GOP 정리 중 오류: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 키프레임 요청을 보냅니다.
        /// </summary>
        private void RequestKeyframe(int clientNumber, string reason)
        {
            try
            {
                if (_networkServer.RequestKeyframe(clientNumber))
                {
                    EnhancedLogger.Instance.Info($"클라이언트 {clientNumber}에 키프레임 요청 전송: {reason}");
                    _frameStats.LogKeyframeRequested(clientNumber, reason);
                }
            }
            catch (Exception ex)
            {
                EnhancedLogger.Instance.Error($"키프레임 요청 중 오류: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 클라이언트의 다음 프레임 처리 시작
        /// </summary>
        private void ProcessNextFrame(int clientNumber)
        {
            // 이미 처리 중인지 확인
            if (_processingFlags.TryGetValue(clientNumber, out bool isProcessing) && isProcessing)
                return;

            // 처리 플래그 설정
            _processingFlags[clientNumber] = true;

            // 스레드 풀 사용
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
        /// 특정 클라이언트에 대한 프레임 처리
        /// </summary>
        private void ProcessFramesForClient(int clientNumber)
        {
            try
            {
                // 클라이언트의 GOP 상태 맵 가져오기
                if (!_gopStateByClient.TryGetValue(clientNumber, out var gopStateMap))
                {
                    _processingFlags[clientNumber] = false;
                    return;
                }

                // 처리 타이머
                var timer = _clientProcessingTimers.GetOrAdd(clientNumber, _ => new Stopwatch());

                // 처리할 프레임 우선순위로 선택
                var frameToProcess = SelectNextFrameToProcess(clientNumber, gopStateMap);
                if (frameToProcess == null)
                {
                    _processingFlags[clientNumber] = false;
                    return;
                }

                // 프레임 처리
                ProcessSelectedFrame(clientNumber, frameToProcess, timer);

                // 아직 처리할 프레임이 있는지 확인
                if (HasFramesToProcess(gopStateMap))
                {
                    // 스레드 풀에 계속 처리 예약
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
                    // 더 이상 처리할 프레임이 없음, 처리 플래그 해제
                    _processingFlags[clientNumber] = false;
                }

                // 필요시 메트릭 업데이트
                UpdateMetrics();
            }
            catch (Exception ex)
            {
                EnhancedLogger.Instance.Error($"클라이언트 {clientNumber}의 프레임 처리 루프 오류: {ex.Message}", ex);
                _processingFlags[clientNumber] = false;

                // 오류 처리 중 새 프레임이 도착했는지 확인
                if (_gopStateByClient.TryGetValue(clientNumber, out var gopStateMap) &&
                    HasFramesToProcess(gopStateMap))
                {
                    // 처리 계속
                    ProcessNextFrame(clientNumber);
                }
            }
        }

        /// <summary>
        /// 처리할 다음 프레임을 선택합니다.
        /// </summary>
        private FrameData SelectNextFrameToProcess(int clientNumber, ConcurrentDictionary<long, GopState> gopStateMap)
        {
            try
            {
                int totalPendingFrames = 0;

                foreach (var gop in gopStateMap.Values)
                {
                    totalPendingFrames += gop.Frames.Count(f => !f.Value.IsProcessed);
                }

                bool highPressure = totalPendingFrames > _maxQueueSizePerClient * 2;

                if (highPressure)
                {
                    EnhancedLogger.Instance.Info($"높은 처리 압박 모드 (대기 프레임 {totalPendingFrames}개) - 최신 컨텐츠 우선 처리");

                    foreach (var gopId in gopStateMap.Keys.OrderByDescending(k => k))
                    {
                        if (gopStateMap.TryGetValue(gopId, out var gopState))
                        {
                            var keyFrame = gopState.Frames.Values
                                .FirstOrDefault(f => f.IsKeyFrame && !f.IsProcessed);

                            if (keyFrame != null)
                            {
                                return keyFrame;
                            }

                            bool keyFrameProcessed = gopState.Frames.Any(kv => kv.Value.IsKeyFrame && kv.Value.IsProcessed);

                            if (keyFrameProcessed)
                            {
                                var newestFrame = gopState.Frames
                                    .Where(kv => !kv.Value.IsProcessed)
                                    .OrderByDescending(kv => kv.Key)
                                    .Select(kv => kv.Value)
                                    .FirstOrDefault();

                                if (newestFrame != null)
                                {
                                    return newestFrame;
                                }
                            }
                        }
                    }
                }

                foreach (var gopId in gopStateMap.Keys.OrderByDescending(k => k))
                {
                    if (gopStateMap.TryGetValue(gopId, out var gopState))
                    {
                        if (gopState.IsComplete)
                            continue;

                        var unprocessedFrames = gopState.Frames
                            .Where(kv => !kv.Value.IsProcessed)
                            .OrderBy(kv => kv.Key)
                            .Select(kv => kv.Value)
                            .ToList();

                        if (unprocessedFrames.Count > 0)
                        {
                            var keyFrame = unprocessedFrames.FirstOrDefault(f => f.IsKeyFrame);
                            if (keyFrame != null)
                                return keyFrame;

                            bool keyFrameProcessed = gopState.Frames
                                .Any(kv => kv.Value.IsKeyFrame && kv.Value.IsProcessed);

                            if (keyFrameProcessed)
                            {
                                return unprocessedFrames[0];
                            }
                            else if (!gopState.Frames.Any(kv => kv.Value.IsKeyFrame))
                            {
                                EnhancedLogger.Instance.Warning($"키프레임 없는 GOP: 클라이언트={clientNumber}, GOP={gopId}");
                                gopState.IsComplete = true;
                                continue;
                            }
                        }
                        else
                        {
                            gopState.IsComplete = true;
                        }
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                EnhancedLogger.Instance.Error($"다음 프레임 선택 중 오류: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// 선택된 프레임을 처리합니다.
        /// </summary>
        private void ProcessSelectedFrame(int clientNumber, FrameData frame, Stopwatch timer)
        {
            if (frame == null)
                return;

            Bitmap decodedFrame = null;
            long startTicks = Stopwatch.GetTimestamp();

            try
            {
                if (_dropOutdatedFrames && (DateTime.UtcNow - frame.ReceivedTime) > _frameExpirationTime)
                {
                    EnhancedLogger.Instance.Debug(
                        $"클라이언트 {clientNumber}의 오래된 프레임 {frame.FrameId} 폐기, " +
                        $"경과시간={(DateTime.UtcNow - frame.ReceivedTime).TotalMilliseconds:F0}ms");

                    Interlocked.Increment(ref _totalFramesDropped);
                    _frameStats.LogFrameDropped(clientNumber, frame.FrameId, frame.IsKeyFrame, "만료됨");

                    frame.IsProcessed = true;
                    return;
                }

                timer.Restart();

                // 큐에 추가 (자체 관리 큐 사용)
                AddToProcessingQueue(clientNumber, frame.FrameId);

                if (frame.IsKeyFrame)
                {
                    EnhancedLogger.Instance.Info(
                        $"클라이언트 {clientNumber}의 키프레임 {frame.FrameId} 디코딩 중, 크기={frame.ScreenData.Length}");
                }
                else
                {
                    EnhancedLogger.Instance.Debug(
                        $"클라이언트 {clientNumber}의 프레임 {frame.FrameId} 디코딩 중, " +
                        $"크기={frame.ScreenData.Length}, 키프레임={frame.LastKeyframeId}");
                }

                decodedFrame = _decoder.DecodeFrame(frame.ScreenData, frame.Width, frame.Height, frame.FrameId);

                if (decodedFrame != null)
                {
                    EnhancedLogger.Instance.Debug(
                        $"클라이언트 {clientNumber}의 프레임 {frame.FrameId} 디코딩 성공");

                    _decodingErrorCounters[clientNumber] = 0;
                    _consecutiveErrorCounters[clientNumber] = 0;

                    SetLastSuccessfulFrame(clientNumber, decodedFrame);

                    timer.Stop();
                    _frameStats.LogDecodingSuccess(clientNumber, frame.FrameId, frame.IsKeyFrame, timer.Elapsed.TotalMilliseconds);

                    Interlocked.Increment(ref _totalFramesProcessed);

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
                    EnhancedLogger.Instance.Warning(
                        $"클라이언트 {clientNumber}의 프레임 {frame.FrameId} 디코딩 실패");

                    int errorCount = _decodingErrorCounters.AddOrUpdate(clientNumber, 1, (k, v) => v + 1);
                    int consecutiveErrors = _consecutiveErrorCounters.AddOrUpdate(clientNumber, 1, (k, v) => v + 1);

                    _frameStats.LogDecodingFailure(clientNumber, frame.FrameId, frame.IsKeyFrame);

                    if (frame.IsKeyFrame)
                    {
                        EnhancedLogger.Instance.Warning(
                            $"클라이언트 {clientNumber}의 키프레임 {frame.FrameId} 디코딩 실패 - 새 키프레임 요청");
                        RequestKeyframe(clientNumber, "키프레임 디코딩 실패");
                    }
                    else if (consecutiveErrors >= _maxConsecutiveErrorsBeforeReset)
                    {
                        EnhancedLogger.Instance.Warning(
                            $"클라이언트 {clientNumber} 연속 {consecutiveErrors}회 디코딩 실패 - 키프레임 요청");
                        RequestKeyframe(clientNumber, $"연속 {consecutiveErrors}회 디코딩 실패");
                        _consecutiveErrorCounters[clientNumber] = 0;
                    }
                    else if (errorCount >= _maxDecodeErrorsBeforeReset)
                    {
                        EnhancedLogger.Instance.Warning(
                            $"클라이언트 {clientNumber} 디코딩 오류 과다({errorCount}회) - 키프레임 요청");
                        //RequestKeyframe(clientNumber, $"누적 {errorCount}회 디코딩 실패");
                        _decodingErrorCounters[clientNumber] = 0;
                    }

                    if (_lastSuccessfulFrameByClient.TryGetValue(clientNumber, out var lastFrame) && lastFrame != null)
                    {
                        try
                        {
                            EnhancedLogger.Instance.Debug(
                                $"클라이언트 {clientNumber}의 마지막 성공 프레임 사용");

                            Bitmap lastFrameCopy = new Bitmap(lastFrame);

                            FrameProcessed?.Invoke(this, new FrameProcessedEventArgs
                            {
                                ClientNumber = clientNumber,
                                Bitmap = lastFrameCopy,
                                FrameId = frame.FrameId,
                                ProcessingTime = timer.Elapsed,
                                IsKeyFrame = false
                            });
                        }
                        catch (Exception ex)
                        {
                            EnhancedLogger.Instance.Error($"마지막 프레임 복사 중 오류: {ex.Message}", ex);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                EnhancedLogger.Instance.Error($"프레임 처리 중 오류: {ex.Message}", ex);
                if (decodedFrame != null)
                {
                    decodedFrame.Dispose();
                }
            }
            finally
            {
                frame.IsProcessed = true;

                long endTicks = Stopwatch.GetTimestamp();
                long frameTicks = endTicks - startTicks;
                Interlocked.Add(ref _totalProcessingTime, frameTicks);

                timer.Stop();
                CompleteProcessing(clientNumber, frame.FrameId, timer.Elapsed);
            }
        }

        /// <summary>
        /// 마지막 성공 프레임을 설정합니다.
        /// </summary>
        private void SetLastSuccessfulFrame(int clientNumber, Bitmap frame)
        {
            try
            {
                // 기존 프레임 제거
                if (_lastSuccessfulFrameByClient.TryGetValue(clientNumber, out var oldFrame))
                {
                    try
                    {
                        oldFrame?.Dispose();
                    }
                    catch { /* 무시 */ }
                }

                // 새 프레임 저장 (복사본)
                _lastSuccessfulFrameByClient[clientNumber] = new Bitmap(frame);
            }
            catch (Exception ex)
            {
                EnhancedLogger.Instance.Error($"마지막 성공 프레임 저장 중 오류: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 처리할 프레임이 있는지 확인합니다.
        /// </summary>
        private bool HasFramesToProcess(ConcurrentDictionary<long, GopState> gopStateMap)
        {
            return gopStateMap.Values.Any(gop =>
                !gop.IsComplete && gop.Frames.Any(f => !f.Value.IsProcessed));
        }

        /// <summary>
        /// 메트릭을 업데이트합니다.
        /// </summary>
        private void UpdateMetrics()
        {
            DateTime now = DateTime.UtcNow;

            if ((now - _lastReportTime) >= _reportInterval)
            {
                double uptimeSeconds = _uptimeTimer.Elapsed.TotalSeconds;
                double avgFps = uptimeSeconds > 0 ? _totalFramesProcessed / uptimeSeconds : 0;

                // 평균 처리 시간 계산
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
            _cleanupTimer?.Dispose();
            _shutdownEvent.Set();
            _networkServer.ScreenDataReceived -= OnScreenDataReceived;

            foreach (var client in _gopStateByClient)
            {
                foreach (var gop in client.Value)
                {
                    gop.Value.Frames.Clear();
                }
                client.Value.Clear();
            }
            _gopStateByClient.Clear();
            _processingFlags.Clear();
            _lastFrameTimeByClient.Clear();
            _clientProcessingTimers.Clear();

            foreach (var frame in _lastSuccessfulFrameByClient.Values)
            {
                try
                {
                    frame?.Dispose();
                }
                catch { /* 무시 */ }
            }
            _lastSuccessfulFrameByClient.Clear();

            _frameStats?.Dispose();

            _shutdownEvent.Dispose();

            EnhancedLogger.Instance.Info("Frame processing manager disposed");
        }
    }

    /// <summary>
    /// GOP 상태를 추적하는 클래스
    /// </summary>
    public class GopState
    {
        // GOP 정보
        public long KeyFrameId { get; set; }
        public DateTime StartTime { get; set; }
        public bool IsComplete { get; set; }

        // 프레임 컬렉션
        public ConcurrentDictionary<long, FrameData> Frames { get; } = new ConcurrentDictionary<long, FrameData>();

        // 디코딩 성공 여부
        public bool IsDecodingSuccessful { get; set; }

        // 마지막 액세스 시간
        public DateTime LastAccessTime { get; set; } = DateTime.UtcNow;

        // 이 GOP에 프레임을 추가
        public void AddFrame(FrameData frame)
        {
            Frames[frame.FrameId] = frame;
            LastAccessTime = DateTime.UtcNow;

            // 키프레임인 경우 시작 시간 설정
            if (frame.IsKeyFrame)
            {
                KeyFrameId = frame.FrameId;
            }
        }

        // 처리되지 않은 프레임 수 계산
        public int GetUnprocessedFrameCount()
        {
            return Frames.Count(pair => !pair.Value.IsProcessed);
        }

        // GOP 유효성 확인
        public bool IsValid()
        {
            return Frames.Any(pair => pair.Value.IsKeyFrame);
        }

        // 모든 프레임 제거
        public void Clear()
        {
            Frames.Clear();
        }
    }

    /// <summary>
    /// 프레임 데이터를 저장하는 클래스
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
        public bool IsProcessed { get; set; }
    }

    /// <summary>
    /// 프레임 처리 이벤트 인자
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
    /// 처리 메트릭
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
    /// 메트릭 업데이트 이벤트 인자
    /// </summary>
    public class ProcessingMetricsEventArgs : EventArgs
    {
        public ProcessingMetrics Metrics { get; set; }
    }

    /// <summary>
    /// 비트맵 데이터 접근 도우미 클래스
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

                return Color.Black; // 기본값
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