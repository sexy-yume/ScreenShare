using System;
using System.Runtime.InteropServices;

namespace ScreenShare.Common.Utils
{
    public static class ConsoleHelper
    {
        [DllImport("kernel32.dll")]
        public static extern bool AllocConsole();

        [DllImport("kernel32.dll")]
        public static extern bool FreeConsole();

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;

        /// <summary>
        /// 디버그 콘솔 창을 생성하고 표시합니다.
        /// </summary>
        public static void ShowConsoleWindow()
        {
            var handle = GetConsoleWindow();

            // 콘솔이 이미 있는 경우
            if (handle != IntPtr.Zero)
            {
                ShowWindow(handle, SW_SHOW);
                return;
            }

            // 콘솔을 새로 생성
            AllocConsole();
            Console.Title = "ScreenShare Debug Console";
            Console.WriteLine("디버그 콘솔이 활성화되었습니다.");
        }

        /// <summary>
        /// 디버그 콘솔 창을 숨깁니다.
        /// </summary>
        public static void HideConsoleWindow()
        {
            var handle = GetConsoleWindow();

            if (handle != IntPtr.Zero)
            {
                ShowWindow(handle, SW_HIDE);
            }
        }

        /// <summary>
        /// 디버그 콘솔 창을 닫습니다.
        /// </summary>
        public static void CloseConsoleWindow()
        {
            var handle = GetConsoleWindow();

            if (handle != IntPtr.Zero)
            {
                FreeConsole();
            }
        }
    }
}