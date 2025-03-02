using System;
using System.IO;
using System.Text.Json;
using ScreenShare.Common.Models;

namespace ScreenShare.Common.Utils
{
    public static class PacketSerializer
    {
        public static byte[] Serialize(object obj)
        {
            return JsonSerializer.SerializeToUtf8Bytes(obj, obj.GetType(), new JsonSerializerOptions
            {
                WriteIndented = false,
                IncludeFields = true
            });
        }

        public static T Deserialize<T>(byte[] data)
        {
            return JsonSerializer.Deserialize<T>(data, new JsonSerializerOptions
            {
                IncludeFields = true
            });
        }
    }
}