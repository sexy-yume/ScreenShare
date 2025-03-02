using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using LiteNetLib;
using LiteNetLib.Utils;
using ScreenShare.Common.Models;
using ScreenShare.Common.Settings;
using ScreenShare.Common.Utils;

namespace ScreenShare.Host.Network
{
    public class NetworkServer : INetEventListener, IDisposable
    {
        // 네트워크 관련 필드
        private NetManager _netManager;
        private HostSettings _settings;
        private ConcurrentDictionary<int, NetPeer> _clientPeers;
        private ConcurrentDictionary<int, ClientInfo> _clientInfos;
        private ConcurrentDictionary<int, PerformanceTracker> _clientPerformance = new ConcurrentDictionary<int, PerformanceTracker>();

        // 큐 모니터링
        private ConcurrentDictionary<int, ConcurrentQueue<FrameProcessingTask>> _processingQueues =
            new ConcurrentDictionary<int, ConcurrentQueue<FrameProcessingTask>>();
        private int _maxQueueSize = 3;

        // 키프레임 요청 관련
        private ConcurrentDictionary<int, DateTime> _lastKeyframeRequestTime = new ConcurrentDictionary<int, DateTime>();
        private TimeSpan _minKeyframeRequestInterval = TimeSpan.FromSeconds(2); // 최소 2초 간격 (원래 5초에서 단축)
        private ConcurrentDictionary<int, int> _keyframeRequestCounter = new ConcurrentDictionary<int, int>();
        private int _maxKeyframeRequestsBeforeBackoff = 3; // 백오프 적용 전 최대 요청 횟수
        private TimeSpan _keyframeBackoffInterval = TimeSpan.FromSeconds(10); // 백오프 시간

        // 네트워크 통계
        private ConcurrentDictionary<int, NetworkStatsPacket> _lastClientStats = new ConcurrentDictionary<int, NetworkStatsPacket>();
        private Stopwatch _statsTimer = new Stopwatch();
        private TimeSpan _statsInterval = TimeSpan.FromSeconds(5); // 통계 보고 간격

        // 이벤트
        public event EventHandler<ClientEventArgs> ClientConnected;
        public event EventHandler<ClientEventArgs> ClientDisconnected;
        public event EventHandler<ScreenDataEventArgs> ScreenDataReceived;
        public event EventHandler<PerformanceEventArgs> PerformanceUpdated;

        public bool IsRunning { get; private set; }
        public ConcurrentDictionary<int, ClientInfo> Clients => _clientInfos;

        public NetworkServer(HostSettings settings)
        {
            _settings = settings;
            _clientPeers = new ConcurrentDictionary<int, NetPeer>();
            _clientInfos = new ConcurrentDictionary<int, ClientInfo>();

            _netManager = new NetManager(this)
            {
                UpdateTime = 15,
                UnconnectedMessagesEnabled = true,
                NatPunchEnabled = true,
                PingInterval = 500,    // 더 빈번한 핑
                DisconnectTimeout = 5000, // 5초 연결 타임아웃
                SimulatePacketLoss = false, // 테스트용 패킷 손실 시뮬레이션
                SimulationPacketLossChance = 0 // 패킷 손실 확률 (0-100)
            };

            _statsTimer.Start();

            EnhancedLogger.Instance.Info($"Network server initialized, max queue size: {_maxQueueSize}");
        }

        public void Start()
        {
            _netManager.Start(_settings.HostPort);
            IsRunning = true;
            EnhancedLogger.Instance.Info($"Network server started on port {_settings.HostPort}");
        }

        public void Stop()
        {
            _netManager.Stop();
            IsRunning = false;
            _clientPeers.Clear();
            _clientInfos.Clear();
            _clientPerformance.Clear();
            _processingQueues.Clear();
            _lastKeyframeRequestTime.Clear();
            _keyframeRequestCounter.Clear();
            _lastClientStats.Clear();
            EnhancedLogger.Instance.Info("Network server stopped");
        }

        public void Update()
        {
            _netManager.PollEvents();

            // 각 클라이언트의 성능 메트릭 업데이트
            foreach (var clientTracker in _clientPerformance)
            {
                if (clientTracker.Value.ShouldReportPerformance())
                {
                    var metrics = clientTracker.Value.GetMetrics();
                    PerformanceUpdated?.Invoke(this, new PerformanceEventArgs
                    {
                        ClientNumber = clientTracker.Key,
                        Metrics = metrics
                    });
                }
            }

            // 네트워크 통계 업데이트
            if (_statsTimer.Elapsed >= _statsInterval)
            {
                _statsTimer.Restart();
                SendNetworkStats();
            }
        }

        /// <summary>
        /// 네트워크 통계를 모든 클라이언트에게 보냅니다.
        /// </summary>
        private void SendNetworkStats()
        {
            try
            {
                // 모든 클라이언트에 대해
                foreach (var entry in _clientPeers)
                {
                    int clientNumber = entry.Key;
                    NetPeer peer = entry.Value;

                    if (peer != null && peer.ConnectionState == ConnectionState.Connected)
                    {
                        // 네트워크 통계 생성
                        var statsPacket = new NetworkStatsPacket
                        {
                            ClientNumber = clientNumber,
                            PacketLoss = (int)(peer.Statistics.PacketLossPercent * 100), // 패킷 손실 (0-100)
                            Rtt = peer.Ping, // 왕복 시간 (ms)
                            Bitrate = CalculateClientBitrate(clientNumber), // 비트레이트 (kbps)
                            QueueDepth = GetClientQueueDepth(clientNumber), // 큐 깊이
                            Timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds()
                        };

                        // 이전 통계와 비교하여 큰 변화가 있는 경우에만 전송
                        bool shouldSend = true;
                        if (_lastClientStats.TryGetValue(clientNumber, out var lastStats))
                        {
                            shouldSend =
                                Math.Abs(statsPacket.PacketLoss - lastStats.PacketLoss) > 5 || // 패킷 손실 5% 이상 변화
                                Math.Abs(statsPacket.Rtt - lastStats.Rtt) > 20 || // RTT 20ms 이상 변화
                                Math.Abs(statsPacket.Bitrate - lastStats.Bitrate) > 500 || // 비트레이트 500kbps 이상 변화
                                statsPacket.QueueDepth > lastStats.QueueDepth; // 큐 깊이 증가
                        }

                        if (shouldSend)
                        {
                            // 패킷 직렬화 및 전송
                            byte[] serializedData = PacketSerializer.Serialize(statsPacket);
                            peer.Send(serializedData, DeliveryMethod.ReliableOrdered);

                            // 마지막 통계 업데이트
                            _lastClientStats[clientNumber] = statsPacket;

                            // 네트워크 상태가 나쁘면 로그
                            if (statsPacket.PacketLoss > 5 || statsPacket.Rtt > 100)
                            {
                                EnhancedLogger.Instance.Warning(
                                    $"네트워크 상태 악화: 클라이언트={clientNumber}, " +
                                    $"패킷 손실={statsPacket.PacketLoss}%, RTT={statsPacket.Rtt}ms, " +
                                    $"큐 깊이={statsPacket.QueueDepth}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                EnhancedLogger.Instance.Error($"네트워크 통계 전송 오류: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 클라이언트의 비트레이트를 계산합니다.
        /// </summary>
        private int CalculateClientBitrate(int clientNumber)
        {
            if (_clientPerformance.TryGetValue(clientNumber, out var tracker))
            {
                return (int)(tracker.GetBitrateMbps() * 1000); // 지난 간격 동안의 평균 kbps
            }
            return 0;
        }

        /// <summary>
        /// 클라이언트의 큐 깊이를 가져옵니다.
        /// </summary>
        private int GetClientQueueDepth(int clientNumber)
        {
            if (_processingQueues.TryGetValue(clientNumber, out var queue))
            {
                return queue.Count;
            }
            return 0;
        }

        /// <summary>
        /// 클라이언트에 키프레임 요청을 보냅니다. 백오프 메커니즘 포함.
        /// </summary>
        public bool RequestKeyframe(int clientNumber)
        {
            if (!_clientPeers.TryGetValue(clientNumber, out var peer))
                return false;

            // 현재 시간
            DateTime now = DateTime.UtcNow;

            // 이 클라이언트에 대한 마지막 요청 시간
            DateTime lastRequest = _lastKeyframeRequestTime.GetOrAdd(clientNumber, DateTime.MinValue);

            // 요청 카운터 가져오기
            int requestCount = _keyframeRequestCounter.GetOrAdd(clientNumber, 0);

            // 요청 간격 결정 (백오프 적용)
            TimeSpan requestInterval = _minKeyframeRequestInterval;
            if (requestCount >= _maxKeyframeRequestsBeforeBackoff)
            {
                requestInterval = _keyframeBackoffInterval;
                EnhancedLogger.Instance.Debug(
                    $"키프레임 요청 백오프 적용: 클라이언트={clientNumber}, " +
                    $"카운터={requestCount}, 간격={requestInterval.TotalSeconds:F1}초");
            }

            // 최소 간격 확인
            if ((now - lastRequest) < requestInterval)
            {
                double waitSeconds = (lastRequest + requestInterval - now).TotalSeconds;
                EnhancedLogger.Instance.Debug(
                    $"키프레임 요청 간격 제한: 클라이언트={clientNumber}, " +
                    $"마지막 요청 후 {(now - lastRequest).TotalSeconds:F1}초, " +
                    $"대기 필요={waitSeconds:F1}초");
                return false;
            }

            try
            {
                // KeyframeRequest 패킷 생성
                var packet = new ScreenPacket
                {
                    Type = PacketType.KeyframeRequest,
                    ClientNumber = clientNumber,
                    Timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds()
                };

                // 패킷 직렬화 및 전송 (확실한 전달을 위해 ReliableOrdered 사용)
                byte[] serializedData = PacketSerializer.Serialize(packet);
                peer.Send(serializedData, DeliveryMethod.ReliableOrdered);

                // 요청 카운터 증가
                _keyframeRequestCounter[clientNumber] = requestCount + 1;

                // 마지막 요청 시간 업데이트
                _lastKeyframeRequestTime[clientNumber] = now;

                EnhancedLogger.Instance.Info(
                    $"클라이언트 {clientNumber}에 키프레임 요청 #{requestCount + 1} 전송");
                return true;
            }
            catch (Exception ex)
            {
                EnhancedLogger.Instance.Error($"키프레임 요청 전송 오류: {ex.Message}", ex);
                return false;
            }
        }

        public bool RequestKeyframe(int clientNumber, string reason)
        {
            EnhancedLogger.Instance.Info($"키프레임 요청 이유: {reason}");
            return RequestKeyframe(clientNumber);
        }
        /// <summary>
        /// 키프레임 카운터를 리셋합니다 (키프레임 수신 후 호출).
        /// </summary>
        private void ResetKeyframeRequestCounter(int clientNumber)
        {
            _keyframeRequestCounter[clientNumber] = 0;
            EnhancedLogger.Instance.Debug($"클라이언트 {clientNumber}의 키프레임 요청 카운터 리셋");
        }

        // 원격 제어 메서드
        public bool SendRemoteControlRequest(int clientNumber)
        {
            if (!_clientPeers.TryGetValue(clientNumber, out var peer))
                return false;

            var packet = new ScreenPacket
            {
                Type = PacketType.RemoteControl,
                ClientNumber = clientNumber,
                Timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds()
            };

            byte[] serializedData = PacketSerializer.Serialize(packet);
            peer.Send(serializedData, DeliveryMethod.ReliableOrdered);

            // 클라이언트 상태 업데이트
            if (_clientInfos.TryGetValue(clientNumber, out var clientInfo))
            {
                clientInfo.IsRemoteControlActive = true;
            }

            return true;
        }

        public bool SendRemoteControlEnd(int clientNumber)
        {
            if (!_clientPeers.TryGetValue(clientNumber, out var peer))
                return false;

            var packet = new ScreenPacket
            {
                Type = PacketType.RemoteEnd,
                ClientNumber = clientNumber,
                Timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds()
            };

            byte[] serializedData = PacketSerializer.Serialize(packet);
            peer.Send(serializedData, DeliveryMethod.ReliableOrdered);

            // 클라이언트 상태 업데이트
            if (_clientInfos.TryGetValue(clientNumber, out var clientInfo))
            {
                clientInfo.IsRemoteControlActive = false;
            }

            return true;
        }

        public bool SendMouseMove(int clientNumber, int x, int y)
        {
            if (!_clientPeers.TryGetValue(clientNumber, out var peer))
                return false;

            var packet = new ScreenPacket
            {
                Type = PacketType.MouseMove,
                ClientNumber = clientNumber,
                MouseX = x,
                MouseY = y,
                Timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds()
            };

            byte[] serializedData = PacketSerializer.Serialize(packet);
            peer.Send(serializedData, DeliveryMethod.ReliableOrdered);
            return true;
        }

        public bool SendMouseClick(int clientNumber, int x, int y, int button)
        {
            if (!_clientPeers.TryGetValue(clientNumber, out var peer))
                return false;

            var packet = new ScreenPacket
            {
                Type = PacketType.MouseClick,
                ClientNumber = clientNumber,
                MouseX = x,
                MouseY = y,
                MouseButton = button,
                Timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds()
            };

            byte[] serializedData = PacketSerializer.Serialize(packet);
            peer.Send(serializedData, DeliveryMethod.ReliableOrdered);
            return true;
        }

        public bool SendKeyPress(int clientNumber, int keyCode)
        {
            if (!_clientPeers.TryGetValue(clientNumber, out var peer))
                return false;

            var packet = new ScreenPacket
            {
                Type = PacketType.KeyPress,
                ClientNumber = clientNumber,
                KeyCode = keyCode,
                Timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds()
            };

            byte[] serializedData = PacketSerializer.Serialize(packet);
            peer.Send(serializedData, DeliveryMethod.ReliableOrdered);
            return true;
        }

        // 프레임 처리 추적 메서드
        public void BeginFrameProcessing(int clientNumber, long frameId)
        {
            var queue = _processingQueues.GetOrAdd(clientNumber, _ => new ConcurrentQueue<FrameProcessingTask>());

            // 처리 큐에 추가
            var task = new FrameProcessingTask
            {
                FrameId = frameId,
                StartTime = Stopwatch.GetTimestamp()
            };

            queue.Enqueue(task);

            // 큐 크기 제한
            while (queue.Count > _maxQueueSize)
            {
                if (queue.TryDequeue(out var oldTask))
                {
                    EnhancedLogger.Instance.Debug(
                        $"클라이언트 {clientNumber}의 프레임 {oldTask.FrameId}을(를) 큐 오버플로우로 인해 제거");
                }
            }

            // 큐 깊이 추적
            if (_clientPerformance.TryGetValue(clientNumber, out var tracker))
            {
                tracker.UpdateQueueDepth(queue.Count);

                // 큐가 계속 커지면 키프레임 요청 고려
                if (queue.Count >= _maxQueueSize)
                {
                    EnhancedLogger.Instance.Warning(
                        $"클라이언트 {clientNumber}의 처리 큐가 최대값에 도달: {queue.Count}");
                }
            }
        }

        // 프레임 처리 완료 및 응답 전송
        public void EndFrameProcessing(int clientNumber, long frameId)
        {
            if (!_processingQueues.TryGetValue(clientNumber, out var queue))
                return;

            // 큐에서 프레임 찾기
            var frameList = new FrameProcessingTask[queue.Count];
            int count = 0;
            bool found = false;

            // 프레임을 찾거나 큐가 비워질 때까지 디큐
            while (queue.TryDequeue(out var task))
            {
                if (task.FrameId == frameId)
                {
                    // 처리 시간 계산
                    long endTime = Stopwatch.GetTimestamp();
                    long elapsedTicks = endTime - task.StartTime;
                    long elapsedMicroseconds = elapsedTicks * 1_000_000 / Stopwatch.Frequency;

                    // 응답 전송
                    SendFrameAcknowledgment(clientNumber, frameId, (int)elapsedMicroseconds, queue.Count);
                    found = true;

                    // 성능 추적기 업데이트
                    if (_clientPerformance.TryGetValue(clientNumber, out var tracker))
                    {
                        tracker.AddProcessingTime(elapsedMicroseconds);
                        tracker.UpdateQueueDepth(queue.Count);
                    }

                    break;
                }

                // 필요한 경우 다시 큐에 넣기 위해 저장
                frameList[count++] = task;
            }

            // 타겟을 찾기 전에 디큐한 항목을 다시 큐에 넣기
            for (int i = 0; i < count; i++)
            {
                queue.Enqueue(frameList[i]);
            }

            if (!found)
            {
                EnhancedLogger.Instance.Debug(
                    $"클라이언트 {clientNumber}의 처리 큐에서 프레임 {frameId}을(를) 찾을 수 없음");
            }
        }

        // 프레임 응답 전송
        private void SendFrameAcknowledgment(int clientNumber, long frameId, long processingTime, int queueLength)
        {
            if (!_clientPeers.TryGetValue(clientNumber, out var peer))
                return;

            try
            {
                // FrameAck 패킷 생성
                var screenPacket = new ScreenPacket
                {
                    Type = PacketType.FrameAck,
                    ClientNumber = clientNumber,
                    FrameId = frameId,
                    Timestamp = processingTime,  // 처리 시간을 타임스탬프 필드에 저장
                    Width = queueLength,         // 큐 길이를 너비 필드에 저장
                    Height = (int)(peer.Statistics.PacketLossPercent * 100)  // 패킷 손실을 높이 필드에 저장 (스케일링)
                };

                // 패킷 직렬화 및 전송
                byte[] serializedData = PacketSerializer.Serialize(screenPacket);
                peer.Send(serializedData, DeliveryMethod.ReliableOrdered);

                EnhancedLogger.Instance.Debug(
                    $"클라이언트 {clientNumber}에 프레임 응답 전송: id={frameId}, " +
                    $"시간={processingTime/1000.0}ms, 큐={queueLength}");
            }
            catch (Exception ex)
            {
                EnhancedLogger.Instance.Error($"프레임 응답 전송 오류: {ex.Message}", ex);
            }
        }

        public void OnPeerConnected(NetPeer peer)
        {
            EnhancedLogger.Instance.Info($"클라이언트 연결됨: {peer.EndPoint}");
        }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            EnhancedLogger.Instance.Info($"클라이언트 연결 끊김: {disconnectInfo.Reason}");

            // 클라이언트 찾기
            foreach (var client in _clientPeers)
            {
                if (client.Value.Id == peer.Id)
                {
                    _clientPeers.TryRemove(client.Key, out _);
                    _clientInfos.TryRemove(client.Key, out var clientInfo);
                    _clientPerformance.TryRemove(client.Key, out _);
                    _processingQueues.TryRemove(client.Key, out _);
                    _lastKeyframeRequestTime.TryRemove(client.Key, out _);
                    _keyframeRequestCounter.TryRemove(client.Key, out _);
                    _lastClientStats.TryRemove(client.Key, out _);

                    ClientDisconnected?.Invoke(this, new ClientEventArgs(client.Key, clientInfo));
                    break;
                }
            }
        }

        public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            EnhancedLogger.Instance.Error($"네트워크 오류: {endPoint} - {socketError}", null);
        }

        // 패킷 수신 처리
        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            try
            {
                byte[] data = reader.GetRemainingBytes();
                var packet = PacketSerializer.Deserialize<ScreenPacket>(data);

                switch (packet.Type)
                {
                    case PacketType.Connect:
                        // 클라이언트 등록
                        _clientPeers[packet.ClientNumber] = peer;

                        var clientInfo = new ClientInfo
                        {
                            ClientNumber = packet.ClientNumber,
                            ClientIp = peer.EndPoint.Address.ToString(),
                            ClientPort = peer.EndPoint.Port,
                            IsRemoteControlActive = false
                        };

                        _clientInfos[packet.ClientNumber] = clientInfo;

                        // 이 클라이언트의 성능 트래커 초기화
                        _clientPerformance[packet.ClientNumber] = new PerformanceTracker();

                        ClientConnected?.Invoke(this, new ClientEventArgs(packet.ClientNumber, clientInfo));

                        EnhancedLogger.Instance.Info(
                            $"클라이언트 {packet.ClientNumber} 등록됨: {clientInfo.ClientIp}:{clientInfo.ClientPort}");
                        break;

                    case PacketType.ScreenData:
                        if (_clientInfos.TryGetValue(packet.ClientNumber, out var info))
                        {
                            info.ScreenWidth = packet.Width;
                            info.ScreenHeight = packet.Height;

                            long frameId = packet.FrameId;
                            bool isKeyFrame = packet.IsKeyFrame;

                            EnhancedLogger.Instance.Debug(
                                $"클라이언트 {packet.ClientNumber}로부터 프레임 수신: " +
                                $"id={frameId}, 키프레임={isKeyFrame}, 크기={packet.ScreenData.Length}");

                            // 키프레임인 경우 카운터 리셋
                            if (isKeyFrame)
                            {
                                EnhancedLogger.Instance.Info(
                                    $"클라이언트 {packet.ClientNumber}로부터 키프레임 수신: id={frameId}");
                                ResetKeyframeRequestCounter(packet.ClientNumber);
                            }

                            // 프레임 처리 추적 시작
                            BeginFrameProcessing(packet.ClientNumber, frameId);

                            // 프레임 크기 추적
                            if (_clientPerformance.TryGetValue(packet.ClientNumber, out var tracker))
                            {
                                tracker.AddFrame(data.Length);
                            }

                            // 처리를 위한 이벤트 발생
                            ScreenDataReceived?.Invoke(this, new ScreenDataEventArgs(
                                packet.ClientNumber,
                                packet.ScreenData,
                                packet.Width,
                                packet.Height,
                                frameId,
                                isKeyFrame));
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                EnhancedLogger.Instance.Error($"패킷 처리 오류: {ex.Message}", ex);
            }
            finally
            {
                reader.Recycle();
            }
        }

        // 필수 인터페이스 메서드
        public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
        {
            // 이 애플리케이션에서는 사용하지 않음
        }

        public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
        {
            // 레이턴시가 높은 경우 로그
            if (latency > 150)
            {
                // 이 클라이언트 식별
                int clientNumber = -1;
                foreach (var client in _clientPeers)
                {
                    if (client.Value.Id == peer.Id)
                    {
                        clientNumber = client.Key;
                        break;
                    }
                }

                if (clientNumber >= 0)
                {
                    EnhancedLogger.Instance.Warning(
                        $"클라이언트 {clientNumber}의 높은 네트워크 레이턴시: {latency}ms");

                    // 레이턴시가 매우 높으면 키프레임 요청 고려
                    if (latency > 300)
                    {
                        RequestKeyframe(clientNumber, "높은 레이턴시");
                    }
                }
            }
        }

        public void OnConnectionRequest(ConnectionRequest request)
        {
            // 모든 연결 요청 수락
            request.Accept();
        }

        public void Dispose()
        {
            Stop();
        }
    }

    // 프레임 처리 작업 추적 클래스
    public class FrameProcessingTask
    {
        public long FrameId { get; set; }
        public long StartTime { get; set; } // Stopwatch.GetTimestamp()로부터의 타임스탬프
    }

    // 클라이언트 성능 추적 클래스
    public class PerformanceTracker
    {
        private readonly Stopwatch _uptime = Stopwatch.StartNew();
        private long _totalFrames = 0;
        private long _totalBytes = 0;
        private long _lastReportTime = 0;
        private TimeSpan _reportInterval = TimeSpan.FromSeconds(5);
        private int _currentQueueDepth = 0;
        private long _totalProcessingTime = 0; // 마이크로초
        private int _maxQueueDepth = 0;

        // 현재 간격에 대한 메트릭
        private long _intervalFrames = 0;
        private long _intervalBytes = 0;
        private long _intervalProcessingTime = 0;

        // 비트레이트 계산용 이동 평균
        private readonly int[] _recentBitrates = new int[5]; // 최근 5개 간격의 비트레이트 저장
        private int _bitrateIndex = 0;

        public void AddFrame(int frameSize)
        {
            _totalFrames++;
            _totalBytes += frameSize;
            _intervalFrames++;
            _intervalBytes += frameSize;
        }

        public void AddProcessingTime(long microseconds)
        {
            _totalProcessingTime += microseconds;
            _intervalProcessingTime += microseconds;
        }

        public void UpdateQueueDepth(int depth)
        {
            _currentQueueDepth = depth;
            _maxQueueDepth = Math.Max(_maxQueueDepth, depth);
        }

        public bool ShouldReportPerformance()
        {
            long now = _uptime.ElapsedMilliseconds;
            return (now - _lastReportTime) >= _reportInterval.TotalMilliseconds;
        }

        public double GetBitrateMbps()
        {
            // 비트레이트 이동 평균 계산
            double sum = _recentBitrates.Sum();
            int count = _recentBitrates.Count(b => b > 0);
            return count > 0 ? sum / count : 0;
        }

        public ClientPerformanceMetrics GetMetrics()
        {
            long now = _uptime.ElapsedMilliseconds;
            long elapsed = now - _lastReportTime;

            // 비트레이트 계산 및 저장
            double mbps = elapsed > 0 ? (_intervalBytes * 8.0 / elapsed / 1000.0) : 0;
            _recentBitrates[_bitrateIndex] = (int)mbps;
            _bitrateIndex = (_bitrateIndex + 1) % _recentBitrates.Length;

            var metrics = new ClientPerformanceMetrics
            {
                TotalFrames = _totalFrames,
                TotalBytes = _totalBytes,
                AverageFps = elapsed > 0 ? (_intervalFrames * 1000.0 / elapsed) : 0,
                AverageBitrateMbps = mbps,
                CurrentQueueDepth = _currentQueueDepth,
                MaxQueueDepth = _maxQueueDepth,
                AverageProcessingTimeMs = _intervalFrames > 0 ? (_intervalProcessingTime / 1000.0 / _intervalFrames) : 0
            };

            // 간격 메트릭 초기화
            _lastReportTime = now;
            _intervalFrames = 0;
            _intervalBytes = 0;
            _intervalProcessingTime = 0;
            _maxQueueDepth = _currentQueueDepth;

            return metrics;
        }
    }
}