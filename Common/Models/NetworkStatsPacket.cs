using System;

namespace ScreenShare.Common.Models
{
    [Serializable]
    public class NetworkStatsPacket
    {
        public int ClientNumber { get; set; }
        public int PacketLoss { get; set; }
        public int Bitrate { get; set; }  // kbps
        public int Rtt { get; set; }      // ms
        public int Fps { get; set; }
        public int QueueDepth { get; set; }
        public long Timestamp { get; set; }
    }
}