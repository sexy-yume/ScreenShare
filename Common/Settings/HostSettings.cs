// ScreenShare.Common/Settings/HostSettings.cs
using System;
using System.IO;
using Newtonsoft.Json;

namespace ScreenShare.Common.Settings
{
    [Serializable]
    public class HostSettings
    {
        public string HostIp { get; set; }
        public int HostPort { get; set; }
        public int MaxClients { get; set; } = 25;
        public int TileColumns { get; set; } = 5;

        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ScreenShare", "host_settings.json");

        public static HostSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    string json = File.ReadAllText(SettingsPath);
                    return JsonConvert.DeserializeObject<HostSettings>(json);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"설정 로드 중 오류: {ex.Message}");
            }

            return new HostSettings
            {
                HostIp = "0.0.0.0",  // 모든 IP에서 접속 허용
                HostPort = 9050,
                MaxClients = 25,
                TileColumns = 5
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