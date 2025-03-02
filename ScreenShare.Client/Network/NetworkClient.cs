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
        // Existing fields
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

        // New fields for adaptive frame management
        private AdaptiveFrameManager _adaptiveManager;
        private ConcurrentDictionary<long, DateTime> _pendingFrames = new ConcurrentDictionary<long, DateTime>();
        private long _nextFrameId = 1;
        private PerformanceMetrics _metrics = new PerformanceMetrics();

        // Events (existing and new)
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
                PingInterval = 500,       // More frequent pings (was 1000)
                DisconnectTimeout = 5000
            };

            _sendStatsTimer.Start();

            // Initialize adaptive frame manager
            _adaptiveManager = new AdaptiveFrameManager(
                settings.LowResFps,
                settings.LowResQuality,
                10_000_000);  // Initial bitrate: 10 Mbps

            _adaptiveManager.SettingsChanged += OnAdaptiveSettingsChanged;
        }

        // New method to handle adaptive settings changes
        private void OnAdaptiveSettingsChanged(object sender, AdaptiveSettingsChangedEventArgs e)
        {
            // Update metrics
            _metrics.TargetFps = e.TargetFps;
            _metrics.EncodingQuality = e.Quality;
            _metrics.TargetBitrate = e.Bitrate;

            // Notify listeners
            PerformanceUpdated?.Invoke(this, _metrics);
        }

        public void Start()
        {
            EnhancedLogger.Instance.Info("Network client starting");
            _netManager.Start();
            Connect();
        }

        // Existing Connect method
        public void Connect()
        {
            if (_isConnecting)
                return;

            _isConnecting = true;
            _connectionTimer.Restart();

            EnhancedLogger.Instance.Info($"Connecting to host: {_settings.HostIp}:{_settings.HostPort}");
            _netManager.Connect(_settings.HostIp, _settings.HostPort, "ScreenShare");

            // Connection monitoring thread
            new Thread(() => {
                while (_isConnecting && _connectionTimer.ElapsedMilliseconds < 10000)
                {
                    EnhancedLogger.Instance.Info($"Waiting for connection... ({_connectionTimer.ElapsedMilliseconds / 1000}s)");
                    Thread.Sleep(1000);
                }
                _isConnecting = false;
            })
            { IsBackground = true }.Start();
        }

        public void Stop()
        {
            EnhancedLogger.Instance.Info("Network client stopping");
            _isConnecting = false;
            _netManager.Stop();
            _adaptiveManager?.Dispose();
        }

        // Modified method to implement frame flow control
        public bool SendScreenData(byte[] data, int width, int height)
        {
            if (!IsConnected || data == null || data.Length == 0)
                return false;

            // Check with adaptive manager if we should send this frame
            if (!_adaptiveManager.BeginFrameSend())
            {
                // Skip this frame based on adaptive rate control
                return false;
            }

            lock (_sendLock)
            {
                try
                {
                    // Generate frame ID for tracking
                    long frameId = _nextFrameId++;

                    // Store send time for RTT calculation
                    _pendingFrames[frameId] = DateTime.UtcNow;

                    // Create packet with frame ID
                    var packet = new ScreenPacket
                    {
                        Type = PacketType.ScreenData,
                        ClientNumber = _settings.ClientNumber,
                        ScreenData = data,
                        Width = width,
                        Height = height,
                        Timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                        FrameId = frameId
                    };

                    byte[] serializedData = PacketSerializer.Serialize(packet);
                    _server.Send(serializedData, DeliveryMethod.ReliableOrdered);

                    // Log frame sending periodically
                    if (frameId % 30 == 0)
                    {
                        EnhancedLogger.Instance.Debug($"Sent frame id={frameId}, size={serializedData.Length}, pending={_pendingFrames.Count}");
                    }

                    _bytesSent += serializedData.Length;
                    _framesSent++;

                    // Update metrics
                    _metrics.LastFrameSize = serializedData.Length;
                    _metrics.TotalBytesSent += serializedData.Length;
                    _metrics.TotalFramesSent++;

                    // Log network statistics periodically
                    if (_sendStatsTimer.ElapsedMilliseconds >= 5000)
                    {
                        double seconds = _sendStatsTimer.ElapsedMilliseconds / 1000.0;
                        double mbps = (_bytesSent * 8.0 / 1_000_000.0) / seconds;
                        double fps = _framesSent / seconds;

                        _metrics.CurrentBitrateMbps = mbps;
                        _metrics.CurrentFps = fps;
                        _metrics.Ping = _server.Ping;

                        EnhancedLogger.Instance.Info(
                            $"Network stats: {mbps:F2} Mbps, {fps:F1} fps, RTT: {_server.Ping}ms, " +
                            $"Pending: {_pendingFrames.Count}, Packet loss: {_server.Statistics.PacketLossPercent:F1}%");

                        // Update adaptive manager with packet loss info
                        _adaptiveManager.UpdatePacketLoss((int)_server.Statistics.PacketLossPercent);

                        // Notify listeners
                        PerformanceUpdated?.Invoke(this, _metrics);

                        _bytesSent = 0;
                        _framesSent = 0;
                        _sendStatsTimer.Restart();
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    EnhancedLogger.Instance.Error($"Data send error: {ex.Message}", ex);
                    return false;
                }
            }
        }

        public void Update()
        {
            _netManager.PollEvents();

            // Check for timed-out frames (unacknowledged for > 5 seconds)
            var now = DateTime.UtcNow;
            foreach (var frame in _pendingFrames)
            {
                if ((now - frame.Value).TotalSeconds > 5)
                {
                    if (_pendingFrames.TryRemove(frame.Key, out _))
                    {
                        EnhancedLogger.Instance.Debug($"Frame {frame.Key} timed out waiting for acknowledgment");
                    }
                }
            }
        }

        public void OnPeerConnected(NetPeer peer)
        {
            _server = peer;
            _isConnecting = false;
            EnhancedLogger.Instance.Info($"Host connected: {peer.EndPoint}, connection time: {_connectionTimer.ElapsedMilliseconds}ms");

            // Send initial connection info
            var packet = new ScreenPacket
            {
                Type = PacketType.Connect,
                ClientNumber = _settings.ClientNumber,
                Timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds()
            };

            byte[] serializedData = PacketSerializer.Serialize(packet);
            _server.Send(serializedData, DeliveryMethod.ReliableOrdered);
            ConnectionStatusChanged?.Invoke(this, true);
        }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            EnhancedLogger.Instance.Info($"Host disconnected: {disconnectInfo.Reason}");
            _server = null;

            // Disable remote control mode
            SetRemoteControlMode(false);
            ConnectionStatusChanged?.Invoke(this, false);

            // Auto-reconnect after 1 second
            Thread.Sleep(1000);
            Connect();
        }

        public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            EnhancedLogger.Instance.Error($"Network error: {endPoint} - {socketError}", null);
        }

        // Modified OnNetworkReceive to handle frame acknowledgments
        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            try
            {
                byte[] data = reader.GetRemainingBytes();
                var packet = PacketSerializer.Deserialize<ScreenPacket>(data);

                // Check for frame acknowledgment
                if (packet.Type == PacketType.FrameAck)
                {
                    EnhancedLogger.Instance.Debug($"Received frame ack: id={packet.FrameId}, time={packet.Timestamp/1000.0}ms, queue={packet.Width}");

                    // Calculate RTT
                    if (_pendingFrames.TryRemove(packet.FrameId, out DateTime sentTime))
                    {
                        int rtt = (int)(DateTime.UtcNow - sentTime).TotalMilliseconds;

                        // Update adaptive manager
                        _adaptiveManager.FrameAcknowledged(packet.FrameId, rtt);

                        // Update metrics
                        _metrics.Ping = rtt;
                        _metrics.HostQueueLength = packet.Width;  // Queue length is stored in Width
                        _metrics.HostProcessingTime = packet.Timestamp / 1000.0; // Processing time in Timestamp field
                        _metrics.PacketLoss = packet.Height / 100;  // Packet loss in Height field (scaled)

                        EnhancedLogger.Instance.Debug($"Processed ack: id={packet.FrameId}, rtt={rtt}ms, queue={packet.Width}");
                    }
                    else
                    {
                        EnhancedLogger.Instance.Debug($"Received ack for unknown frame: id={packet.FrameId}");
                    }

                    return;
                }

                // Handle other packet types
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

        private void SetRemoteControlMode(bool active)
        {
            if (_isRemoteControlActive != active)
            {
                _isRemoteControlActive = active;
                EnhancedLogger.Instance.Info($"Remote control mode changed: {active}");
                RemoteControlStatusChanged?.Invoke(this, active);

                // Notify adaptive manager of mode change
                _adaptiveManager.SetRemoteControlMode(active);
            }
        }

        // Implement all required interface methods
        public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
        {
            // Not used in this application
        }

        public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
        {
            // Could be used to update metrics if needed
            if (latency > 500)
            {
                EnhancedLogger.Instance.Debug($"High network latency: {latency}ms");
            }
        }

        public void OnConnectionRequest(ConnectionRequest request)
        {
            // Client doesn't accept incoming connections
            request.Reject();
        }

        public void Dispose()
        {
            Stop();
        }
    }

    // Performance metrics class
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
        public double HostProcessingTime { get; set; } // In milliseconds
        public long TotalBytesSent { get; set; }
        public long TotalFramesSent { get; set; }
        public int LastFrameSize { get; set; }
    }
}