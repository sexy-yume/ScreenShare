// ScreenShare.Common/Settings/ClientSettings.cs
using System;
using System.IO;
using Newtonsoft.Json;

namespace ScreenShare.Common.Settings
{
    [Serializable]
    public class ClientSettings
    {
        public int ClientNumber { get; set; }
        public string HostIp { get; set; }
        public int HostPort { get; set; }

        // 화면 캡처 관련 설정
        public int LowResFps { get; set; } = 8;
        public int HighResFps { get; set; } = 30;
        public int LowResQuality { get; set; } = 30;
        public int HighResQuality { get; set; } = 90;

        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ScreenShare", "client_settings.json");

        public static ClientSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    string json = File.ReadAllText(SettingsPath);
                    return JsonConvert.DeserializeObject<ClientSettings>(json);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"설정 로드 중 오류: {ex.Message}");
            }

            return new ClientSettings
            {
                ClientNumber = 1,
                HostIp = "",
                HostPort = 9050,
                LowResFps = 8,
                HighResFps = 30
            };
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                string json = JsonConvert.SerializeObject(this, Newtonsoft.Json.Formatting.Indented);
                File.WriteAllText(SettingsPath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"설정 저장 중 오류: {ex.Message}");
            }
        }
    }
}