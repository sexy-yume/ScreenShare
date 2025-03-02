using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Collections.Concurrent;

namespace ScreenShare.Common.Utils
{
    public class EnhancedLogger : IDisposable
    {
        // Singleton instance
        private static readonly object _instanceLock = new object();
        private static EnhancedLogger _instance;

        // Log file management
        private string _logFilePath;
        private StreamWriter _writer;
        private readonly int _maxLogFiles = 10;
        private readonly long _maxLogSize = 10 * 1024 * 1024; // 10MB

        // Log level
        public enum LogLevel
        {
            Trace = 0,    // Extremely detailed logs
            Debug = 1,    // Developer debugging information
            Info = 2,     // Normal operational information
            Warning = 3,  // Unusual but recoverable situations
            Error = 4,    // Errors that prevent normal operation
            Fatal = 5     // Critical failures
        }

        private LogLevel _consoleLogLevel = LogLevel.Info;
        private LogLevel _fileLogLevel = LogLevel.Debug;
        private bool _consoleOutputEnabled = true;

        // Performance tracking
        private long _logCounter = 0;
        private ConcurrentDictionary<string, Stopwatch> _timerDictionary = new ConcurrentDictionary<string, Stopwatch>();

        // Async writing
        private Thread _flushThread;
        private readonly ConcurrentQueue<LogEntry> _pendingLogs = new ConcurrentQueue<LogEntry>();
        private bool _isRunning = false;
        private readonly AutoResetEvent _writeEvent = new AutoResetEvent(false);
        private readonly int _maxQueueSize = 1000;
        private readonly StringBuilder _builder = new StringBuilder(1024);

        public static EnhancedLogger Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        if (_instance == null)
                        {
                            _instance = new EnhancedLogger();
                        }
                    }
                }
                return _instance;
            }
        }

        private EnhancedLogger()
        {
            try
            {
                // Create logs directory
                string logsDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ScreenShare", "Logs");

                Directory.CreateDirectory(logsDirectory);

                // Clean up old log files if needed
                CleanupOldLogFiles(logsDirectory);

                // Create log file with timestamp
                string appName = AppDomain.CurrentDomain.FriendlyName.Replace(".exe", "");
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                _logFilePath = Path.Combine(logsDirectory, $"{appName}_{timestamp}.log");

                // Create writer
                _writer = new StreamWriter(_logFilePath, false, Encoding.UTF8)
                {
                    AutoFlush = false
                };

                // Start logging thread
                _isRunning = true;
                _flushThread = new Thread(ProcessLogQueue)
                {
                    IsBackground = true,
                    Name = "LoggingThread",
                    Priority = ThreadPriority.BelowNormal
                };
                _flushThread.Start();

                // Write initial log entries
                Log(LogLevel.Info, $"=== Logging started: {DateTime.Now} ===");
                Log(LogLevel.Info, $"Application: {appName}");
                Log(LogLevel.Info, $"OS: {Environment.OSVersion}");
                Log(LogLevel.Info, $".NET version: {Environment.Version}");
                Log(LogLevel.Info, $"Processors: {Environment.ProcessorCount}");
                Log(LogLevel.Info, $"Memory: {GC.GetTotalMemory(false) / (1024 * 1024)} MB");
                Log(LogLevel.Info, "===============================");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Logger initialization error: {ex.Message}");
            }
        }

        private void CleanupOldLogFiles(string directory)
        {
            try
            {
                var logFiles = Directory.GetFiles(directory, "*.log")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.CreationTime)
                    .ToList();

                // Keep only the most recent files
                for (int i = _maxLogFiles; i < logFiles.Count; i++)
                {
                    try
                    {
                        logFiles[i].Delete();
                    }
                    catch
                    {
                        // Ignore errors during cleanup
                    }
                }
            }
            catch
            {
                // Ignore errors during cleanup
            }
        }

        public void SetLogLevels(LogLevel consoleLevel, LogLevel fileLevel)
        {
            _consoleLogLevel = consoleLevel;
            _fileLogLevel = fileLevel;
            Log(LogLevel.Info, $"Log levels set - Console: {consoleLevel}, File: {fileLevel}");
        }

        public void EnableConsoleOutput(bool enable)
        {
            _consoleOutputEnabled = enable;
        }

        // Main logging method
        public void Log(LogLevel level, string message, Exception ex = null)
        {
            try
            {
                // Always create the log entry
                string category = level.ToString().ToUpper();
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

                if (ex != null)
                {
                    message = $"{message}\r\n{ex.GetType().Name}: {ex.Message}\r\n{ex.StackTrace}";
                }

                // Queue the log entry instead of writing it directly
                var entry = new LogEntry
                {
                    Level = level,
                    Message = message,
                    Category = category,
                    Timestamp = timestamp
                };

                _pendingLogs.Enqueue(entry);

                // If queue getting too large, drop some low-priority logs
                if (_pendingLogs.Count > _maxQueueSize)
                {
                    LogEntry dequeuedEntry;
                    if (_pendingLogs.TryDequeue(out dequeuedEntry) && dequeuedEntry.Level <= LogLevel.Debug)
                    {
                        Interlocked.Increment(ref _logCounter);
                    }
                }

                // Signal the processing thread
                _writeEvent.Set();

                // Write directly to console if needed (for immediate feedback)
                if (_consoleOutputEnabled && level >= _consoleLogLevel)
                {
                    Console.ForegroundColor = GetColorForLevel(level);
                    Console.WriteLine($"[{timestamp}] [{category}] {message}");
                    Console.ResetColor();
                }
            }
            catch (Exception logEx)
            {
                // Fail silently for logging errors
                Console.WriteLine($"Logging error: {logEx.Message}");
            }
        }

        private ConsoleColor GetColorForLevel(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Trace:
                    return ConsoleColor.DarkGray;
                case LogLevel.Debug:
                    return ConsoleColor.Gray;
                case LogLevel.Info:
                    return ConsoleColor.White;
                case LogLevel.Warning:
                    return ConsoleColor.Yellow;
                case LogLevel.Error:
                    return ConsoleColor.Red;
                case LogLevel.Fatal:
                    return ConsoleColor.DarkRed;
                default:
                    return ConsoleColor.White;
            }
        }

        // Log queue processing thread
        private void ProcessLogQueue()
        {
            while (_isRunning)
            {
                try
                {
                    // Wait for log entries or periodic flush
                    _writeEvent.WaitOne(1000);

                    // Process all queued logs
                    int processedCount = 0;

                    while (_pendingLogs.TryDequeue(out LogEntry entry) && _isRunning)
                    {
                        if (entry.Level >= _fileLogLevel)
                        {
                            _builder.Clear();
                            _builder.Append('[').Append(entry.Timestamp).Append(']');
                            _builder.Append(" [").Append(entry.Category).Append(']');
                            _builder.Append(' ').Append(entry.Message);

                            _writer.WriteLine(_builder.ToString());
                            processedCount++;
                        }

                        // Check if we need to flush periodically
                        if (processedCount > 20)
                        {
                            _writer.Flush();
                            processedCount = 0;
                        }

                        // Check log file size and rotate if needed
                        CheckLogFileSize();
                    }

                    // Flush remaining entries
                    if (processedCount > 0)
                    {
                        _writer.Flush();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Log queue processing error: {ex.Message}");
                    Thread.Sleep(1000); // Prevent tight loop in case of errors
                }
            }
        }

        private void CheckLogFileSize()
        {
            try
            {
                // Check file size
                var fileInfo = new FileInfo(_logFilePath);
                if (fileInfo.Exists && fileInfo.Length > _maxLogSize)
                {
                    // Close current log file
                    _writer.Flush();
                    _writer.Close();

                    // Create new log file
                    string appName = AppDomain.CurrentDomain.FriendlyName.Replace(".exe", "");
                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    _logFilePath = Path.Combine(
                        Path.GetDirectoryName(fileInfo.FullName),
                        $"{appName}_{timestamp}.log");

                    _writer = new StreamWriter(_logFilePath, false, Encoding.UTF8)
                    {
                        AutoFlush = false
                    };

                    // Log rotation message
                    _writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [INFO] Log file rotated, previous log was {fileInfo.Length / 1024 / 1024} MB");
                }
            }
            catch
            {
                // Ignore errors during log rotation
            }
        }

        // Convenience methods
        public void Trace(string message) => Log(LogLevel.Trace, message);
        public void Debug(string message) => Log(LogLevel.Debug, message);
        public void Info(string message) => Log(LogLevel.Info, message);
        public void Warning(string message) => Log(LogLevel.Warning, message);
        public void Error(string message, Exception ex = null) => Log(LogLevel.Error, message, ex);
        public void Fatal(string message, Exception ex = null) => Log(LogLevel.Fatal, message, ex);

        // Performance measurement helpers
        public void StartTimer(string operation)
        {
            var timer = new Stopwatch();
            timer.Start();
            _timerDictionary[operation] = timer;
        }

        public TimeSpan StopTimer(string operation, bool logResult = true)
        {
            Stopwatch timer;
            if (_timerDictionary.TryRemove(operation, out timer))
            {
                timer.Stop();
                var elapsed = timer.Elapsed;

                if (logResult)
                {
                    Debug($"Operation '{operation}' took {elapsed.TotalMilliseconds:F2} ms");
                }

                return elapsed;
            }

            return TimeSpan.Zero;
        }

        public void Flush()
        {
            try
            {
                _writer?.Flush();
            }
            catch
            {
                // Ignore flush errors
            }
        }

        public void Dispose()
        {
            if (!_isRunning)
                return;

            try
            {
                _isRunning = false;
                _writeEvent.Set(); // Wake up processing thread

                // Wait for thread to complete
                _flushThread?.Join(3000);

                // Process remaining logs
                while (_pendingLogs.TryDequeue(out LogEntry entry))
                {
                    if (entry.Level >= _fileLogLevel)
                    {
                        _writer?.WriteLine($"[{entry.Timestamp}] [{entry.Category}] {entry.Message}");
                    }
                }

                // Log final message
                _writer?.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [INFO] === Logging ended, total entries: {_logCounter} ===");

                // Close writer
                _writer?.Flush();
                _writer?.Close();
                _writer?.Dispose();
                _writer = null;

                _writeEvent.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error disposing logger: {ex.Message}");
            }
        }

        // Structure for log entries
        private class LogEntry
        {
            public LogLevel Level { get; set; }
            public string Message { get; set; }
            public string Category { get; set; }
            public string Timestamp { get; set; }
        }
    }
}