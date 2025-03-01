using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ScreenShare.Common.Models
{
    public class ClientInfo
    {
        public int ClientNumber { get; set; }
        public string ClientIp { get; set; }
        public int ClientPort { get; set; }
        public bool IsRemoteControlActive { get; set; }
        public int ScreenWidth { get; set; }
        public int ScreenHeight { get; set; }
    }
}