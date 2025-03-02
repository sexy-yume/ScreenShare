using System;
using System.Collections.Concurrent;
using System.Threading;

namespace ScreenShare.Common.Utils
{
    /// <summary>
    /// 프레임 처리 통계를 기록하고 로깅하는 클래스
    /// </summary>
    public class FrameStatisticsLogger : IDisposable
    {
        private readonly EnhancedLogger _logger;
        private readonly string _prefix;
        private readonly ConcurrentDictionary<int, ClientFrameStats> _clientStats = new ConcurrentDictionary<int, ClientFrameStats>();
        private readonly Timer _reportingTimer;

        /// <summary>
        /// 프레임 통계 로거를 초기화합니다.
        /// </summary>
        /// <param name="prefix">로그 메시지에 추가할 접두사</param>
        public FrameStatisticsLogger(string prefix = "")
        {
            _logger = EnhancedLogger.Instance;
            _prefix = !string.IsNullOrEmpty(prefix) ? prefix + ": " : "";

            // 30초마다 통계 보고
            _reportingTimer = new Timer(ReportStatistics, null, 30000, 30000);
        }

        /// <summary>
        /// 프레임 수신을 기록합니다.
        /// </summary>
        public void LogFrameReceived(int clientId, long frameId, bool isKeyframe, int frameSize)
        {
            var stats = _clientStats.GetOrAdd(clientId, _ => new ClientFrameStats());
            stats.TotalFrames++;
            stats.IntervalFrames++;
            stats.TotalBytes += frameSize;
            stats.IntervalBytes += frameSize;

            if (isKeyframe)
            {
                stats.KeyFrames++;
                stats.IntervalKeyFrames++;
                stats.KeyFrameBytes += frameSize;

                // 모든 키프레임 로깅
                _logger.Info($"{_prefix}키프레임 수신: 클라이언트={clientId}, ID={frameId}, 크기={frameSize/1024.0:F1}KB, " +
                             $"비율={stats.KeyFrames * 100.0 / stats.TotalFrames:F1}%");
            }
        }

        /// <summary>
        /// 프레임 디코딩 성공을 기록합니다.
        /// </summary>
        public void LogDecodingSuccess(int clientId, long frameId, bool isKeyframe, double decodeTimeMs)
        {
            var stats = _clientStats.GetOrAdd(clientId, _ => new ClientFrameStats());
            stats.DecodedFrames++;
            stats.TotalDecodeTimeMs += decodeTimeMs;

            if (isKeyframe)
            {
                stats.DecodedKeyFrames++;
                _logger.Info($"{_prefix}키프레임 디코딩 성공: 클라이언트={clientId}, ID={frameId}, 시간={decodeTimeMs:F1}ms");
            }
        }

        /// <summary>
        /// 프레임 디코딩 실패를 기록합니다.
        /// </summary>
        public void LogDecodingFailure(int clientId, long frameId, bool isKeyframe)
        {
            var stats = _clientStats.GetOrAdd(clientId, _ => new ClientFrameStats());
            stats.FailedFrames++;

            if (isKeyframe)
            {
                stats.FailedKeyFrames++;
                _logger.Warning($"{_prefix}키프레임 디코딩 실패: 클라이언트={clientId}, ID={frameId}");
            }
            else
            {
                _logger.Debug($"{_prefix}프레임 디코딩 실패: 클라이언트={clientId}, ID={frameId}");
            }
        }

        /// <summary>
        /// 프레임 폐기를 기록합니다.
        /// </summary>
        public void LogFrameDropped(int clientId, long frameId, bool isKeyframe, string reason)
        {
            var stats = _clientStats.GetOrAdd(clientId, _ => new ClientFrameStats());
            stats.DroppedFrames++;

            if (isKeyframe)
            {
                stats.DroppedKeyFrames++;
                _logger.Warning($"{_prefix}키프레임 폐기: 클라이언트={clientId}, ID={frameId}, 이유={reason}");
            }
        }

        /// <summary>
        /// 키프레임 요청을 기록합니다.
        /// </summary>
        public void LogKeyframeRequested(int clientId, string reason)
        {
            var stats = _clientStats.GetOrAdd(clientId, _ => new ClientFrameStats());
            stats.KeyframeRequests++;
            _logger.Info($"{_prefix}키프레임 요청: 클라이언트={clientId}, 이유={reason}");
        }

        /// <summary>
        /// 통계 보고서를 생성합니다.
        /// </summary>
        private void ReportStatistics(object state)
        {
            foreach (var entry in _clientStats)
            {
                var clientId = entry.Key;
                var stats = entry.Value;

                if (stats.TotalFrames == 0) continue;

                // 통계 로깅
                _logger.Info($"{_prefix}프레임 통계 - 클라이언트 {clientId}: " +
                    $"총 프레임={stats.TotalFrames}, " +
                    $"키프레임={stats.KeyFrames} ({stats.KeyFrames * 100.0 / stats.TotalFrames:F1}%), " +
                    $"디코딩 성공={stats.DecodedFrames} ({stats.DecodedFrames * 100.0 / stats.TotalFrames:F1}%), " +
                    $"실패={stats.FailedFrames} ({stats.FailedFrames * 100.0 / stats.TotalFrames:F1}%), " +
                    $"폐기={stats.DroppedFrames} ({stats.DroppedFrames * 100.0 / stats.TotalFrames:F1}%), " +
                    $"키프레임 요청={stats.KeyframeRequests}, " +
                    $"평균 디코딩 시간={(stats.DecodedFrames > 0 ? stats.TotalDecodeTimeMs / stats.DecodedFrames : 0):F1}ms, " +
                    $"평균 키프레임 크기={(stats.KeyFrames > 0 ? stats.KeyFrameBytes / 1024.0 / stats.KeyFrames : 0):F1}KB");

                // 구간 통계 초기화
                stats.ResetIntervalStats();
            }
        }

        /// <summary>
        /// 리소스를 해제합니다.
        /// </summary>
        public void Dispose()
        {
            _reportingTimer?.Dispose();
        }

        /// <summary>
        /// 클라이언트별 통계 정보
        /// </summary>
        private class ClientFrameStats
        {
            // 누적 통계
            public long TotalFrames;
            public long KeyFrames;
            public long DecodedFrames;
            public long DecodedKeyFrames;
            public long FailedFrames;
            public long FailedKeyFrames;
            public long DroppedFrames;
            public long DroppedKeyFrames;
            public long KeyframeRequests;
            public long TotalBytes;
            public long KeyFrameBytes;
            public double TotalDecodeTimeMs;

            // 구간 통계 (보고 주기마다 초기화)
            public long IntervalFrames;
            public long IntervalKeyFrames;
            public long IntervalBytes;

            public void ResetIntervalStats()
            {
                IntervalFrames = 0;
                IntervalKeyFrames = 0;
                IntervalBytes = 0;
            }
        }
    }
}