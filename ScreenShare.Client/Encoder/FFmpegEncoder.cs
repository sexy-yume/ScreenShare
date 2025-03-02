using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen;
using ScreenShare.Common.Utils;

namespace ScreenShare.Client.Encoder
{
    /// <summary>
    /// FFmpeg을 사용한 향상된 화면 인코더 - 키프레임 관리 및 인코딩 안정성 개선
    /// </summary>
    public unsafe class FFmpegEncoder : IDisposable
    {
        // 코어 FFmpeg 컴포넌트
        private AVCodec* _codec;
        private AVCodecContext* _context;
        private AVFrame* _frame;
        private AVPacket* _packet;
        private SwsContext* _swsContext;

        // 하드웨어 가속 관련
        private AVHWDeviceType _hwType = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
        private AVBufferRef* _hwDeviceCtx = null;
        private AVFrame* _hwFrame = null;
        private bool _isHardwareEncodingEnabled = false;

        // 크기 및 상태
        private int _width;
        private int _height;
        private int _quality;
        private int _bitrate;
        private readonly object _encodeLock = new object();

        // 성능 추적
        private Stopwatch _performanceTimer = new Stopwatch();
        private long _frameCount = 0;
        private long _keyframeCount = 0;
        private bool _isDisposed = false;

        // 키프레임 제어 개선
        private bool _forceKeyframe = false;
        private int _origGopSize = 10;
        private DateTime _lastKeyframe = DateTime.MinValue;
        private int _minKeyframeInterval = 2000; // 최소 2초 간격으로 키프레임 허용 (밀리초)
        private Stopwatch _keyframeIntervalTimer = new Stopwatch();
        private bool _keyframeInProgress = false;

        // 인코딩 옵션
        private bool _useLowLatencySettings = true;
        private bool _enableHardwareEncoding = true;
        private int _defaultGopSize = 10;

        // 이벤트
        public event EventHandler<FrameEncodedEventArgs> FrameEncoded;

        public FFmpegEncoder(int width, int height, int quality = 70, int bitrate = 10000000)
        {
            _width = width;
            _height = height;
            _quality = quality;
            _bitrate = bitrate;

            InitializeFFmpeg();
            _performanceTimer.Start();
            _keyframeIntervalTimer.Start();
        }

        private void InitializeFFmpeg()
        {
            try
            {
                EnhancedLogger.Instance.Info("FFmpeg 인코더 초기화 시작");

                // 하드웨어 가속 초기화 시도
                if (_enableHardwareEncoding && TryInitializeHardwareAcceleration())
                {
                    EnhancedLogger.Instance.Info($"하드웨어 가속 초기화 성공: {_hwType}");
                }
                else
                {
                    EnhancedLogger.Instance.Info("하드웨어 가속을 찾을 수 없음, 소프트웨어 인코딩으로 전환");
                }

                // 인코더 설정
                if (_isHardwareEncodingEnabled)
                {
                    ConfigureHardwareEncoder();
                }
                else
                {
                    ConfigureSoftwareEncoder();
                }

                // 프레임 및 패킷 초기화
                InitializeFrameAndPacket();

                EnhancedLogger.Instance.Info("FFmpeg 인코더 초기화 완료");
            }
            catch (Exception ex)
            {
                EnhancedLogger.Instance.Error($"FFmpeg 인코더 초기화 오류: {ex.Message}", ex);
                Dispose();
                throw;
            }
        }

        private bool TryInitializeHardwareAcceleration()
        {
            // 하드웨어 인코더 우선순위: NVIDIA, Intel QuickSync, AMD 등
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
                EnhancedLogger.Instance.Warning($"하드웨어 인코더를 찾을 수 없습니다. 타입: {_hwType}");
                _isHardwareEncodingEnabled = false;
                ConfigureSoftwareEncoder();
                return;
            }

            // 코덱 컨텍스트 설정
            _context = ffmpeg.avcodec_alloc_context3(_codec);
            _context->width = _width;
            _context->height = _height;
            _context->time_base = new AVRational { num = 1, den = 60 };
            _context->framerate = new AVRational { num = 60, den = 1 };
            _context->bit_rate = _bitrate;
            _context->rc_max_rate = _bitrate * 2;
            _context->rc_buffer_size = _bitrate;
            _context->gop_size = _defaultGopSize;
            _origGopSize = _defaultGopSize; // 원래 GOP 크기 저장
            _context->max_b_frames = 0;
            _context->pix_fmt = GetHardwarePixelFormat();
            _context->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
            _context->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;

            // 하드웨어 가속 설정
            if (_hwDeviceCtx != null)
            {
                try
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
                            EnhancedLogger.Instance.Error($"hw_frames_ctx 초기화 실패: {errorMsg}");
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
                        EnhancedLogger.Instance.Warning("하드웨어 프레임 제약조건을 가져올 수 없습니다.");
                        _isHardwareEncodingEnabled = false;
                        ConfigureSoftwareEncoder();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    EnhancedLogger.Instance.Error($"하드웨어 프레임 컨텍스트 설정 오류: {ex.Message}", ex);
                    _isHardwareEncodingEnabled = false;
                    ConfigureSoftwareEncoder();
                    return;
                }
            }
            else
            {
                EnhancedLogger.Instance.Warning("하드웨어 디바이스 컨텍스트가 NULL입니다.");
                _isHardwareEncodingEnabled = false;
                ConfigureSoftwareEncoder();
                return;
            }

            // 인코딩 옵션 설정 - 저지연 중심
            AVDictionary* opts = null;

            // NVIDIA NVENC 설정
            if (_hwType == AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA)
            {
                if (_useLowLatencySettings)
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
                else
                {
                    // 높은 품질 설정
                    ffmpeg.av_dict_set(&opts, "preset", "p4", 0);
                    ffmpeg.av_dict_set(&opts, "tune", "hq", 0);
                    ffmpeg.av_dict_set(&opts, "rc", "vbr", 0);
                }
            }
            else if (_hwType == AVHWDeviceType.AV_HWDEVICE_TYPE_QSV)
            {
                // Intel QuickSync 설정
                if (_useLowLatencySettings)
                {
                    ffmpeg.av_dict_set(&opts, "low_delay", "1", 0);
                    ffmpeg.av_dict_set(&opts, "look_ahead", "0", 0);       // 룩어헤드 비활성화
                }
            }
            else if (_hwType == AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI)
            {
                // VAAPI 설정
                ffmpeg.av_dict_set(&opts, "low_power", "1", 0);        // 저전력 모드
            }

            // 공통 설정
            ffmpeg.av_dict_set(&opts, "threads", "auto", 0);

            // 코덱 열기
            int result = ffmpeg.avcodec_open2(_context, _codec, &opts);
            if (result < 0)
            {
                string errorMsg = GetErrorMessage(result);
                EnhancedLogger.Instance.Error($"하드웨어 코덱 열기 실패: {errorMsg}");
                _isHardwareEncodingEnabled = false;
                ConfigureSoftwareEncoder();
            }
            else
            {
                EnhancedLogger.Instance.Info($"하드웨어 인코더 초기화 성공: {_hwType}");
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
            _context->gop_size = _defaultGopSize;
            _origGopSize = _defaultGopSize; // 원래 GOP 크기 저장
            _context->max_b_frames = 0;
            _context->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
            _context->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
            _context->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;

            // 품질 설정 (CRF 방식)
            int crf = Math.Max(0, Math.Min(51, 30 - (_quality * 25 / 100)));

            // 인코딩 옵션 설정 - 저지연 중심
            AVDictionary* opts = null;

            if (_useLowLatencySettings)
            {
                ffmpeg.av_dict_set(&opts, "preset", "ultrafast", 0); // 가장 빠른 프리셋
                ffmpeg.av_dict_set(&opts, "tune", "zerolatency", 0);
                ffmpeg.av_dict_set(&opts, "profile", "baseline", 0); // 가장 기본적인 프로파일

                // 지연 최소화를 위한 추가 설정
                ffmpeg.av_dict_set(&opts, "x264opts", "no-mbtree:sliced-threads:sync-lookahead=0", 0);
            }
            else
            {
                // 품질 중심 설정
                ffmpeg.av_dict_set(&opts, "preset", "fast", 0);
                ffmpeg.av_dict_set(&opts, "profile", "main", 0);
            }

            ffmpeg.av_dict_set(&opts, "crf", crf.ToString(), 0);
            ffmpeg.av_dict_set(&opts, "threads", "auto", 0);

            // 코덱 열기
            int result = ffmpeg.avcodec_open2(_context, _codec, &opts);
            if (result < 0)
            {
                string errorMsg = GetErrorMessage(result);
                throw new Exception($"코덱을 열 수 없습니다: {errorMsg}");
            }

            EnhancedLogger.Instance.Info("소프트웨어 인코더 초기화 성공");
        }

        private void InitializeFrameAndPacket()
        {
            // 프레임 생성 - 일반 작업용 및 하드웨어용(선택적)
            _frame = ffmpeg.av_frame_alloc();
            _frame->format = (int)(_isHardwareEncodingEnabled ? AVPixelFormat.AV_PIX_FMT_NV12 : _context->pix_fmt);
            _frame->width = _width;
            _frame->height = _height;

            // 프레임 버퍼 할당
            int result = ffmpeg.av_frame_get_buffer(_frame, 32);
            if (result < 0)
            {
                string errorMsg = GetErrorMessage(result);
                throw new Exception($"프레임 버퍼 할당 실패: {errorMsg}");
            }

            // 하드웨어 인코딩용 프레임 생성
            if (_isHardwareEncodingEnabled && _context->hw_frames_ctx != null)
            {
                _hwFrame = ffmpeg.av_frame_alloc();
                _hwFrame->format = (int)GetHardwarePixelFormat();
                _hwFrame->width = _width;
                _hwFrame->height = _height;
            }

            // 패킷 할당
            _packet = ffmpeg.av_packet_alloc();

            // BGRA를 필요한 픽셀 포맷으로 변환하기 위한 SWS 컨텍스트 생성
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

                    // 코덱 컨텍스트 비트레이트 설정 업데이트
                    _context->bit_rate = bitrate;
                    _context->rc_max_rate = bitrate * 2;
                    _context->rc_buffer_size = bitrate;

                    EnhancedLogger.Instance.Info($"비트레이트 변경: {bitrate / 1000} kbps");
                }
            }
        }

        /// <summary>
        /// 키프레임을 강제로 생성합니다. 개선된 버전으로 최소 간격 제한 적용.
        /// </summary>
        public void ForceKeyframe()
        {
            if (_isDisposed)
                return;

            lock (_encodeLock)
            {
                try
                {
                    // 키프레임 간격 제한 확인
                    if (_keyframeIntervalTimer.ElapsedMilliseconds < _minKeyframeInterval)
                    {
                        EnhancedLogger.Instance.Debug(
                            $"키프레임 요청 제한됨: 마지막 키프레임으로부터 " +
                            $"{_keyframeIntervalTimer.ElapsedMilliseconds}ms, " +
                            $"최소 간격 {_minKeyframeInterval}ms");
                        return;
                    }

                    // 이미 키프레임 생성 중인 경우 무시
                    if (_keyframeInProgress)
                    {
                        EnhancedLogger.Instance.Debug("이미 키프레임 생성 중");
                        return;
                    }

                    EnhancedLogger.Instance.Info("키프레임 강제 생성 요청");
                    _forceKeyframe = true;
                    _keyframeInProgress = true;

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
                    EnhancedLogger.Instance.Error($"키프레임 강제 생성 오류: {ex.Message}", ex);
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

                    // BGRA를 필요한 형식으로 변환 (YUV420P 또는 NV12)
                    ffmpeg.sws_scale(_swsContext, srcDataPtr, srcStrides, 0, _height, dstDataPtr, dstStrides);

                    // 타임스탬프 설정
                    _frame->pts = _frameCount++;

                    // 키프레임 강제 요청 처리
                    bool isKeyframe = false;
                    if (_forceKeyframe)
                    {
                        // pict_type을 I-frame으로 설정
                        _frame->pict_type = AVPictureType.AV_PICTURE_TYPE_I;
                        isKeyframe = true;
                        _keyframeCount++;
                        _forceKeyframe = false;
                        _keyframeInProgress = false;

                        // 타이머 초기화
                        _keyframeIntervalTimer.Restart();
                        _lastKeyframe = DateTime.UtcNow;

                        // GOP 크기를 원래대로 복원 (다음 프레임부터)
                        Task.Run(() =>
                        {
                            Thread.Sleep(100);  // 현재 프레임이 전송되도록 잠시 대기
                            lock (_encodeLock)
                            {
                                if (!_isDisposed)
                                {
                                    _context->gop_size = _origGopSize;
                                    EnhancedLogger.Instance.Debug($"GOP 크기 복원: {_origGopSize}");
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
                        double keyframeRatio = (_frameCount > 0) ? (double)_keyframeCount / _frameCount * 100 : 0;

                        EnhancedLogger.Instance.Info(
                            $"인코딩 성능: {sw.ElapsedMilliseconds}ms, " +
                            $"총 프레임: {_frameCount}, 키프레임: {_keyframeCount} ({keyframeRatio:F1}%), " +
                            $"평균: {averageEncodingTime:F2}ms/프레임");
                    }
                }
                catch (Exception ex)
                {
                    EnhancedLogger.Instance.Error($"인코딩 오류: {ex.Message}", ex);
                }
                finally
                {
                    bitmap.UnlockBits(bitmapData);
                }
            }
        }

        private void EncodeWithHardware()
        {
            try
            {
                // CPU에서 GPU로 데이터 전송
                int ret = ffmpeg.av_hwframe_get_buffer(_context->hw_frames_ctx, _hwFrame, 0);
                if (ret < 0)
                {
                    string errorMsg = GetErrorMessage(ret);
                    EnhancedLogger.Instance.Error($"하드웨어 프레임 버퍼 할당 실패: {errorMsg}");
                    EncodeWithSoftware(); // 소프트웨어 인코딩으로 폴백
                    return;
                }

                ret = ffmpeg.av_hwframe_transfer_data(_hwFrame, _frame, 0);
                if (ret < 0)
                {
                    string errorMsg = GetErrorMessage(ret);
                    EnhancedLogger.Instance.Error($"하드웨어 프레임 데이터 전송 실패: {errorMsg}");
                    EncodeWithSoftware(); // 소프트웨어 인코딩으로 폴백
                    return;
                }

                _hwFrame->pts = _frame->pts;
                _hwFrame->pict_type = _frame->pict_type;

                // 프레임을 인코더로 전송
                ret = ffmpeg.avcodec_send_frame(_context, _hwFrame);
                if (ret < 0)
                {
                    string errorMsg = GetErrorMessage(ret);
                    EnhancedLogger.Instance.Error($"하드웨어 프레임 인코딩 실패: {errorMsg}");
                    EncodeWithSoftware(); // 소프트웨어 인코딩으로 폴백
                    return;
                }

                // 인코딩된 패킷 수신
                ReceivePackets();
            }
            catch (Exception ex)
            {
                EnhancedLogger.Instance.Error($"하드웨어 인코딩 오류: {ex.Message}", ex);
                EncodeWithSoftware(); // 소프트웨어 인코딩으로 폴백
            }
        }

        private void EncodeWithSoftware()
        {
            try
            {
                // 프레임을 인코더로 전송
                int ret = ffmpeg.avcodec_send_frame(_context, _frame);
                if (ret < 0)
                {
                    string errorMsg = GetErrorMessage(ret);
                    EnhancedLogger.Instance.Error($"프레임 인코딩 실패: {errorMsg}");
                    return;
                }

                // 인코딩된 패킷 수신
                ReceivePackets();
            }
            catch (Exception ex)
            {
                EnhancedLogger.Instance.Error($"소프트웨어 인코딩 오류: {ex.Message}", ex);
            }
        }

        private void ReceivePackets()
        {
            try
            {
                while (true)
                {
                    int ret = ffmpeg.avcodec_receive_packet(_context, _packet);
                    if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                        break;

                    if (ret < 0)
                    {
                        string errorMsg = GetErrorMessage(ret);
                        EnhancedLogger.Instance.Error($"패킷 수신 실패: {errorMsg}");
                        break;
                    }

                    // 인코딩된 데이터 복사
                    byte[] encodedData = new byte[_packet->size];
                    Marshal.Copy((IntPtr)_packet->data, encodedData, 0, _packet->size);

                    // 키프레임 여부 확인
                    bool isKeyFrame = (_packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;

                    // 이벤트 인자 생성
                    var args = new FrameEncodedEventArgs
                    {
                        EncodedData = encodedData,
                        IsKeyFrame = isKeyFrame
                    };

                    // 이벤트 트리거
                    FrameEncoded?.Invoke(this, args);

                    if (isKeyFrame)
                    {
                        EnhancedLogger.Instance.Info($"키프레임 생성: size={encodedData.Length}");
                    }

                    ffmpeg.av_packet_unref(_packet);
                }
            }
            catch (Exception ex)
            {
                EnhancedLogger.Instance.Error($"패킷 수신 처리 오류: {ex.Message}", ex);
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
            double keyframeRatio = (_frameCount > 0) ? (double)_keyframeCount / _frameCount * 100 : 0;

            EnhancedLogger.Instance.Info(
                $"FFmpeg 인코더 종료. 하드웨어 가속: {(_isHardwareEncodingEnabled ? "활성화" : "비활성화")}, " +
                $"총 {_frameCount}개 프레임 인코딩 (키프레임: {_keyframeCount}, {keyframeRatio:F1}%), " +
                $"평균 처리 시간: {averageTime:F2}ms/프레임");

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