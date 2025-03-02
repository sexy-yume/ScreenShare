using System;
using ScreenShare.Common.Models;

namespace ScreenShare.Host.Network
{
    /// <summary>
    /// Event arguments for client events
    /// </summary>
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

    /// <summary>
    /// Event arguments for screen data events
    /// </summary>
    public class ScreenDataEventArgs : EventArgs
    {
        public int ClientNumber { get; }
        public byte[] ScreenData { get; }
        public int Width { get; }
        public int Height { get; }
        public long FrameId { get; }

        public ScreenDataEventArgs(int clientNumber, byte[] screenData, int width, int height, long frameId = 0)
        {
            ClientNumber = clientNumber;
            ScreenData = screenData;
            Width = width;
            Height = height;
            FrameId = frameId;
        }
    }

    /// <summary>
    /// Event arguments for performance metrics events
    /// </summary>
    public class PerformanceEventArgs : EventArgs
    {
        public int ClientNumber { get; set; }
        public ClientPerformanceMetrics Metrics { get; set; }
    }

    /// <summary>
    /// Performance metrics for reporting
    /// </summary>
    public class ClientPerformanceMetrics
    {
        public long TotalFrames { get; set; }
        public long TotalBytes { get; set; }
        public double AverageFps { get; set; }
        public double AverageBitrateMbps { get; set; }
        public int CurrentQueueDepth { get; set; }
        public int MaxQueueDepth { get; set; }
        public double AverageProcessingTimeMs { get; set; }
    }
}