// ScreenShare.Common/Utils/FileLogger.cs
using System;
using System.IO;
using System.Text;
using System.Threading;

namespace ScreenShare.Common.Utils
{
    public class FileLogger
    {
        private static readonly object _lock = new object();
        private static FileLogger _instance;
        private string _logFilePath;
        private StreamWriter _writer;
        private bool _consoleOutputEnabled = true;
        private Thread _flushThread;
        private bool _isRunning = false;

        public static FileLogger Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new FileLogger();
                        }
                    }
                }
                return _instance;
            }
        }

        private FileLogger()
        {
            try
            {
                string logsDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ScreenShare", "Logs");

                Directory.CreateDirectory(logsDirectory);

                string appName = AppDomain.CurrentDomain.FriendlyName.Replace(".exe", "");
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                _logFilePath = Path.Combine(logsDirectory, $"{appName}_{timestamp}.log");

                _writer = new StreamWriter(_logFilePath, false, Encoding.UTF8)
                {
                    AutoFlush = false
                };

                // 주기적으로 로그 파일 Flush를 수행하는 스레드
                _isRunning = true;
                _flushThread = new Thread(() =>
                {
                    while (_isRunning)
                    {
                        Thread.Sleep(2000);
                        Flush();
                    }
                })
                {
                    IsBackground = true,
                    Name = "LogFlushThread"
                };
                _flushThread.Start();

                WriteLine($"=== 로그 시작: {DateTime.Now} ===");
                WriteLine($"애플리케이션: {appName}");
                WriteLine($"OS: {Environment.OSVersion}");
                WriteLine($".NET 버전: {Environment.Version}");
                WriteLine($"프로세서 수: {Environment.ProcessorCount}");
                WriteLine($"메모리: {GC.GetTotalMemory(false) / (1024 * 1024)} MB");
                WriteLine("===============================");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"로거 초기화 오류: {ex.Message}");
            }
        }

        public void EnableConsoleOutput(bool enable)
        {
            _consoleOutputEnabled = enable;
        }

        public void WriteLine(string message)
        {
            try
            {
                lock (_lock)
                {
                    string formattedMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";

                    if (_writer != null)
                    {
                        _writer.WriteLine(formattedMessage);
                    }

                    if (_consoleOutputEnabled)
                    {
                        Console.WriteLine(formattedMessage);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"로그 기록 오류: {ex.Message}");
            }
        }

        public void WriteError(string message, Exception ex = null)
        {
            string errorMessage = ex != null
                ? $"[ERROR] {message}\r\n{ex.GetType().Name}: {ex.Message}\r\n{ex.StackTrace}"
                : $"[ERROR] {message}";

            WriteLine(errorMessage);
        }

        public void WriteWarning(string message)
        {
            WriteLine($"[WARNING] {message}");
        }

        public void WriteInfo(string message)
        {
            WriteLine($"[INFO] {message}");
        }

        public void WriteDebug(string message)
        {
#if DEBUG
            WriteLine($"[DEBUG] {message}");
#endif
        }

        public void Flush()
        {
            try
            {
                lock (_lock)
                {
                    _writer?.Flush();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"로그 Flush 오류: {ex.Message}");
            }
        }

        public void Close()
        {
            try
            {
                _isRunning = false;
                _flushThread?.Join(3000);

                lock (_lock)
                {
                    WriteLine($"=== 로그 종료: {DateTime.Now} ===");
                    _writer?.Flush();
                    _writer?.Close();
                    _writer?.Dispose();
                    _writer = null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"로그 종료 오류: {ex.Message}");
            }
        }
    }
}