// ScreenShare.Host/Network/NetworkServer.cs - OnNetworkReceive 메서드 수정
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
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

        public event EventHandler<ClientEventArgs> ClientConnected;
        public event EventHandler<ClientEventArgs> ClientDisconnected;
        public event EventHandler<ScreenDataEventArgs> ScreenDataReceived;

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
                NatPunchEnabled = true
            };
        }

        public void Start()
        {
            _netManager.Start(_settings.HostPort);
            IsRunning = true;
        }

        public void Stop()
        {
            _netManager.Stop();
            IsRunning = false;
            _clientPeers.Clear();
            _clientInfos.Clear();
        }

        public void Update()
        {
            _netManager.PollEvents();
        }

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

        public void OnPeerConnected(NetPeer peer)
        {
            Console.WriteLine($"클라이언트 연결됨: {peer.EndPoint}");
        }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            Console.WriteLine($"클라이언트 연결 해제: {disconnectInfo.Reason}");

            // 해당 클라이언트 찾기
            foreach (var client in _clientPeers)
            {
                if (client.Value.Id == peer.Id)
                {
                    _clientPeers.TryRemove(client.Key, out _);
                    _clientInfos.TryRemove(client.Key, out var clientInfo);

                    ClientDisconnected?.Invoke(this, new ClientEventArgs(client.Key, clientInfo));
                    break;
                }
            }
        }

        public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            Console.WriteLine($"네트워크 오류: {socketError}");
        }

        // 수정된 OnNetworkReceive 메서드 시그니처
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
                        ClientConnected?.Invoke(this, new ClientEventArgs(packet.ClientNumber, clientInfo));
                        break;

                    case PacketType.ScreenData:
                        if (_clientInfos.TryGetValue(packet.ClientNumber, out var info))
                        {
                            info.ScreenWidth = packet.Width;
                            info.ScreenHeight = packet.Height;

                            ScreenDataReceived?.Invoke(this, new ScreenDataEventArgs(
                                packet.ClientNumber, packet.ScreenData, packet.Width, packet.Height));
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

        public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
        {
        }

        public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
        {
        }

        public void OnConnectionRequest(ConnectionRequest request)
        {
            request.Accept();
        }

        public void Dispose()
        {
            Stop();
        }
    }

    public class ClientEventArgs : EventArgs
    {
        public int ClientNumber { get; }
        public ClientInfo ClientInfo { get; }

        public ClientEventArgs(int clientNumber, ClientInfo clientInfo)
        {
            ClientNumber = clientNumber;
            ClientInfo = clientInfo;
        }
    }

    public class ScreenDataEventArgs : EventArgs
    {
        public int ClientNumber { get; }
        public byte[] ScreenData { get; }
        public int Width { get; }
        public int Height { get; }

        public ScreenDataEventArgs(int clientNumber, byte[] screenData, int width, int height)
        {
            ClientNumber = clientNumber;
            ScreenData = screenData;
            Width = width;
            Height = height;
        }
    }
}