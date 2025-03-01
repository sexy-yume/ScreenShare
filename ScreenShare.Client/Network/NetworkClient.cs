// ScreenShare.Client/Network/NetworkClient.cs
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Diagnostics;
using LiteNetLib;
using LiteNetLib.Utils;
using ScreenShare.Common.Models;
using ScreenShare.Common.Settings;
using ScreenShare.Common.Utils;

namespace ScreenShare.Client.Network
{
    public class NetworkClient : INetEventListener, IDisposable
    {
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

        public event EventHandler<bool> RemoteControlStatusChanged;
        public event EventHandler<ScreenPacket> RemoteControlReceived;
        public event EventHandler<bool> ConnectionStatusChanged;

        public bool IsConnected => _server != null && _server.ConnectionState == ConnectionState.Connected;
        public bool IsRemoteControlActive => _isRemoteControlActive;

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
                PingInterval = 1000,
                DisconnectTimeout = 5000
            };

            _sendStatsTimer.Start();
        }

        public void Start()
        {
            Console.WriteLine("네트워크 클라이언트 시작");
            _netManager.Start();
            Connect();
        }

        public void Connect()
        {
            if (_isConnecting)
                return;

            _isConnecting = true;
            _connectionTimer.Restart();

            Console.WriteLine($"호스트 연결 시도: {_settings.HostIp}:{_settings.HostPort}");
            _netManager.Connect(_settings.HostIp, _settings.HostPort, "ScreenShare");

            // 연결 상태 주기적으로 로깅
            new Thread(() => {
                while (_isConnecting && _connectionTimer.ElapsedMilliseconds < 10000)
                {
                    Console.WriteLine($"연결 대기 중... ({_connectionTimer.ElapsedMilliseconds / 1000}초)");
                    Thread.Sleep(1000);
                }
                _isConnecting = false;
            })
            { IsBackground = true }.Start();
        }

        public void Stop()
        {
            Console.WriteLine("네트워크 클라이언트 종료");
            _isConnecting = false;
            _netManager.Stop();
        }

        public void SendScreenData(byte[] data, int width, int height)
        {
            if (!IsConnected || data == null || data.Length == 0)
                return;

            lock (_sendLock)
            {
                try
                {
                    var packet = new ScreenPacket
                    {
                        Type = PacketType.ScreenData,
                        ClientNumber = _settings.ClientNumber,
                        ScreenData = data,
                        Width = width,
                        Height = height,
                        Timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds()
                    };

                    byte[] serializedData = PacketSerializer.Serialize(packet);
                    _server.Send(serializedData, DeliveryMethod.ReliableOrdered);

                    _bytesSent += serializedData.Length;
                    _framesSent++;

                    // 주기적으로 네트워크 통계 출력
                    if (_sendStatsTimer.ElapsedMilliseconds >= 5000)
                    {
                        double seconds = _sendStatsTimer.ElapsedMilliseconds / 1000.0;
                        double mbps = (_bytesSent * 8.0 / 1_000_000.0) / seconds;
                        double fps = _framesSent / seconds;
                        Console.WriteLine($"네트워크 통계: {mbps:F2} Mbps, {fps:F1} fps, 지연: {_server.Ping}ms");

                        _bytesSent = 0;
                        _framesSent = 0;
                        _sendStatsTimer.Restart();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"데이터 전송 오류: {ex.Message}");
                }
            }
        }

        public void Update()
        {
            _netManager.PollEvents();
        }

        public void OnPeerConnected(NetPeer peer)
        {
            _server = peer;
            _isConnecting = false;
            Console.WriteLine($"호스트 연결됨: {peer.EndPoint}, 연결 시간: {_connectionTimer.ElapsedMilliseconds}ms");

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
        }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            Console.WriteLine($"호스트 연결 해제: {disconnectInfo.Reason}");
            _server = null;

            // 원격 제어 모드 해제
            SetRemoteControlMode(false);
            ConnectionStatusChanged?.Invoke(this, false);

            // 자동 재연결 (1초 후)
            Thread.Sleep(1000);
            Connect();
        }

        public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            Console.WriteLine($"네트워크 오류: {endPoint} - {socketError}");
        }

        // LiteNetLib 최신 버전에 맞게 메서드 시그니처 업데이트
        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            try
            {
                byte[] data = reader.GetRemainingBytes();
                var packet = PacketSerializer.Deserialize<ScreenPacket>(data);

                switch (packet.Type)
                {
                    case PacketType.RemoteControl:
                        Console.WriteLine("원격 제어 요청 수신");
                        SetRemoteControlMode(true);
                        break;

                    case PacketType.RemoteEnd:
                        Console.WriteLine("원격 제어 종료 수신");
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
                Console.WriteLine($"패킷 처리 오류: {ex.Message}");
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
                Console.WriteLine($"원격 제어 모드 변경: {active}");
                RemoteControlStatusChanged?.Invoke(this, active);
            }
        }

        public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
        {
        }

        public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
        {
            if (latency > 500)
            {
                Console.WriteLine($"네트워크 지연 높음: {latency}ms");
            }
        }

        public void OnConnectionRequest(ConnectionRequest request)
        {
        }

        public void Dispose()
        {
            Stop();
        }
    }
}