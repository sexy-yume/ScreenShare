using System;

namespace ScreenShare.Common.Models
{
    [Serializable]
    public class FrameAckPacket
    {
        public long FrameId { get; set; }
        public int RoundTripTime { get; set; }
        public int HostQueueLength { get; set; }
        public long HostProcessingTime { get; set; }  // Microseconds
        public int PacketLoss { get; set; }
    }
}