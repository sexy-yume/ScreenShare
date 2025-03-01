using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ScreenShare.Common.Models
{
    public enum PacketType
    {
        Connect,        // 연결 요청
        Disconnect,     // 연결 종료
        ScreenData,     // 화면 데이터
        RemoteControl,  // 원격 제어 요청
        RemoteEnd,      // 원격 제어 종료
        MouseMove,      // 마우스 이동
        MouseClick,     // 마우스 클릭
        KeyPress        // 키보드 입력
    }
}