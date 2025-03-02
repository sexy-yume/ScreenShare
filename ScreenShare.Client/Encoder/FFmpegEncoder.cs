using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen;

namespace ScreenShare.Client.Encoder
{
    public unsafe class FFmpegEncoder : IDisposable
    {
        private AVCodec* _codec;
        private AVCodecContext* _context;
        private AVFrame* _frame;
        private AVPacket* _packet;
        private SwsContext* _swsContext;

        private int _width;
        private int _height;
        private int _quality;
        private int _bitrate;
        private readonly object _encodeLock = new object();
        private Stopwatch _performanceTimer = new Stopwatch();
        private long _frameCount = 0;
        private bool _isDisposed = false;
        private bool _isHardwareEncodingEnabled = false;
        private AVHWDeviceType _hwType = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
        private AVBufferRef* _hwDeviceCtx = null;
        private AVFrame* _hwFrame = null;

        // 키프레임 요청 관련 필드 추가
        private bool _forceKeyframe = false;
        private int _origGopSize = 10;

        public event EventHandler<FrameEncodedEventArgs> FrameEncoded;

        public FFmpegEncoder(int width, int height, int quality = 70, int bitrate = 10000000)
        {
            _width = width;
            _height = height;
            _quality = quality;
            _bitrate = bitrate;

            InitializeFFmpeg();
            _performanceTimer.Start();
        }

        private void InitializeFFmpeg()
        {
            try
            {
                Console.WriteLine("FFmpeg 초기화 시작");

                // Try to find hardware acceleration devices
                if (TryInitializeHardwareAcceleration())
                {
                    Console.WriteLine($"하드웨어 가속 초기화 성공: {_hwType}");
                }
                else
                {
                    Console.WriteLine("하드웨어 가속을 찾을 수 없음, 소프트웨어 인코딩으로 전환");
                }

                // Configure encoder
                if (_isHardwareEncodingEnabled)
                {
                    ConfigureHardwareEncoder();
                }
                else
                {
                    ConfigureSoftwareEncoder();
                }

                // Initialize frame and packet
                InitializeFrameAndPacket();

                Console.WriteLine("FFmpeg 초기화 완료");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FFmpeg 초기화 오류: {ex.Message}");
                Dispose();
                throw;
            }
        }

        private bool TryInitializeHardwareAcceleration()
        {
            // Try to find the best hardware encoder in this order: NVIDIA, Intel QuickSync, AMD
            AVHWDeviceType[] hwPriority = new AVHWDeviceType[]
            {
                AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA,      // NVIDIA
                AVHWDeviceType.AV_HWDEVICE_TYPE_QSV,       // Intel QuickSync
                AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA,   // DirectX 11 Video Acceleration
                AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2,     // DirectX Video Acceleration
                AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI,     // Video Acceleration API (Linux)
                AVHWDeviceType.AV_HWDEVICE_TYPE_DRM        // Direct Rendering Manager (Linux)
            };

            foreach (var deviceType in hwPriority)
            {
                AVBufferRef* deviceCtx = null;
                int ret = ffmpeg.av_hwdevice_ctx_create(&deviceCtx, deviceType, null, null, 0);
                if (ret == 0)
                {
                    _hwDeviceCtx = deviceCtx;
                    _hwType = deviceType;
                    _isHardwareEncodingEnabled = true;
                    return true;
                }
            }

            return false;
        }

        private void ConfigureHardwareEncoder()
        {
            // 하드웨어 타입에 따라 인코더 선택
            switch (_hwType)
            {
                case AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA:
                    _codec = ffmpeg.avcodec_find_encoder_by_name("h264_nvenc");
                    break;
                case AVHWDeviceType.AV_HWDEVICE_TYPE_QSV:
                    _codec = ffmpeg.avcodec_find_encoder_by_name("h264_qsv");
                    break;
                case AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI:
                    _codec = ffmpeg.avcodec_find_encoder_by_name("h264_vaapi");
                    break;
                case AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA:
                    _codec = ffmpeg.avcodec_find_encoder_by_name("h264_d3d11va");
                    break;
                case AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2:
                    _codec = ffmpeg.avcodec_find_encoder_by_name("h264_dxva2");
                    break;
                default:
                    _codec = ffmpeg.avcodec_find_encoder_by_name("h264_nvenc"); // 기본적으로 NVENC 시도
                    break;
            }

            if (_codec == null)
            {
                Console.WriteLine($"하드웨어 인코더를 찾을 수 없습니다. 타입: {_hwType}");
                _isHardwareEncodingEnabled = false;
                ConfigureSoftwareEncoder();
                return;
            }

            // 코덱 컨텍스트 설정
            _context = ffmpeg.avcodec_alloc_context3(_codec);
            _context->width = _width;
            _context->height = _height;
            _context->time_base = new AVRational { num = 1, den = 30 };
            _context->framerate = new AVRational { num = 30, den = 1 };
            _context->bit_rate = _bitrate;
            _context->rc_max_rate = _bitrate * 2;
            _context->rc_buffer_size = _bitrate;
            _context->gop_size = 30;
            _origGopSize = 30; // 원래 GOP 크기 저장
            _context->max_b_frames = 0;
            _context->pix_fmt = GetHardwarePixelFormat();
            _context->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
            _context->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;

            // 하드웨어 가속 설정
            if (_hwDeviceCtx != null)
            {
                // hw_device_ctx 설정
                _context->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDeviceCtx);

                // hw_frames_ctx 생성 및 설정
                AVHWFramesConstraints* constraints = ffmpeg.av_hwdevice_get_hwframe_constraints(_hwDeviceCtx, null);
                if (constraints != null)
                {
                    // 프레임 컨텍스트 생성
                    AVBufferRef* hw_frames_ctx = ffmpeg.av_hwframe_ctx_alloc(_hwDeviceCtx);
                    AVHWFramesContext* frames_ctx = (AVHWFramesContext*)hw_frames_ctx->data;

                    // 프레임 컨텍스트 설정
                    frames_ctx->format = GetHardwarePixelFormat();
                    frames_ctx->sw_format = AVPixelFormat.AV_PIX_FMT_NV12; // NVENC은 보통 NV12 사용
                    frames_ctx->width = _width;
                    frames_ctx->height = _height;
                    frames_ctx->initial_pool_size = 3; // 초기 프레임 풀 크기

                    // 프레임 컨텍스트 초기화
                    int ret = ffmpeg.av_hwframe_ctx_init(hw_frames_ctx);
                    if (ret < 0)
                    {
                        string errorMsg = GetErrorMessage(ret);
                        Console.WriteLine($"hw_frames_ctx 초기화 실패: {errorMsg}");
                        ffmpeg.av_buffer_unref(&hw_frames_ctx);
                        _isHardwareEncodingEnabled = false;
                        ConfigureSoftwareEncoder();
                        return;
                    }

                    // 코덱 컨텍스트에 프레임 컨텍스트 설정
                    _context->hw_frames_ctx = ffmpeg.av_buffer_ref(hw_frames_ctx);
                    ffmpeg.av_buffer_unref(&hw_frames_ctx);

                    // 제약조건 해제
                    ffmpeg.av_hwframe_constraints_free(&constraints);
                }
                else
                {
                    Console.WriteLine("하드웨어 프레임 제약조건을 가져올 수 없습니다.");
                    _isHardwareEncodingEnabled = false;
                    ConfigureSoftwareEncoder();
                    return;
                }
            }
            else
            {
                Console.WriteLine("하드웨어 디바이스 컨텍스트가 NULL입니다.");
                _isHardwareEncodingEnabled = false;
                ConfigureSoftwareEncoder();
                return;
            }

            // 인코딩 옵션 설정 - 최소 지연 중심
            AVDictionary* opts = null;

            // NVIDIA NVENC 설정 (가장 빠른 설정)
            if (_hwType == AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA)
            {
                ffmpeg.av_dict_set(&opts, "preset", "p1", 0);          // 가장 빠른 프리셋
                ffmpeg.av_dict_set(&opts, "tune", "ull", 0);           // 초저지연
                ffmpeg.av_dict_set(&opts, "zerolatency", "1", 0);
                ffmpeg.av_dict_set(&opts, "delay", "0", 0);
                ffmpeg.av_dict_set(&opts, "surfaces", "4", 0);
                ffmpeg.av_dict_set(&opts, "rc", "cbr", 0);             // 일정 비트레이트

                // 고급 설정 제거 - 지연 최소화
                ffmpeg.av_dict_set(&opts, "rc-lookahead", "0", 0);     // 룩어헤드 비활성화
            }
            else if (_hwType == AVHWDeviceType.AV_HWDEVICE_TYPE_QSV)
            {
                ffmpeg.av_dict_set(&opts, "low_delay", "1", 0);
                ffmpeg.av_dict_set(&opts, "look_ahead", "0", 0);       // 룩어헤드 비활성화
            }
            else if (_hwType == AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI)
            {
                ffmpeg.av_dict_set(&opts, "low_power", "1", 0);        // 저전력 모드 (일부 하드웨어에서만 지원)
            }

            // 공통 설정
            ffmpeg.av_dict_set(&opts, "threads", "auto", 0);

            // 코덱 열기
            int result = ffmpeg.avcodec_open2(_context, _codec, &opts);
            if (result < 0)
            {
                string errorMsg = GetErrorMessage(result);
                Console.WriteLine($"하드웨어 코덱 열기 실패: {errorMsg}");
                _isHardwareEncodingEnabled = false;
                ConfigureSoftwareEncoder();
            }
            else
            {
                Console.WriteLine($"하드웨어 인코더 초기화 성공: {_hwType}");
            }
        }

        private AVPixelFormat GetHardwarePixelFormat()
        {
            switch (_hwType)
            {
                case AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA:
                    return AVPixelFormat.AV_PIX_FMT_CUDA;
                case AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI:
                    return AVPixelFormat.AV_PIX_FMT_VAAPI;
                case AVHWDeviceType.AV_HWDEVICE_TYPE_QSV:
                    return AVPixelFormat.AV_PIX_FMT_QSV;
                case AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA:
                    return AVPixelFormat.AV_PIX_FMT_D3D11;
                case AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2:
                    return AVPixelFormat.AV_PIX_FMT_DXVA2_VLD;
                default:
                    return AVPixelFormat.AV_PIX_FMT_YUV420P;
            }
        }

        private void ConfigureSoftwareEncoder()
        {
            // H.264 소프트웨어 코덱
            _codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_H264);
            if (_codec == null)
                throw new Exception("H.264 코덱을 찾을 수 없습니다.");

            // 코덱 컨텍스트 설정
            _context = ffmpeg.avcodec_alloc_context3(_codec);
            _context->width = _width;
            _context->height = _height;
            _context->time_base = new AVRational { num = 1, den = 60 };
            _context->framerate = new AVRational { num = 60, den = 1 };
            _context->bit_rate = _bitrate;
            _context->rc_max_rate = _bitrate * 2;
            _context->rc_buffer_size = _bitrate;
            _context->gop_size = 10;  // 원래 설정으로 복원
            _origGopSize = 10; // 원래 GOP 크기 저장
            _context->max_b_frames = 0;
            _context->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
            _context->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
            _context->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;

            // 품질 설정 (CRF 방식)
            int crf = Math.Max(0, Math.Min(51, 30 - (_quality * 25 / 100))); // 원래 계산식으로 복원

            // 인코딩 옵션 설정 - 초저지연 중심
            AVDictionary* opts = null;
            ffmpeg.av_dict_set(&opts, "preset", "ultrafast", 0); // 가장 빠른 프리셋
            ffmpeg.av_dict_set(&opts, "tune", "zerolatency", 0);
            ffmpeg.av_dict_set(&opts, "crf", crf.ToString(), 0);
            ffmpeg.av_dict_set(&opts, "threads", "auto", 0);
            ffmpeg.av_dict_set(&opts, "profile", "baseline", 0); // 가장 기본적인 프로파일

            // 지연 최소화를 위한 추가 설정
            ffmpeg.av_dict_set(&opts, "x264opts", "no-mbtree:sliced-threads:sync-lookahead=0", 0);

            // 코덱 열기
            int result = ffmpeg.avcodec_open2(_context, _codec, &opts);
            if (result < 0)
            {
                string errorMsg = GetErrorMessage(result);
                throw new Exception($"코덱을 열 수 없습니다: {errorMsg}");
            }
        }

        private void InitializeFrameAndPacket()
        {
            // Create frames - one for normal operations and an optional one for hardware
            _frame = ffmpeg.av_frame_alloc();
            _frame->format = (int)(_isHardwareEncodingEnabled ? AVPixelFormat.AV_PIX_FMT_NV12 : _context->pix_fmt);
            _frame->width = _width;
            _frame->height = _height;

            // Allocate frame buffer
            int result = ffmpeg.av_frame_get_buffer(_frame, 32);
            if (result < 0)
            {
                string errorMsg = GetErrorMessage(result);
                throw new Exception($"프레임 버퍼 할당 실패: {errorMsg}");
            }

            // For hardware encoding, create a hw frame
            if (_isHardwareEncodingEnabled && _context->hw_frames_ctx != null)
            {
                _hwFrame = ffmpeg.av_frame_alloc();
                _hwFrame->format = (int)GetHardwarePixelFormat();
                _hwFrame->width = _width;
                _hwFrame->height = _height;
            }

            // Allocate packet
            _packet = ffmpeg.av_packet_alloc();

            // Create SWS context for BGRA to required pixel format conversion
            AVPixelFormat srcFormat = AVPixelFormat.AV_PIX_FMT_BGRA;
            AVPixelFormat dstFormat = _isHardwareEncodingEnabled ? AVPixelFormat.AV_PIX_FMT_NV12 : AVPixelFormat.AV_PIX_FMT_YUV420P;

            _swsContext = ffmpeg.sws_getContext(
                _width, _height, srcFormat,
                _width, _height, dstFormat,
                ffmpeg.SWS_FAST_BILINEAR, null, null, null);

            if (_swsContext == null)
                throw new Exception("SWS 컨텍스트 초기화 실패");
        }

        public void SetBitrate(int bitrate)
        {
            if (_isDisposed)
                return;

            lock (_encodeLock)
            {
                if (_bitrate != bitrate)
                {
                    _bitrate = bitrate;

                    // Update codec context bitrate settings
                    _context->bit_rate = bitrate;
                    _context->rc_max_rate = bitrate * 2;
                    _context->rc_buffer_size = bitrate;

                    Console.WriteLine($"비트레이트 변경: {bitrate / 1000} kbps");
                }
            }
        }

        /// <summary>
        /// 키프레임을 강제로 생성합니다.
        /// </summary>
        public void ForceKeyframe()
        {
            if (_isDisposed)
                return;

            lock (_encodeLock)
            {
                try
                {
                    Console.WriteLine("키프레임 강제 생성 요청");
                    _forceKeyframe = true;

                    // 하드웨어 인코더에서 강제 키프레임 설정
                    if (_isHardwareEncodingEnabled && _context->priv_data != null)
                    {
                        // NVENC의 경우 force_key 또는 forced_idr 사용
                        if (_hwType == AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA)
                        {
                            ffmpeg.av_opt_set(_context->priv_data, "forced_idr", "1", 0);
                        }
                        // QSV의 경우
                        else if (_hwType == AVHWDeviceType.AV_HWDEVICE_TYPE_QSV)
                        {
                            ffmpeg.av_opt_set(_context->priv_data, "forced_key", "1", 0);
                        }
                    }
                    else
                    {
                        // 소프트웨어 인코더의 경우 GOP 크기를 1로 변경
                        // (다음 프레임은 무조건 키프레임이 됨)
                        _context->gop_size = 1;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"키프레임 강제 생성 오류: {ex.Message}");
                }
            }
        }

        private string GetErrorMessage(int errorCode)
        {
            const int bufferSize = 1024;
            var buffer = new byte[bufferSize];

            fixed (byte* bufferPtr = buffer)
            {
                ffmpeg.av_strerror(errorCode, bufferPtr, (ulong)bufferSize);
            }

            return Encoding.UTF8.GetString(buffer).TrimEnd('\0');
        }

        public void EncodeFrame(Bitmap bitmap)
        {
            if (_isDisposed)
                return;

            lock (_encodeLock)
            {
                Stopwatch sw = Stopwatch.StartNew();

                var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
                var bitmapData = bitmap.LockBits(
                    rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                try
                {
                    byte* srcData = (byte*)bitmapData.Scan0;
                    int srcStride = bitmapData.Stride;

                    byte_ptrArray4 srcDataPtr = new byte_ptrArray4();
                    srcDataPtr[0] = srcData;
                    int_array4 srcStrides = new int_array4();
                    srcStrides[0] = srcStride;

                    byte_ptrArray4 dstDataPtr = new byte_ptrArray4();
                    dstDataPtr[0] = _frame->data[0];
                    dstDataPtr[1] = _frame->data[1];
                    dstDataPtr[2] = _frame->data[2];
                    int_array4 dstStrides = new int_array4();
                    dstStrides[0] = _frame->linesize[0];
                    dstStrides[1] = _frame->linesize[1];
                    dstStrides[2] = _frame->linesize[2];

                    // Convert BGRA to the required format (YUV420P or NV12)
                    ffmpeg.sws_scale(_swsContext, srcDataPtr, srcStrides, 0, _height, dstDataPtr, dstStrides);

                    // Set timestamp
                    _frame->pts = _frameCount++;

                    // 키프레임 강제 요청 처리
                    if (_forceKeyframe)
                    {
                        // pict_type을 I-frame으로 설정
                        _frame->pict_type = AVPictureType.AV_PICTURE_TYPE_I;
                        _forceKeyframe = false;

                        // GOP 크기를 원래대로 복원 (다음 프레임부터)
                        Task.Run(() =>
                        {
                            Thread.Sleep(100);  // 현재 프레임이 전송되도록 잠시 대기
                            lock (_encodeLock)
                            {
                                if (!_isDisposed)
                                {
                                    _context->gop_size = _origGopSize;
                                    Console.WriteLine($"GOP 크기 복원: {_origGopSize}");
                                }
                            }
                        });
                    }

                    if (_isHardwareEncodingEnabled && _hwFrame != null && _context->hw_frames_ctx != null)
                    {
                        EncodeWithHardware();
                    }
                    else
                    {
                        EncodeWithSoftware();
                    }

                    sw.Stop();
                    if (_frameCount % 60 == 0)
                    {
                        double averageEncodingTime = _performanceTimer.ElapsedMilliseconds / (double)_frameCount;
                        Console.WriteLine($"인코딩 시간: {sw.ElapsedMilliseconds}ms, 총 프레임: {_frameCount}, 평균: {averageEncodingTime:F2}ms/프레임");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"인코딩 오류: {ex.Message}");
                }
                finally
                {
                    bitmap.UnlockBits(bitmapData);
                }
            }
        }

        private void EncodeWithHardware()
        {
            // Transfer data from CPU to GPU
            int ret = ffmpeg.av_hwframe_get_buffer(_context->hw_frames_ctx, _hwFrame, 0);
            if (ret < 0)
            {
                string errorMsg = GetErrorMessage(ret);
                Console.WriteLine($"하드웨어 프레임 버퍼 할당 실패: {errorMsg}");
                EncodeWithSoftware(); // Fallback to software encoding
                return;
            }

            ret = ffmpeg.av_hwframe_transfer_data(_hwFrame, _frame, 0);
            if (ret < 0)
            {
                string errorMsg = GetErrorMessage(ret);
                Console.WriteLine($"하드웨어 프레임 데이터 전송 실패: {errorMsg}");
                EncodeWithSoftware(); // Fallback to software encoding
                return;
            }

            _hwFrame->pts = _frame->pts;

            // Send frame to encoder
            ret = ffmpeg.avcodec_send_frame(_context, _hwFrame);
            if (ret < 0)
            {
                string errorMsg = GetErrorMessage(ret);
                Console.WriteLine($"하드웨어 프레임 인코딩 실패: {errorMsg}");
                EncodeWithSoftware(); // Fallback to software encoding
                return;
            }

            // Receive encoded packets
            ReceivePackets();
        }

        private void EncodeWithSoftware()
        {
            // Send frame to encoder
            int ret = ffmpeg.avcodec_send_frame(_context, _frame);
            if (ret < 0)
            {
                string errorMsg = GetErrorMessage(ret);
                Console.WriteLine($"프레임 인코딩 실패: {errorMsg}");
                return;
            }

            // Receive encoded packets
            ReceivePackets();
        }

        private void ReceivePackets()
        {
            while (true)
            {
                int ret = ffmpeg.avcodec_receive_packet(_context, _packet);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                    break;

                if (ret < 0)
                {
                    string errorMsg = GetErrorMessage(ret);
                    Console.WriteLine($"패킷 수신 실패: {errorMsg}");
                    break;
                }

                // Copy encoded data
                byte[] encodedData = new byte[_packet->size];
                Marshal.Copy((IntPtr)_packet->data, encodedData, 0, _packet->size);

                // Check if this is a keyframe
                bool isKeyFrame = (_packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;

                // Create frame encoded event args
                var args = new FrameEncodedEventArgs
                {
                    EncodedData = encodedData,
                    IsKeyFrame = isKeyFrame
                };

                // Trigger event
                FrameEncoded?.Invoke(this, args);

                if (isKeyFrame)
                {
                    Console.WriteLine($"키프레임 생성: size={encodedData.Length}");
                }

                ffmpeg.av_packet_unref(_packet);
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            _performanceTimer.Stop();

            double totalTime = _performanceTimer.ElapsedMilliseconds;
            double averageTime = _frameCount > 0 ? totalTime / _frameCount : 0;

            Console.WriteLine($"FFmpeg 인코더 종료. 하드웨어 가속: {(_isHardwareEncodingEnabled ? "활성화" : "비활성화")}, " +
                              $"총 {_frameCount}개 프레임 인코딩, 평균 처리 시간: {averageTime:F2}ms/프레임");

            lock (_encodeLock)
            {
                if (_packet != null)
                {
                    fixed (AVPacket** packet = &_packet)
                    {
                        ffmpeg.av_packet_free(packet);
                    }
                    _packet = null;
                }

                if (_hwFrame != null)
                {
                    fixed (AVFrame** frame = &_hwFrame)
                    {
                        ffmpeg.av_frame_free(frame);
                    }
                    _hwFrame = null;
                }

                if (_frame != null)
                {
                    fixed (AVFrame** frame = &_frame)
                    {
                        ffmpeg.av_frame_free(frame);
                    }
                    _frame = null;
                }

                if (_context != null)
                {
                    fixed (AVCodecContext** context = &_context)
                    {
                        ffmpeg.avcodec_free_context(context);
                    }
                    _context = null;
                }

                if (_hwDeviceCtx != null)
                {
                    AVBufferRef* temp = _hwDeviceCtx;
                    ffmpeg.av_buffer_unref(&temp);
                    _hwDeviceCtx = null;
                }

                if (_swsContext != null)
                {
                    ffmpeg.sws_freeContext(_swsContext);
                    _swsContext = null;
                }
            }

            GC.SuppressFinalize(this);
        }

        ~FFmpegEncoder()
        {
            Dispose();
        }
    }

    /// <summary>
    /// 인코딩된 프레임 이벤트 인자
    /// </summary>
    public class FrameEncodedEventArgs : EventArgs
    {
        /// <summary>
        /// 인코딩된 프레임 데이터
        /// </summary>
        public byte[] EncodedData { get; set; }

        /// <summary>
        /// 키프레임 여부
        /// </summary>
        public bool IsKeyFrame { get; set; }
    }
}