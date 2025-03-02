using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Diagnostics;
using System.Collections.Concurrent;
using LiteNetLib;
using LiteNetLib.Utils;
using ScreenShare.Common.Models;
using ScreenShare.Common.Settings;
using ScreenShare.Common.Utils;

namespace ScreenShare.Client.Network
{
    public class NetworkClient : INetEventListener, IDisposable
    {
        // 네트워크 관련 필드
        private NetManager _netManager;
        private NetPeer _server;
        private ClientSettings _settings;
        private bool _isRemoteControlActive;
        private readonly object _sendLock = new object();
        private bool _isConnecting = false;
        private Stopwatch _connectionTimer = new Stopwatch();
        private Stopwatch _sendStatsTimer = new Stopwatch();
        private long _bytesSent = 0;
        private int _framesSent = 0;

        // 적응형 프레임 관리
        private AdaptiveFrameManager _adaptiveManager;
        private ConcurrentDictionary<long, DateTime> _pendingFrames = new ConcurrentDictionary<long, DateTime>();
        private ConcurrentDictionary<long, bool> _keyframeMap = new ConcurrentDictionary<long, bool>(); // 키프레임 추적
        private long _nextFrameId = 1;
        private PerformanceMetrics _metrics = new PerformanceMetrics();

        // 재연결 관리
        private DateTime _lastReconnectAttempt = DateTime.MinValue;
        private int _reconnectAttemptCount = 0;
        private TimeSpan _maxReconnectInterval = TimeSpan.FromSeconds(30);
        private TimeSpan _initialReconnectDelay = TimeSpan.FromSeconds(1);

        // 네트워크 상태 추적
        private ConcurrentDictionary<long, FrameAckPacket> _recentAcks = new ConcurrentDictionary<long, FrameAckPacket>();
        private int _consecutiveTimeouts = 0;
        private const int MaxConsecutiveTimeouts = 3;
        private Stopwatch _lastAckReceived = new Stopwatch();
        private bool _keyframeRequested = false;

        // 이벤트
        public event EventHandler<bool> RemoteControlStatusChanged;
        public event EventHandler<ScreenPacket> RemoteControlReceived;
        public event EventHandler<bool> ConnectionStatusChanged;
        public event EventHandler<PerformanceMetrics> PerformanceUpdated;

        public bool IsConnected => _server != null && _server.ConnectionState == ConnectionState.Connected;
        public bool IsRemoteControlActive => _isRemoteControlActive;
        public AdaptiveFrameManager AdaptiveManager => _adaptiveManager;

        public NetworkClient(ClientSettings settings)
        {
            _settings = settings;
            _netManager = new NetManager(this)
            {
                UpdateTime = 15,
                UnconnectedMessagesEnabled = true,
                NatPunchEnabled = true,
                ReconnectDelay = 500,
                MaxConnectAttempts = 10,
                PingInterval = 500,       // 더 빈번한 핑 (1000ms에서 500ms로 변경)
                DisconnectTimeout = 5000
            };

            _sendStatsTimer.Start();
            _lastAckReceived.Start();

            // 적응형 프레임 관리자 초기화
            _adaptiveManager = new AdaptiveFrameManager(
                settings.LowResFps,
                settings.LowResQuality,
                10_000_000);  // 초기 비트레이트: 10 Mbps

            _adaptiveManager.SettingsChanged += OnAdaptiveSettingsChanged;
        }

        // 적응형 설정 변경 처리
        private void OnAdaptiveSettingsChanged(object sender, AdaptiveSettingsChangedEventArgs e)
        {
            // 메트릭 업데이트
            _metrics.TargetFps = e.TargetFps;
            _metrics.EncodingQuality = e.Quality;
            _metrics.TargetBitrate = e.Bitrate;

            // 구독자에게 알림
            PerformanceUpdated?.Invoke(this, _metrics);
        }

        public void Start()
        {
            EnhancedLogger.Instance.Info("Network client starting");
            _netManager.Start();
            Connect();
        }

        // 서버 연결
        public void Connect()
        {
            if (_isConnecting)
                return;

            _isConnecting = true;
            _connectionTimer.Restart();
            _reconnectAttemptCount++;

            EnhancedLogger.Instance.Info($"Connecting to host: {_settings.HostIp}:{_settings.HostPort} (Attempt {_reconnectAttemptCount})");
            _netManager.Connect(_settings.HostIp, _settings.HostPort, "ScreenShare");

            // 연결 모니터링 스레드
            new Thread(() => {
                while (_isConnecting && _connectionTimer.ElapsedMilliseconds < 10000)
                {
                    EnhancedLogger.Instance.Info($"Waiting for connection... ({_connectionTimer.ElapsedMilliseconds / 1000}s)");
                    Thread.Sleep(1000);
                }
                _isConnecting = false;
                // 연결 실패 시 재연결 예약
                if (!IsConnected)
                {
                    ScheduleReconnect();
                }
            })
            { IsBackground = true }.Start();
        }

        // 재연결 예약
        private void ScheduleReconnect()
        {
            try
            {
                // 백오프 계산 (지수 백오프 적용)
                double backoffFactor = Math.Min(Math.Pow(1.5, _reconnectAttemptCount - 1), 10);
                TimeSpan delay = TimeSpan.FromMilliseconds(_initialReconnectDelay.TotalMilliseconds * backoffFactor);

                // 최대 간격 제한
                if (delay > _maxReconnectInterval)
                    delay = _maxReconnectInterval;

                _lastReconnectAttempt = DateTime.UtcNow;

                EnhancedLogger.Instance.Info($"Scheduling reconnect in {delay.TotalSeconds:F1} seconds (attempt {_reconnectAttemptCount})");

                // 재연결 타이머
                System.Threading.Timer reconnectTimer = null;
                reconnectTimer = new System.Threading.Timer(_ => {
                    try
                    {
                        reconnectTimer?.Dispose();
                        Connect();
                    }
                    catch (Exception ex)
                    {
                        EnhancedLogger.Instance.Error($"Reconnect error: {ex.Message}", ex);
                    }
                }, null, delay, Timeout.InfiniteTimeSpan);
            }
            catch (Exception ex)
            {
                EnhancedLogger.Instance.Error($"Error scheduling reconnect: {ex.Message}", ex);
            }
        }

        public void Stop()
        {
            EnhancedLogger.Instance.Info("Network client stopping");
            _isConnecting = false;
            _netManager.Stop();
            _adaptiveManager?.Dispose();
        }

        // 화면 데이터 전송 (키프레임 인식 개선)
        public bool SendScreenData(byte[] data, int width, int height, bool isKeyFrame = false)
        {
            if (!IsConnected || data == null || data.Length == 0)
                return false;

            // 네트워크 상태 확인 및 키프레임 강제 요청
            if (!isKeyFrame && CheckKeyframeNeeded())
            {
                EnhancedLogger.Instance.Info("네트워크 상태로 인한 키프레임 강제 요청");
                return false; // 현재 프레임은 건너뛰고 키프레임이 생성되길 기다림
            }

            // 적응형 관리자에게 프레임 전송 가능 여부 확인
            if (!_adaptiveManager.BeginFrameSend(isKeyFrame))
            {
                // 적응형 속도 제어에 따라 이 프레임 건너뛰기
                return false;
            }

            lock (_sendLock)
            {
                try
                {
                    // 프레임 ID 생성 및 추적
                    long frameId = _nextFrameId++;

                    // 키프레임 추적
                    if (isKeyFrame)
                    {
                        _keyframeMap[frameId] = true;
                        _adaptiveManager.RegisterKeyframe(frameId);
                        _keyframeRequested = false; // 키프레임 요청 플래그 초기화
                        EnhancedLogger.Instance.Info($"키프레임 전송: id={frameId}, size={data.Length}");
                    }

                    // RTT 계산을 위해 전송 시간 저장
                    _pendingFrames[frameId] = DateTime.UtcNow;

                    // 프레임 ID와 키프레임 정보를 포함한 패킷 생성
                    var packet = new ScreenPacket
                    {
                        Type = PacketType.ScreenData,
                        ClientNumber = _settings.ClientNumber,
                        ScreenData = data,
                        Width = width,
                        Height = height,
                        Timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                        FrameId = frameId,
                        IsKeyFrame = isKeyFrame
                    };

                    byte[] serializedData = PacketSerializer.Serialize(packet);
                    _server.Send(serializedData, DeliveryMethod.ReliableOrdered);

                    // 주기적으로 프레임 전송 로깅
                    if (frameId % 30 == 0 || isKeyFrame)
                    {
                        EnhancedLogger.Instance.Debug(
                            $"프레임 전송: id={frameId}, size={serializedData.Length}, " +
                            $"키프레임={isKeyFrame}, 대기중={_pendingFrames.Count}");
                    }

                    _bytesSent += serializedData.Length;
                    _framesSent++;

                    // 메트릭 업데이트
                    _metrics.LastFrameSize = serializedData.Length;
                    _metrics.TotalBytesSent += serializedData.Length;
                    _metrics.TotalFramesSent++;

                    // 주기적으로 네트워크 통계 로깅
                    if (_sendStatsTimer.ElapsedMilliseconds >= 5000)
                    {
                        double seconds = _sendStatsTimer.ElapsedMilliseconds / 1000.0;
                        double mbps = (_bytesSent * 8.0 / 1_000_000.0) / seconds;
                        double fps = _framesSent / seconds;

                        _metrics.CurrentBitrateMbps = mbps;
                        _metrics.CurrentFps = fps;
                        _metrics.Ping = _server?.Ping ?? 0;

                        EnhancedLogger.Instance.Info(
                            $"네트워크 통계: {mbps:F2} Mbps, {fps:F1} fps, RTT: {_metrics.Ping}ms, " +
                            $"대기중: {_pendingFrames.Count}, 패킷 손실: {(_server?.Statistics.PacketLossPercent ?? 0):F1}%");

                        // 패킷 손실 정보로 적응형 관리자 업데이트
                        _adaptiveManager.UpdatePacketLoss((int)(_server?.Statistics.PacketLossPercent ?? 0));

                        // 이벤트 구독자들에게 알림
                        PerformanceUpdated?.Invoke(this, _metrics);

                        _bytesSent = 0;
                        _framesSent = 0;
                        _sendStatsTimer.Restart();
                    }

                    // 마지막 응답 시간 확인 및 타임아웃 처리
                    CheckResponseTimeout();

                    return true;
                }
                catch (Exception ex)
                {
                    EnhancedLogger.Instance.Error($"데이터 전송 오류: {ex.Message}", ex);
                    return false;
                }
            }
        }

        // 키프레임이 필요한지 확인
        private bool CheckKeyframeNeeded()
        {
            // 적응형 관리자의 판단 우선 확인
            if (_adaptiveManager.ShouldForceKeyframe)
            {
                return true;
            }

            // 키프레임 요청 확인
            if (_keyframeRequested)
            {
                return true;
            }

            // 응답 타임아웃 확인
            if (_lastAckReceived.ElapsedMilliseconds > 5000 && _pendingFrames.Count > 0)
            {
                _consecutiveTimeouts++;
                if (_consecutiveTimeouts >= MaxConsecutiveTimeouts)
                {
                    EnhancedLogger.Instance.Warning(
                        $"응답 타임아웃 {_consecutiveTimeouts}회 연속 발생, 키프레임 요청");
                    _consecutiveTimeouts = 0;
                    return true;
                }
            }

            return false;
        }

        // 응답 타임아웃 확인
        private void CheckResponseTimeout()
        {
            // 5초 동안 응답이 없으면 재연결 고려
            if (_lastAckReceived.ElapsedMilliseconds > 5000 && _pendingFrames.Count > 10)
            {
                EnhancedLogger.Instance.Warning(
                    $"응답 타임아웃: {_lastAckReceived.ElapsedMilliseconds}ms, 보류 프레임: {_pendingFrames.Count}");

                // 강제 키프레임 설정
                _keyframeRequested = true;

                // 연속 타임아웃 카운터 증가
                _consecutiveTimeouts++;

                // 연속 타임아웃이 너무 많으면 연결 복구 시도
                if (_consecutiveTimeouts >= MaxConsecutiveTimeouts && !_isConnecting && IsConnected)
                {
                    EnhancedLogger.Instance.Error("연속 타임아웃으로 인한 연결 복구 시도");
                    _server?.Disconnect();
                    _consecutiveTimeouts = 0;
                }
            }
        }

        public void Update()
        {
            _netManager.PollEvents();

            // 시간이 초과된 프레임 처리 (5초 이상 응답 없음)
            var now = DateTime.UtcNow;
            int timedOutFrames = 0;

            foreach (var frame in _pendingFrames)
            {
                if ((now - frame.Value).TotalSeconds > 5)
                {
                    if (_pendingFrames.TryRemove(frame.Key, out _))
                    {
                        timedOutFrames++;
                        EnhancedLogger.Instance.Debug($"프레임 {frame.Key} 응답 대기 타임아웃");
                    }
                }
            }

            // 많은 타임아웃 발생 시 로그
            if (timedOutFrames > 5)
            {
                EnhancedLogger.Instance.Warning($"{timedOutFrames}개 프레임 타임아웃 - 네트워크 상태 불안정");
                _keyframeRequested = true; // 키프레임 요청 플래그 설정
            }
        }

        public void OnPeerConnected(NetPeer peer)
        {
            _server = peer;
            _isConnecting = false;
            _reconnectAttemptCount = 0;
            EnhancedLogger.Instance.Info(
                $"Host connected: {peer.EndPoint}, connection time: {_connectionTimer.ElapsedMilliseconds}ms");

            // 초기 연결 정보 전송
            var packet = new ScreenPacket
            {
                Type = PacketType.Connect,
                ClientNumber = _settings.ClientNumber,
                Timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds()
            };

            byte[] serializedData = PacketSerializer.Serialize(packet);
            _server.Send(serializedData, DeliveryMethod.ReliableOrdered);
            ConnectionStatusChanged?.Invoke(this, true);

            // 마지막 응답 타이머 초기화
            _lastAckReceived.Restart();
            _consecutiveTimeouts = 0;

            // 연결 성공 후 즉시 키프레임 요청
            _keyframeRequested = true;
        }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            EnhancedLogger.Instance.Info($"Host disconnected: {disconnectInfo.Reason}");
            _server = null;

            // 원격 제어 모드 비활성화
            SetRemoteControlMode(false);
            ConnectionStatusChanged?.Invoke(this, false);

            // 재연결 예약
            ScheduleReconnect();
        }

        public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            EnhancedLogger.Instance.Error($"Network error: {endPoint} - {socketError}", null);
        }

        // 패킷 수신 처리 (프레임 응답 추적 개선)
        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            try
            {
                byte[] data = reader.GetRemainingBytes();
                var packet = PacketSerializer.Deserialize<ScreenPacket>(data);

                // 마지막 응답 타이머 재설정
                _lastAckReceived.Restart();
                _consecutiveTimeouts = 0;

                // 키프레임 요청 처리
                if (packet.Type == PacketType.KeyframeRequest)
                {
                    EnhancedLogger.Instance.Info("호스트로부터 키프레임 요청 수신");
                    _keyframeRequested = true;
                    _adaptiveManager.HandleKeyframeRequest();
                    return;
                }

                // 프레임 응답 처리
                if (packet.Type == PacketType.FrameAck)
                {
                    HandleFrameAcknowledgment(packet);
                    return;
                }

                // 네트워크 통계 처리
                if (packet.Type == PacketType.NetworkStats)
                {
                    var statsPacket = PacketSerializer.Deserialize<NetworkStatsPacket>(data);
                    HandleNetworkStats(statsPacket);
                    return;
                }

                // 기타 패킷 유형 처리
                switch (packet.Type)
                {
                    case PacketType.RemoteControl:
                        EnhancedLogger.Instance.Info("Remote control request received");
                        SetRemoteControlMode(true);
                        break;

                    case PacketType.RemoteEnd:
                        EnhancedLogger.Instance.Info("Remote control end received");
                        SetRemoteControlMode(false);
                        break;

                    case PacketType.MouseMove:
                    case PacketType.MouseClick:
                    case PacketType.KeyPress:
                        if (_isRemoteControlActive)
                        {
                            RemoteControlReceived?.Invoke(this, packet);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                EnhancedLogger.Instance.Error($"Packet processing error: {ex.Message}", ex);
            }
            finally
            {
                reader.Recycle();
            }
        }

        // 프레임 응답 처리 (세부 정보 추출)
        private void HandleFrameAcknowledgment(ScreenPacket packet)
        {
            try
            {
                EnhancedLogger.Instance.Debug(
                    $"프레임 응답: id={packet.FrameId}, " +
                    $"처리시간={packet.Timestamp/1000.0:F2}ms, " +
                    $"큐={packet.Width}, 손실={packet.Height/100.0:F1}%");

                // 프레임 응답 생성
                var ackInfo = new FrameAckPacket
                {
                    FrameId = packet.FrameId,
                    HostQueueLength = packet.Width,
                    HostProcessingTime = packet.Timestamp,
                    PacketLoss = packet.Height
                };

                // 응답 정보 저장
                _recentAcks[packet.FrameId] = ackInfo;

                // 대기 중인 프레임에서 제거 및 RTT 계산
                if (_pendingFrames.TryRemove(packet.FrameId, out DateTime sentTime))
                {
                    int rtt = (int)(DateTime.UtcNow - sentTime).TotalMilliseconds;
                    ackInfo.RoundTripTime = rtt;

                    // 적응형 관리자 업데이트
                    _adaptiveManager.FrameAcknowledged(
                        packet.FrameId,
                        rtt,
                        packet.Width,  // 호스트 큐 길이
                        packet.Timestamp // 처리 시간 (마이크로초)
                    );

                    // 메트릭 업데이트
                    _metrics.Ping = rtt;
                    _metrics.HostQueueLength = packet.Width;
                    _metrics.HostProcessingTime = packet.Timestamp / 1000.0; // 마이크로초 -> 밀리초
                    _metrics.PacketLoss = packet.Height / 100;  // 스케일링됨

                    // 타임아웃 카운터 초기화
                    _consecutiveTimeouts = 0;
                }
                else
                {
                    EnhancedLogger.Instance.Debug($"알 수 없는 프레임 응답: id={packet.FrameId}");
                }
            }
            catch (Exception ex)
            {
                EnhancedLogger.Instance.Error($"프레임 응답 처리 오류: {ex.Message}", ex);
            }
        }

        // 네트워크 통계 처리
        private void HandleNetworkStats(NetworkStatsPacket packet)
        {
            try
            {
                EnhancedLogger.Instance.Debug(
                    $"네트워크 통계: 손실={packet.PacketLoss}%, RTT={packet.Rtt}ms, " +
                    $"비트레이트={packet.Bitrate}kbps, FPS={packet.Fps}, 큐={packet.QueueDepth}");

                // 손실률 업데이트
                _adaptiveManager.UpdatePacketLoss(packet.PacketLoss);

                // 호스트 큐가 비정상적으로 커지면 키프레임 요청
                if (packet.QueueDepth > 3)
                {
                    EnhancedLogger.Instance.Warning($"호스트 큐 깊이가 비정상적입니다: {packet.QueueDepth}");
                    _keyframeRequested = true;
                }

                // 메트릭 업데이트
                _metrics.PacketLoss = packet.PacketLoss;
                _metrics.Ping = packet.Rtt;
                _metrics.HostQueueLength = packet.QueueDepth;
            }
            catch (Exception ex)
            {
                EnhancedLogger.Instance.Error($"네트워크 통계 처리 오류: {ex.Message}", ex);
            }
        }

        private void SetRemoteControlMode(bool active)
        {
            if (_isRemoteControlActive != active)
            {
                _isRemoteControlActive = active;
                EnhancedLogger.Instance.Info($"Remote control mode changed: {active}");
                RemoteControlStatusChanged?.Invoke(this, active);

                // 적응형 관리자에 모드 변경 알림
                _adaptiveManager.SetRemoteControlMode(active);
            }
        }

        // 필수 인터페이스 메서드
        public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
        {
            // 이 애플리케이션에서는 사용하지 않음
        }

        public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
        {
            // 고지연 로깅
            if (latency > 500)
            {
                EnhancedLogger.Instance.Debug($"높은 네트워크 지연: {latency}ms");
            }
        }

        public void OnConnectionRequest(ConnectionRequest request)
        {
            // 클라이언트는 수신 연결을 수락하지 않음
            request.Reject();
        }

        public void Dispose()
        {
            Stop();
        }
    }

    // 성능 메트릭 클래스
    public class PerformanceMetrics
    {
        public double CurrentBitrateMbps { get; set; }
        public double CurrentFps { get; set; }
        public int TargetFps { get; set; }
        public int EncodingQuality { get; set; }
        public int TargetBitrate { get; set; }
        public int Ping { get; set; }
        public int PacketLoss { get; set; }
        public int HostQueueLength { get; set; }
        public double HostProcessingTime { get; set; } // 밀리초 단위
        public long TotalBytesSent { get; set; }
        public long TotalFramesSent { get; set; }
        public int LastFrameSize { get; set; }
    }
}