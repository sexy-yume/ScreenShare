using System;
using System.Text.Json.Serialization;

namespace ScreenShare.Common.Models
{
    [Serializable]
    public class ScreenPacket
    {
        [JsonInclude]
        public PacketType Type { get; set; }

        [JsonInclude]
        public int ClientNumber { get; set; }

        [JsonInclude]
        public byte[] ScreenData { get; set; }

        [JsonInclude]
        public int Width { get; set; }

        [JsonInclude]
        public int Height { get; set; }

        [JsonInclude]
        public long Timestamp { get; set; }

        [JsonInclude]
        public long FrameId { get; set; }

        // Remote control fields
        [JsonInclude]
        public int? MouseX { get; set; }

        [JsonInclude]
        public int? MouseY { get; set; }

        [JsonInclude]
        public int? MouseButton { get; set; }

        [JsonInclude]
        public int? KeyCode { get; set; }
    }
}