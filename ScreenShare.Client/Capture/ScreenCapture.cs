using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.DXGI;
using SharpDX.Direct3D11;
using SharpDX.Direct3D;
using Device = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;
using ScreenShare.Common.Utils;
using DXGIResultCode = SharpDX.DXGI.ResultCode;

namespace ScreenShare.Client.Capture
{
    
    public class OptimizedScreenCapture : IDisposable
    {
        private readonly Device _device;
        private readonly OutputDuplication _duplicatedOutput;
        private readonly Texture2D _sharedTexture;
        private readonly int _width;
        private readonly int _height;
        private readonly object _lockObject = new object();
        private readonly byte[] _buffer;
        private readonly GCHandle _pinnedArray;
        private bool _disposed;

        private Thread _captureThread;
        private bool _capturing;
        private int _fps = 8;
        private int _quality = 70;
        private Stopwatch _fpsTimer = new Stopwatch();
        private int _frameCount = 0;
        private int _failedCaptureCount = 0;

        public event EventHandler<CaptureData> FrameCaptured;

        private readonly Queue<double> _processingTimeHistory = new Queue<double>(30); // 최근 30개 프레임의 처리 시간
        private readonly object _historyLock = new object();
        private double _averageProcessingTime = 0;
        private double _lastCaptureTimeMs = 0;
        private double _lastEncodingTimeMs = 0;
        private bool _remoteModeActive = false;

        public class CaptureData : EventArgs
        {
            public Bitmap Bitmap { get; set; }
            public double CaptureTimeMs { get; set; }
        }

        public int Fps
        {
            get => _fps;
            set => _fps = value;
        }

        public int Quality
        {
            get => _quality;
            set => _quality = value;
        }

        public OptimizedScreenCapture(int screenIndex = 0)
        {
            try
            {
                FileLogger.Instance.WriteInfo("OptimizedScreenCapture 초기화 시작");

                // Device 초기화
                var deviceFlags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.SingleThreaded;
                _device = new Device(DriverType.Hardware, deviceFlags);

                // Output 초기화
                using (var factory = new Factory1())
                using (var adapter = factory.GetAdapter1(0))
                {
                    var output = adapter.GetOutput(screenIndex);
                    var output1 = output.QueryInterface<Output1>();

                    _width = output1.Description.DesktopBounds.Right - output1.Description.DesktopBounds.Left;
                    _height = output1.Description.DesktopBounds.Bottom - output1.Description.DesktopBounds.Top;

                    FileLogger.Instance.WriteInfo($"화면 크기: {_width}x{_height}");

                    _duplicatedOutput = output1.DuplicateOutput(_device);
                    output1.Dispose();
                    output.Dispose();
                }

                // 공유 텍스처 생성
                var textureDesc = new Texture2DDescription
                {
                    CpuAccessFlags = CpuAccessFlags.Read,
                    BindFlags = BindFlags.None,
                    Format = Format.B8G8R8A8_UNorm,
                    Width = _width,
                    Height = _height,
                    OptionFlags = ResourceOptionFlags.Shared,
                    MipLevels = 1,
                    ArraySize = 1,
                    SampleDescription = { Count = 1, Quality = 0 },
                    Usage = ResourceUsage.Staging
                };

                _sharedTexture = new Texture2D(_device, textureDesc);

                // 고정된 메모리 할당
                _buffer = new byte[_width * _height * 4];
                _pinnedArray = GCHandle.Alloc(_buffer, GCHandleType.Pinned);

                FileLogger.Instance.WriteInfo("OptimizedScreenCapture 초기화 완료");
            }
            catch (Exception ex)
            {
                FileLogger.Instance.WriteError("OptimizedScreenCapture 초기화 오류", ex);
                Dispose();
                throw;
            }
        }

        public void Start()
        {
            if (_captureThread != null) return;

            _capturing = true;
            _captureThread = new Thread(CaptureLoop)
            {
                IsBackground = true,
                Name = "Screen Capture Thread",
                Priority = ThreadPriority.AboveNormal
            };
            _captureThread.Start();
            _fpsTimer.Start();

            FileLogger.Instance.WriteInfo("화면 캡처 시작");
        }

        public void Stop()
        {
            _capturing = false;
            _captureThread?.Join(1000);
            _captureThread = null;
            _fpsTimer.Stop();

            FileLogger.Instance.WriteInfo("화면 캡처 중지");
        }

        private void CaptureLoop()
        {
            Stopwatch frameTimer = new Stopwatch();
            Stopwatch captureTimer = new Stopwatch();
            DateTime lastCaptureTime = DateTime.MinValue;

            while (_capturing)
            {
                try
                {
                    frameTimer.Restart();

                    // 1. 목표 프레임 간격 (ms)
                    int targetFrameTime = 1000 / _fps;

                    // 2. 순수 처리 시간(캡처+인코딩)이 목표 간격보다 길면 적응
                    // 평균 처리 시간에 5% 여유를 추가하여 적응형 간격 계산
                    double adaptiveInterval = Math.Max(targetFrameTime, _averageProcessingTime * 1.05);

                    // 3. 현재 시간과 마지막 캡처 시간의 간격 확인
                    TimeSpan timeSinceLastCapture = DateTime.Now - lastCaptureTime;

                    // 4. 적응형 간격보다 짧게 지났으면 대기
                    if (timeSinceLastCapture.TotalMilliseconds < adaptiveInterval - 2) // 2ms 여유
                    {
                        int waitTime = (int)(adaptiveInterval - timeSinceLastCapture.TotalMilliseconds);
                        if (waitTime > 1)
                        {
                            // 반응성을 위해 최소한의 대기만 수행
                            Thread.Sleep(Math.Min(waitTime / 2, 5));
                            continue;
                        }
                    }

                    // 5. 화면 캡처 수행
                    captureTimer.Restart();
                    byte[] screenData = CaptureScreen(50); // 50ms 타임아웃
                    double captureTimeMs = captureTimer.ElapsedMilliseconds;

                    if (screenData != null)
                    {
                        _failedCaptureCount = 0;
                        _frameCount++;

                        // 캡처 시간 기록
                        lastCaptureTime = DateTime.Now;

                        // 6. Bitmap 변환 및 인코딩
                        using (Bitmap bitmap = CreateBitmapFromBuffer(screenData, _width, _height))
                        {
                            if (bitmap != null)
                            {
                                // 타임스탬프와 성능 정보 표시
                                using (Graphics g = Graphics.FromImage(bitmap))
                                {
                                    string timeText = $"{DateTime.Now:HH:mm:ss.fff}";
                                    g.DrawString(timeText, new Font("Arial", 10), Brushes.Yellow, 10, 10);

                                    // 성능 정보 표시
                                    string perfText = $"Cap: {_lastCaptureTimeMs:F1}ms, Enc: {_lastEncodingTimeMs:F1}ms, " +
                                                    $"Total: {_averageProcessingTime:F1}ms, " +
                                                    $"Mode: {(_remoteModeActive ? "Remote" : "Normal")}";
                                    g.DrawString(perfText, new Font("Arial", 10), Brushes.Lime, 10, 30);
                                }

                                // 클론을 만들어 이벤트 발생 (비트맵 객체가 일회용이므로)
                                Bitmap clonedBitmap = new Bitmap(bitmap);

                                // 캡처 시간 전달 (OnFrameCaptured 핸들러에서 사용)
                                var captureData = new CaptureData
                                {
                                    Bitmap = clonedBitmap,
                                    CaptureTimeMs = captureTimeMs
                                };

                                // 비트맵과 함께 캡처 시간도 전달
                                FrameCaptured?.Invoke(this, captureData);
                            }
                        }
                    }
                    else
                    {
                        // 캡처 실패 시 카운트 증가
                        _failedCaptureCount++;

                        // 연속 5회 이상 실패 시 로그
                        if (_failedCaptureCount >= 5 && _failedCaptureCount % 5 == 0)
                        {
                            FileLogger.Instance.WriteWarning($"연속 {_failedCaptureCount}회 화면 캡처 실패");
                        }
                    }

                    // 5초마다 성능 로그
                    if (_fpsTimer.ElapsedMilliseconds >= 5000)
                    {
                        double seconds = _fpsTimer.ElapsedMilliseconds / 1000.0;
                        double actualFps = _frameCount / seconds;
                        double theoreticalMaxFps = 1000 / Math.Max(1, _averageProcessingTime);

                        FileLogger.Instance.WriteInfo(
                            $"캡처 통계: 실제 FPS={actualFps:F1}, 목표 FPS={_fps}, " +
                            $"가능한 최대 FPS={theoreticalMaxFps:F1}, " +
                            $"평균 처리 시간={_averageProcessingTime:F1}ms, " +
                            $"현재 모드={(_remoteModeActive ? "원격" : "일반")}");

                        _frameCount = 0;
                        _fpsTimer.Restart();
                    }

                    // 짧은 지연으로 CPU 사용량 제어 (선택적)
                    Thread.Sleep(1);
                }
                catch (Exception ex)
                {
                    FileLogger.Instance.WriteError("캡처 루프 오류", ex);
                    Thread.Sleep(1000); // 오류 발생 시 잠시 대기
                }
            }
        }

        private unsafe byte[] CaptureScreen(int timeoutMs = 10)
        {
            lock (_lockObject)
            {
                try
                {
                    SharpDX.DXGI.Resource screenResource;
                    OutputDuplicateFrameInformation duplicateFrameInformation;

                    // 다음 프레임 획득 시도
                    Result result = _duplicatedOutput.TryAcquireNextFrame(
                        timeoutMs,
                        out duplicateFrameInformation,
                        out screenResource);

                    if (result.Failure)
                    {
                        return null; // 타임아웃 또는 오류 발생
                    }

                    // 리소스에서 텍스처 가져오기
                    using (var screenTexture = screenResource.QueryInterface<Texture2D>())
                    {
                        _device.ImmediateContext.CopyResource(screenTexture, _sharedTexture);
                    }

                    screenResource.Dispose();
                    _duplicatedOutput.ReleaseFrame();

                    // DataBox를 사용하여 메모리 접근
                    var dataBox = _device.ImmediateContext.MapSubresource(
                        _sharedTexture,
                        0,
                        MapMode.Read,
                        MapFlags.None);

                    // 고정된 버퍼에 데이터 복사
                    for (int row = 0; row < _height; row++)
                    {
                        Marshal.Copy(
                            dataBox.DataPointer + row * dataBox.RowPitch,
                            _buffer,
                            row * _width * 4,
                            _width * 4);
                    }

                    _device.ImmediateContext.UnmapSubresource(_sharedTexture, 0);

                    return _buffer;
                }
                catch (SharpDXException ex)
                {
                    // 디바이스 손실 또는 액세스 권한 오류 처리
                    if (ex.ResultCode.Code == DXGIResultCode.DeviceRemoved.Result.Code ||
                        ex.ResultCode.Code == DXGIResultCode.DeviceReset.Result.Code ||
                        ex.ResultCode.Code == DXGIResultCode.AccessLost.Result.Code)
                    {
                        FileLogger.Instance.WriteError("DirectX 디바이스 오류 - 재초기화 필요", ex);
                    }
                    else
                    {
                        FileLogger.Instance.WriteError("DirectX 캡처 오류", ex);
                    }
                    return null;
                }
                catch (Exception ex)
                {
                    FileLogger.Instance.WriteError("일반 캡처 오류", ex);
                    return null;
                }
            }
        }

        private Bitmap CreateBitmapFromBuffer(byte[] buffer, int width, int height)
        {
            try
            {
                Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);

                BitmapData bitmapData = bitmap.LockBits(
                    new System.Drawing.Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format32bppArgb);

                // 버퍼의 데이터를 비트맵으로 복사
                Marshal.Copy(buffer, 0, bitmapData.Scan0, buffer.Length);

                bitmap.UnlockBits(bitmapData);

                return bitmap;
            }
            catch (Exception ex)
            {
                FileLogger.Instance.WriteError("비트맵 생성 오류", ex);
                return null;
            }
        }
        public void SetRemoteMode(bool active, int newFps, int newQuality)
        {
            _remoteModeActive = active;
            Fps = newFps;
            Quality = newQuality;

            // 중요: 모드 변경 시 처리 시간 히스토리 초기화 
            ResetProcessingTimeHistory();

            EnhancedLogger.Instance.Info(
                $"{(active ? "원격 제어" : "일반")} 모드 설정: FPS={Fps}, 품질={Quality}");
        }

        // 처리 시간 히스토리 초기화 (FPS 변경 시 호출)
        private void ResetProcessingTimeHistory()
        {
            lock (_historyLock)
            {
                _processingTimeHistory.Clear();
                _averageProcessingTime = 0;

                // 첫 몇 개 프레임은 빠르게 적응하도록 작은 초기값으로 설정
                for (int i = 0; i < 5; i++)
                {
                    _processingTimeHistory.Enqueue(10); // 10ms 초기값 (빠른 적응)
                }
                UpdateAverageProcessingTime();
            }
        }

        // 처리 시간 히스토리 업데이트 및 평균 계산
        private void AddProcessingTime(double captureTimeMs, double encodingTimeMs)
        {
            _lastCaptureTimeMs = captureTimeMs;
            _lastEncodingTimeMs = encodingTimeMs;
            double totalProcessingTime = captureTimeMs + encodingTimeMs;

            lock (_historyLock)
            {
                // 히스토리 크기 제한
                if (_processingTimeHistory.Count >= 30)
                {
                    _processingTimeHistory.Dequeue();
                }

                _processingTimeHistory.Enqueue(totalProcessingTime);
                UpdateAverageProcessingTime();
            }
        }

        // 평균 처리 시간 업데이트
        private void UpdateAverageProcessingTime()
        {
            if (_processingTimeHistory.Count == 0)
                return;

            _averageProcessingTime = _processingTimeHistory.Average();
        }

        // 인코딩 완료 이벤트 처리 메서드 - 클래스에 추가
        public void OnEncodingCompleted(double captureTimeMs, double encodingTimeMs)
        {
            // 처리 시간 히스토리 업데이트
            AddProcessingTime(captureTimeMs, encodingTimeMs);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Stop();

                if (_pinnedArray.IsAllocated)
                {
                    _pinnedArray.Free();
                }

                _sharedTexture?.Dispose();
                _duplicatedOutput?.Dispose();
                _device?.Dispose();

                _disposed = true;

                FileLogger.Instance.WriteInfo("OptimizedScreenCapture 리소스 해제");
            }

            GC.SuppressFinalize(this);
        }

        ~OptimizedScreenCapture()
        {
            Dispose();
        }
    }
}