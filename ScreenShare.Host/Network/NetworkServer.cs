using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Linq;
using LiteNetLib;
using LiteNetLib.Utils;
using ScreenShare.Common.Models;
using ScreenShare.Common.Settings;
using ScreenShare.Common.Utils;

namespace ScreenShare.Host.Network
{
    public class NetworkServer : INetEventListener, IDisposable
    {
        private NetManager _netManager;
        private HostSettings _settings;
        private ConcurrentDictionary<int, NetPeer> _clientPeers;
        private ConcurrentDictionary<int, ClientInfo> _clientInfos;
        private ConcurrentDictionary<int, PerformanceTracker> _clientPerformance = new ConcurrentDictionary<int, PerformanceTracker>();

        // Queue monitoring
        private ConcurrentDictionary<int, ConcurrentQueue<FrameProcessingTask>> _processingQueues =
            new ConcurrentDictionary<int, ConcurrentQueue<FrameProcessingTask>>();
        private int _maxQueueSize = 3;

        // 키프레임 요청 관련
        private ConcurrentDictionary<int, DateTime> _lastKeyframeRequestTime = new ConcurrentDictionary<int, DateTime>();
        private TimeSpan _minKeyframeRequestInterval = TimeSpan.FromSeconds(5); // 최소 5초 간격

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
                PingInterval = 500    // More frequent pings
            };

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
            EnhancedLogger.Instance.Info("Network server stopped");
        }

        public void Update()
        {
            _netManager.PollEvents();

            // Update performance metrics for each client
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
        }

        /// <summary>
        /// 클라이언트에 키프레임 요청을 보냅니다.
        /// </summary>
        /// <param name="clientNumber">클라이언트 번호</param>
        /// <returns>성공 여부</returns>
        public bool RequestKeyframe(int clientNumber)
        {
            if (!_clientPeers.TryGetValue(clientNumber, out var peer))
                return false;

            // 최소 요청 간격 체크
            DateTime now = DateTime.UtcNow;
            DateTime lastRequest = _lastKeyframeRequestTime.GetOrAdd(clientNumber, DateTime.MinValue);
            if ((now - lastRequest) < _minKeyframeRequestInterval)
            {
                EnhancedLogger.Instance.Debug($"키프레임 요청 간격 제한: 클라이언트 {clientNumber}, 마지막 요청으로부터 {(now - lastRequest).TotalSeconds:F1}초");
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

                byte[] serializedData = PacketSerializer.Serialize(packet);
                peer.Send(serializedData, DeliveryMethod.ReliableOrdered);

                // 마지막 요청 시간 업데이트
                _lastKeyframeRequestTime[clientNumber] = now;

                EnhancedLogger.Instance.Info($"클라이언트 {clientNumber}에 키프레임 요청 전송");
                return true;
            }
            catch (Exception ex)
            {
                EnhancedLogger.Instance.Error($"키프레임 요청 전송 오류: {ex.Message}", ex);
                return false;
            }
        }

        // Remote control methods
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

            // Update client state
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

            // Update client state
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

        // Add method to track frame processing
        public void BeginFrameProcessing(int clientNumber, long frameId)
        {
            var queue = _processingQueues.GetOrAdd(clientNumber, _ => new ConcurrentQueue<FrameProcessingTask>());

            // Add to processing queue
            var task = new FrameProcessingTask
            {
                FrameId = frameId,
                StartTime = Stopwatch.GetTimestamp()
            };

            queue.Enqueue(task);

            // Keep queue size limited
            while (queue.Count > _maxQueueSize)
            {
                if (queue.TryDequeue(out var oldTask))
                {
                    EnhancedLogger.Instance.Debug($"Dropping frame {oldTask.FrameId} from queue for client {clientNumber} due to queue overflow");
                }
            }

            // Track queue depth
            if (_clientPerformance.TryGetValue(clientNumber, out var tracker))
            {
                tracker.UpdateQueueDepth(queue.Count);
            }
        }

        // Add method to complete frame processing and send acknowledgment
        public void EndFrameProcessing(int clientNumber, long frameId)
        {
            if (!_processingQueues.TryGetValue(clientNumber, out var queue))
                return;

            // Find the frame in the queue
            var frameList = new FrameProcessingTask[queue.Count];
            int count = 0;
            bool found = false;

            // Dequeue until we find the frame or empty the queue
            while (queue.TryDequeue(out var task))
            {
                if (task.FrameId == frameId)
                {
                    // Calculate processing time
                    long endTime = Stopwatch.GetTimestamp();
                    long elapsedTicks = endTime - task.StartTime;
                    long elapsedMicroseconds = elapsedTicks * 1_000_000 / Stopwatch.Frequency;

                    // Send acknowledgment
                    SendFrameAcknowledgment(clientNumber, frameId, (int)elapsedMicroseconds, queue.Count);
                    found = true;

                    // Update performance tracker
                    if (_clientPerformance.TryGetValue(clientNumber, out var tracker))
                    {
                        tracker.AddProcessingTime(elapsedMicroseconds);
                        tracker.UpdateQueueDepth(queue.Count);
                    }

                    break;
                }

                // Save in case we need to requeue
                frameList[count++] = task;
            }

            // Requeue any frames we dequeued before finding our target
            for (int i = 0; i < count; i++)
            {
                queue.Enqueue(frameList[i]);
            }

            if (!found)
            {
                EnhancedLogger.Instance.Debug($"Frame {frameId} not found in processing queue for client {clientNumber}");
            }
        }

        // New method to send frame acknowledgment
        private void SendFrameAcknowledgment(int clientNumber, long frameId, long processingTime, int queueLength)
        {
            if (!_clientPeers.TryGetValue(clientNumber, out var peer))
                return;

            try
            {
                // Create a special packet with the FrameAck type
                var screenPacket = new ScreenPacket
                {
                    Type = PacketType.FrameAck,
                    ClientNumber = clientNumber,
                    FrameId = frameId,
                    Timestamp = processingTime,  // Use timestamp field for processing time
                    Width = queueLength,         // Use width field for queue length
                    Height = (int)(peer.Statistics.PacketLossPercent * 100) // Use height field for packet loss (scaled)
                };

                // Serialize the packet directly - much simpler and safer
                byte[] serializedData = PacketSerializer.Serialize(screenPacket);

                // Send the packet
                peer.Send(serializedData, DeliveryMethod.ReliableOrdered);

                EnhancedLogger.Instance.Debug($"Sent frame ack to client {clientNumber}, id={frameId}, time={processingTime/1000.0}ms, queue={queueLength}");
            }
            catch (Exception ex)
            {
                EnhancedLogger.Instance.Error($"Error sending frame ack: {ex.Message}", ex);
            }
        }

        public void OnPeerConnected(NetPeer peer)
        {
            EnhancedLogger.Instance.Info($"Client connected: {peer.EndPoint}");
        }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            EnhancedLogger.Instance.Info($"Client disconnected: {disconnectInfo.Reason}");

            // Find the client
            foreach (var client in _clientPeers)
            {
                if (client.Value.Id == peer.Id)
                {
                    _clientPeers.TryRemove(client.Key, out _);
                    _clientInfos.TryRemove(client.Key, out var clientInfo);
                    _clientPerformance.TryRemove(client.Key, out _);
                    _processingQueues.TryRemove(client.Key, out _);
                    _lastKeyframeRequestTime.TryRemove(client.Key, out _);

                    ClientDisconnected?.Invoke(this, new ClientEventArgs(client.Key, clientInfo));
                    break;
                }
            }
        }

        public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            EnhancedLogger.Instance.Error($"Network error: {endPoint} - {socketError}", null);
        }

        // Modified OnNetworkReceive to extract frameId and track performance
        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            try
            {
                byte[] data = reader.GetRemainingBytes();
                var packet = PacketSerializer.Deserialize<ScreenPacket>(data);

                switch (packet.Type)
                {
                    case PacketType.Connect:
                        // Register client
                        _clientPeers[packet.ClientNumber] = peer;

                        var clientInfo = new ClientInfo
                        {
                            ClientNumber = packet.ClientNumber,
                            ClientIp = peer.EndPoint.Address.ToString(),
                            ClientPort = peer.EndPoint.Port,
                            IsRemoteControlActive = false
                        };

                        _clientInfos[packet.ClientNumber] = clientInfo;

                        // Initialize performance tracker for this client
                        _clientPerformance[packet.ClientNumber] = new PerformanceTracker();

                        ClientConnected?.Invoke(this, new ClientEventArgs(packet.ClientNumber, clientInfo));

                        EnhancedLogger.Instance.Info($"Client {packet.ClientNumber} registered from {clientInfo.ClientIp}:{clientInfo.ClientPort}");
                        break;

                    case PacketType.ScreenData:
                        if (_clientInfos.TryGetValue(packet.ClientNumber, out var info))
                        {
                            info.ScreenWidth = packet.Width;
                            info.ScreenHeight = packet.Height;

                            // Start tracking frame processing
                            long frameId = packet.FrameId;
                            bool isKeyFrame = packet.IsKeyFrame;
                            EnhancedLogger.Instance.Debug($"Received frame from client {packet.ClientNumber}, id={frameId}, keyframe={isKeyFrame}, size={packet.ScreenData.Length}");
                            BeginFrameProcessing(packet.ClientNumber, frameId);

                            // Track frame size
                            if (_clientPerformance.TryGetValue(packet.ClientNumber, out var tracker))
                            {
                                tracker.AddFrame(data.Length);
                            }

                            // Raise event for actual processing with keyframe info
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
                EnhancedLogger.Instance.Error($"Packet processing error: {ex.Message}", ex);
            }
            finally
            {
                reader.Recycle();
            }
        }

        // Implement all required interface methods
        public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
        {
            // Not used in this application
        }

        public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
        {
            // Can be used to track latency if needed
        }

        public void OnConnectionRequest(ConnectionRequest request)
        {
            // Accept all connection requests
            request.Accept();
        }

        public void Dispose()
        {
            Stop();
        }
    }

    // Class for frame processing task tracking
    public class FrameProcessingTask
    {
        public long FrameId { get; set; }
        public long StartTime { get; set; } // Timestamp from Stopwatch.GetTimestamp()
    }

    // Class for tracking client performance
    public class PerformanceTracker
    {
        private readonly Stopwatch _uptime = Stopwatch.StartNew();
        private long _totalFrames = 0;
        private long _totalBytes = 0;
        private long _lastReportTime = 0;
        private TimeSpan _reportInterval = TimeSpan.FromSeconds(5);
        private int _currentQueueDepth = 0;
        private long _totalProcessingTime = 0; // Microseconds

        // Metrics for current interval
        private long _intervalFrames = 0;
        private long _intervalBytes = 0;
        private long _intervalProcessingTime = 0;
        private int _maxQueueDepth = 0;

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

        public ClientPerformanceMetrics GetMetrics()
        {
            long now = _uptime.ElapsedMilliseconds;
            long elapsed = now - _lastReportTime;

            var metrics = new ClientPerformanceMetrics
            {
                TotalFrames = _totalFrames,
                TotalBytes = _totalBytes,
                AverageFps = elapsed > 0 ? (_intervalFrames * 1000.0 / elapsed) : 0,
                AverageBitrateMbps = elapsed > 0 ? (_intervalBytes * 8.0 / elapsed / 1000.0) : 0,
                CurrentQueueDepth = _currentQueueDepth,
                MaxQueueDepth = _maxQueueDepth,
                AverageProcessingTimeMs = _intervalFrames > 0 ? (_intervalProcessingTime / 1000.0 / _intervalFrames) : 0
            };

            // Reset interval metrics
            _lastReportTime = now;
            _intervalFrames = 0;
            _intervalBytes = 0;
            _intervalProcessingTime = 0;
            _maxQueueDepth = _currentQueueDepth;

            return metrics;
        }
    }
}