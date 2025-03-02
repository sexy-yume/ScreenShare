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

        public event EventHandler<Bitmap> FrameCaptured;

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
            

            while (_capturing)
            {
                frameTimer.Restart();
                int targetFrameTime = 1000 / _fps;
                try
                {
                    // 화면 캡처
                    byte[] screenData = CaptureScreen(50); // 50ms 타임아웃

                    if (screenData != null)
                    {
                        _failedCaptureCount = 0;
                        _frameCount++;

                        // BGRA 바이트 배열을 Bitmap으로 변환
                        using (Bitmap bitmap = CreateBitmapFromBuffer(screenData, _width, _height))
                        {
                            if (bitmap != null)
                            {
                                // 타임스탬프 표시 (디버깅용, 필요 없으면 제거)
                                using (Graphics g = Graphics.FromImage(bitmap))
                                {
                                    string text = $"{DateTime.Now:HH:mm:ss.fff}";
                                    g.DrawString(text, new Font("Arial", 10), Brushes.Yellow, 10, 10);
                                }

                                // 클론을 만들어 이벤트 발생 (비트맵 객체가 일회용이므로)
                                Bitmap clonedBitmap = new Bitmap(bitmap);
                                FrameCaptured?.Invoke(this, clonedBitmap);
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
                        double fps = _frameCount / seconds;
                        FileLogger.Instance.WriteInfo($"캡처 FPS: {fps:F1}, 총 프레임: {_frameCount}");
                        
                        _frameCount = 0;
                        _fpsTimer.Restart();
                    }

                    // 프레임 레이트 조절
                    int elapsed = (int)frameTimer.ElapsedMilliseconds;
                    int sleepTime = Math.Max(0, targetFrameTime - elapsed);

                    if (sleepTime > 0)
                    {
                        //FileLogger.Instance.WriteInfo($"sleepTime : {sleepTime}");
                        Thread.Sleep(sleepTime);
                    }
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