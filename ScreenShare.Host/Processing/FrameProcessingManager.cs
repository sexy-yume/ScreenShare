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

        // Configuration settings
        private bool _dropOutdatedFrames = true;
        private TimeSpan _frameExpirationTime = TimeSpan.FromSeconds(1);
        private int _maxQueueSizePerClient = 5;
        private bool _useParallelProcessing = true;
        private int _maxGopsPerClient = 3; // 클라이언트당 유지할 최대 GOP 수

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

            // 프레임 통계 로거 초기화
            _frameStats = new FrameStatisticsLogger("Processing");

            // Subscribe to network events
            _networkServer.ScreenDataReceived += OnScreenDataReceived;

            _uptimeTimer.Start();

            EnhancedLogger.Instance.Info("Frame processing manager initialized with improved GOP management");
        }

        /// <summary>
        /// Configure frame processing options
        /// </summary>
        public void Configure(
            bool dropOutdatedFrames = true,
            int maxQueueSize = 5,
            int frameExpirationMs = 1000,
            bool useParallelProcessing = true,
            int maxGopsPerClient = 3)
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
                // 클라이언트에 대한 GOP 상태 맵 가져오기
                var gopStateMap = _gopStateByClient.GetOrAdd(e.ClientNumber,
                    _ => new ConcurrentDictionary<long, GopState>());

                // 프레임 데이터 생성
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

                // 키프레임인 경우 새 GOP 시작
                if (e.IsKeyFrame)
                {
                    EnhancedLogger.Instance.Debug($"클라이언트 {e.ClientNumber}의 키프레임 수신, frameId={e.FrameId}, 크기={e.ScreenData.Length}");

                    // 새 GOP 상태 생성
                    var gopState = new GopState
                    {
                        KeyFrameId = e.FrameId,
                        StartTime = DateTime.UtcNow,
                        IsComplete = false
                    };

                    // 키프레임 추가
                    gopState.Frames[e.FrameId] = frameData;

                    // GOP 맵에 추가
                    gopStateMap[e.FrameId] = gopState;

                    // 최대 GOP 수 유지 (가장 오래된 것부터 제거)
                    CleanupOldGops(gopStateMap, e.FrameId);

                    _frameStats.LogFrameReceived(e.ClientNumber, e.FrameId, true, e.ScreenData.Length);
                }
                else
                {
                    // 이 프레임이 속한 GOP 찾기 (가장 최근 키프레임)
                    long keyFrameId = FindOwnerKeyFrameId(gopStateMap, e.FrameId);

                    if (keyFrameId > 0 && gopStateMap.TryGetValue(keyFrameId, out var gopState))
                    {
                        // 현재 GOP에 중간 프레임 추가
                        gopState.Frames[e.FrameId] = frameData;
                        frameData.LastKeyframeId = keyFrameId;

                        EnhancedLogger.Instance.Debug(
                            $"클라이언트 {e.ClientNumber}의 프레임 추가: id={e.FrameId}, 키프레임={keyFrameId}, " +
                            $"GOP 내 프레임 수={gopState.Frames.Count}");

                        _frameStats.LogFrameReceived(e.ClientNumber, e.FrameId, false, e.ScreenData.Length);
                    }
                    else
                    {
                        // 소속된 GOP를 찾을 수 없는 경우 (키프레임 누락)
                        EnhancedLogger.Instance.Warning(
                            $"클라이언트 {e.ClientNumber}의 프레임 {e.FrameId}에 대한 GOP를 찾을 수 없음 - 키프레임 요청");

                        // 키프레임 요청
                        RequestKeyframe(e.ClientNumber, "GOP 대응 없음");

                        _frameStats.LogFrameDropped(e.ClientNumber, e.FrameId, false, "GOP 없음");
                        Interlocked.Increment(ref _totalFramesDropped);

                        // 이 프레임은 처리하지 않고 종료
                        return;
                    }
                }

                // 마지막 프레임 시간 업데이트
                _lastFrameTimeByClient[e.ClientNumber] = DateTime.UtcNow;

                // 처리 시작
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
            // 프레임 ID보다 작거나 같은 가장 큰 키프레임 ID를 찾습니다
            return gopStateMap.Keys
                .Where(keyFrameId => keyFrameId < frameId)
                .OrderByDescending(keyFrameId => keyFrameId)
                .FirstOrDefault();
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
                // 가장 최근 GOP부터 찾기
                foreach (var gopId in gopStateMap.Keys.OrderByDescending(k => k))
                {
                    if (gopStateMap.TryGetValue(gopId, out var gopState))
                    {
                        // 이 GOP가 완료되었으면 다음 GOP로
                        if (gopState.IsComplete)
                            continue;

                        // 이 GOP에서 처리되지 않은 가장 오래된 프레임 찾기
                        var unprocessedFrames = gopState.Frames
                            .Where(kv => !kv.Value.IsProcessed)
                            .OrderBy(kv => kv.Key)
                            .Select(kv => kv.Value)
                            .ToList();

                        if (unprocessedFrames.Count > 0)
                        {
                            // 먼저 키프레임 처리
                            var keyFrame = unprocessedFrames.FirstOrDefault(f => f.IsKeyFrame);
                            if (keyFrame != null)
                                return keyFrame;

                            // 키프레임이 처리되었는지 확인
                            bool keyFrameProcessed = gopState.Frames
                                .Any(kv => kv.Value.IsKeyFrame && kv.Value.IsProcessed);

                            // 키프레임이 처리되었으면 다음 순차 프레임 반환
                            if (keyFrameProcessed)
                            {
                                return unprocessedFrames[0];
                            }
                            // 키프레임이 없으면 이 GOP에 문제가 있음, 다음 GOP로
                            else if (!gopState.Frames.Any(kv => kv.Value.IsKeyFrame))
                            {
                                EnhancedLogger.Instance.Warning($"키프레임 없는 GOP: 클라이언트={clientNumber}, GOP={gopId}");
                                // 이 GOP를 완료 표시하고 다음으로
                                gopState.IsComplete = true;
                                continue;
                            }
                        }
                        else
                        {
                            // 이 GOP의 모든 프레임이 처리됨
                            gopState.IsComplete = true;
                        }
                    }
                }

                // 처리할 프레임을 찾지 못함
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
                // 만료된 프레임 확인
                if (_dropOutdatedFrames && (DateTime.UtcNow - frame.ReceivedTime) > _frameExpirationTime)
                {
                    EnhancedLogger.Instance.Debug(
                        $"클라이언트 {clientNumber}의 오래된 프레임 {frame.FrameId} 폐기, " +
                        $"경과시간={(DateTime.UtcNow - frame.ReceivedTime).TotalMilliseconds:F0}ms");

                    Interlocked.Increment(ref _totalFramesDropped);
                    _frameStats.LogFrameDropped(clientNumber, frame.FrameId, frame.IsKeyFrame, "만료됨");

                    // 프레임을 처리된 것으로 표시
                    frame.IsProcessed = true;
                    return;
                }

                // 처리 시간 추적
                timer.Restart();

                // 프레임 디코딩
                _networkServer.BeginFrameProcessing(clientNumber, frame.FrameId);

                // 로깅
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
                    // 디코딩 성공
                    EnhancedLogger.Instance.Debug(
                        $"클라이언트 {clientNumber}의 프레임 {frame.FrameId} 디코딩 성공");

                    // 오류 카운터 초기화
                    _decodingErrorCounters[clientNumber] = 0;
                    _consecutiveErrorCounters[clientNumber] = 0;

                    // 마지막 성공 프레임 저장
                    SetLastSuccessfulFrame(clientNumber, decodedFrame);

                    // 디코딩 성공 통계 기록
                    timer.Stop();
                    _frameStats.LogDecodingSuccess(clientNumber, frame.FrameId, frame.IsKeyFrame, timer.Elapsed.TotalMilliseconds);

                    Interlocked.Increment(ref _totalFramesProcessed);

                    // 알림
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
                    // 디코딩 실패
                    EnhancedLogger.Instance.Warning(
                        $"클라이언트 {clientNumber}의 프레임 {frame.FrameId} 디코딩 실패");

                    // 오류 카운터 증가
                    int errorCount = _decodingErrorCounters.AddOrUpdate(clientNumber, 1, (k, v) => v + 1);
                    int consecutiveErrors = _consecutiveErrorCounters.AddOrUpdate(clientNumber, 1, (k, v) => v + 1);

                    // 디코딩 실패 통계 기록
                    _frameStats.LogDecodingFailure(clientNumber, frame.FrameId, frame.IsKeyFrame);

                    // 키프레임이 실패한 경우
                    if (frame.IsKeyFrame)
                    {
                        EnhancedLogger.Instance.Warning(
                            $"클라이언트 {clientNumber}의 키프레임 {frame.FrameId} 디코딩 실패 - 새 키프레임 요청");
                        RequestKeyframe(clientNumber, "키프레임 디코딩 실패");
                    }
                    // 연속 실패가 너무 많음
                    else if (consecutiveErrors >= _maxConsecutiveErrorsBeforeReset)
                    {
                        EnhancedLogger.Instance.Warning(
                            $"클라이언트 {clientNumber} 연속 {consecutiveErrors}회 디코딩 실패 - 키프레임 요청");
                        RequestKeyframe(clientNumber, $"연속 {consecutiveErrors}회 디코딩 실패");
                        _consecutiveErrorCounters[clientNumber] = 0;
                    }
                    // 누적 실패가 너무 많음
                    else if (errorCount >= _maxDecodeErrorsBeforeReset)
                    {
                        EnhancedLogger.Instance.Warning(
                            $"클라이언트 {clientNumber} 디코딩 오류 과다({errorCount}회) - 키프레임 요청");
                        RequestKeyframe(clientNumber, $"누적 {errorCount}회 디코딩 실패");
                        _decodingErrorCounters[clientNumber] = 0;
                    }

                    // 마지막 성공 프레임 사용
                    if (_lastSuccessfulFrameByClient.TryGetValue(clientNumber, out var lastFrame) && lastFrame != null)
                    {
                        EnhancedLogger.Instance.Debug(
                            $"클라이언트 {clientNumber}의 마지막 성공 프레임 사용");

                        // 이전 성공 프레임의 복사본 만들기
                        Bitmap lastFrameCopy = new Bitmap(lastFrame);

                        // 알림
                        FrameProcessed?.Invoke(this, new FrameProcessedEventArgs
                        {
                            ClientNumber = clientNumber,
                            Bitmap = lastFrameCopy,
                            FrameId = frame.FrameId,
                            ProcessingTime = timer.Elapsed,
                            IsKeyFrame = false
                        });
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
                // 프레임을 처리된 것으로 표시
                frame.IsProcessed = true;

                // 처리 완료 및 확인 전송
                long endTicks = Stopwatch.GetTimestamp();
                long frameTicks = endTicks - startTicks;
                Interlocked.Add(ref _totalProcessingTime, frameTicks);

                timer.Stop();
                _networkServer.EndFrameProcessing(clientNumber, frame.FrameId);
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
            _shutdownEvent.Set();
            _networkServer.ScreenDataReceived -= OnScreenDataReceived;

            // 리소스 정리
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

            // 마지막 성공 프레임 정리
            foreach (var frame in _lastSuccessfulFrameByClient.Values)
            {
                try
                {
                    frame?.Dispose();
                }
                catch { /* 무시 */ }
            }
            _lastSuccessfulFrameByClient.Clear();

            // 통계 로거 정리
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
        // 키프레임 ID
        public long KeyFrameId { get; set; }

        // GOP 시작 시간
        public DateTime StartTime { get; set; }

        // GOP가 완료되었는지 여부
        public bool IsComplete { get; set; }

        // 이 GOP의 모든 프레임 (프레임 ID -> 프레임 데이터)
        public ConcurrentDictionary<long, FrameData> Frames { get; } = new ConcurrentDictionary<long, FrameData>();
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