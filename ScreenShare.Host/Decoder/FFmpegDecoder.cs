// ScreenShare.Host/Decoder/FFmpegDecoder.cs
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using FFmpeg.AutoGen;

namespace ScreenShare.Host.Decoder
{
    public unsafe class FFmpegDecoder : IDisposable
    {
        private AVCodec* _codec;
        private AVCodecContext* _context;
        private AVFrame* _frame;
        private AVPacket* _packet;
        private SwsContext* _swsContext;

        private int _width;
        private int _height;
        private bool _isInitialized;
        private readonly object _decodeLock = new object();
        private Stopwatch _performanceTimer = new Stopwatch();
        private long _frameCount = 0;
        private bool _isDisposed = false;

        public event EventHandler<Bitmap> FrameDecoded;

        public FFmpegDecoder()
        {
            InitializeFFmpeg();
            _performanceTimer.Start();
        }

        private string GetErrorMessage(int errorCode)
        {
            // FFmpeg 오류 메시지 가져오기
            const int bufferSize = 1024;
            var buffer = new byte[bufferSize];

            fixed (byte* bufferPtr = buffer)
            {
                ffmpeg.av_strerror(errorCode, bufferPtr, (ulong)bufferSize);
            }

            return Encoding.UTF8.GetString(buffer).TrimEnd('\0');
        }

        private void InitializeFFmpeg()
        {
            try
            {
                Console.WriteLine("FFmpeg 디코더 초기화 시작");

                // H.264 코덱 선택
                _codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
                if (_codec == null)
                    throw new Exception("H.264 코덱을 찾을 수 없습니다.");

                // 코덱 컨텍스트 초기화
                _context = ffmpeg.avcodec_alloc_context3(_codec);
                _context->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;  // 낮은 지연 설정
                _context->thread_count = 4;  // 멀티스레딩 설정

                // 코덱 열기
                AVDictionary* opts = null;
                ffmpeg.av_dict_set(&opts, "threads", "auto", 0);  // 멀티스레딩 활성화

                int result = ffmpeg.avcodec_open2(_context, _codec, &opts);
                if (result < 0)
                {
                    string errorMsg = GetErrorMessage(result);
                    throw new Exception($"코덱을 열 수 없습니다: {errorMsg}");
                }

                // 프레임 초기화
                _frame = ffmpeg.av_frame_alloc();

                // 패킷 초기화
                _packet = ffmpeg.av_packet_alloc();

                Console.WriteLine("FFmpeg 디코더 초기화 완료");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FFmpeg 디코더 초기화 오류: {ex.Message}");
                Dispose();
                throw;
            }
        }

        // FFmpegDecoder.cs의 DecodeFrame 메서드 수정
        public void DecodeFrame(byte[] data, int width, int height)
        {
            if (_isDisposed || data == null || data.Length == 0)
                return;

            lock (_decodeLock)
            {
                Stopwatch sw = Stopwatch.StartNew();
                Console.WriteLine($"디코딩 시작: 데이터 크기={data.Length}, 목표 해상도={width}x{height}");

                try
                {
                    // 크기가 변경되었거나 첫 프레임인 경우 초기화
                    if (_width != width || _height != height || !_isInitialized)
                    {
                        Console.WriteLine($"디코더 크기 변경: {width}x{height}");
                        _width = width;
                        _height = height;

                        // SWS 컨텍스트 초기화 (YUV420P -> RGBA 변환)
                        if (_swsContext != null)
                        {
                            ffmpeg.sws_freeContext(_swsContext);
                        }

                        _swsContext = ffmpeg.sws_getContext(
                            width, height, AVPixelFormat.AV_PIX_FMT_YUV420P,
                            width, height, AVPixelFormat.AV_PIX_FMT_BGRA,
                            ffmpeg.SWS_FAST_BILINEAR, null, null, null);

                        if (_swsContext == null)
                            throw new Exception("SWS 컨텍스트 초기화 실패");

                        _isInitialized = true;
                    }

                    // 패킷 데이터 설정
                    fixed (byte* ptr = data)
                    {
                        _packet->data = ptr;
                        _packet->size = data.Length;

                        // 패킷 디코딩
                        int ret = ffmpeg.avcodec_send_packet(_context, _packet);
                        if (ret < 0)
                        {
                            string errorMsg = GetErrorMessage(ret);
                            Console.WriteLine($"패킷 디코딩 실패: {errorMsg}, 코드: {ret}");
                            return;
                        }

                        Console.WriteLine("패킷 전송 성공, 프레임 수신 대기");
                        bool frameReceived = false;

                        while (ret >= 0)
                        {
                            ret = ffmpeg.avcodec_receive_frame(_context, _frame);
                            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                            {
                                Console.WriteLine($"더 이상 프레임 없음: {(ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) ? "EAGAIN" : "EOF")}");
                                break;
                            }

                            if (ret < 0)
                            {
                                string errorMsg = GetErrorMessage(ret);
                                Console.WriteLine($"프레임 수신 실패: {errorMsg}, 코드: {ret}");
                                break;
                            }

                            Console.WriteLine($"프레임 수신 성공: 포맷={_frame->format}, 크기={_frame->width}x{_frame->height}");
                            frameReceived = true;

                            // 디코딩된 프레임 정보 출력
                            Console.WriteLine($"YUV 데이터: Y_linesize={_frame->linesize[0]}, U_linesize={_frame->linesize[1]}, V_linesize={_frame->linesize[2]}");

                            // 비트맵 생성
                            var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                            var bitmapData = bitmap.LockBits(
                                new Rectangle(0, 0, width, height),
                                System.Drawing.Imaging.ImageLockMode.WriteOnly,
                                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                            // YUV420P -> BGRA 변환
                            byte_ptrArray4 srcDataPtr = new byte_ptrArray4();
                            srcDataPtr[0] = _frame->data[0];
                            srcDataPtr[1] = _frame->data[1];
                            srcDataPtr[2] = _frame->data[2];
                            int_array4 srcStrides = new int_array4();
                            srcStrides[0] = _frame->linesize[0];
                            srcStrides[1] = _frame->linesize[1];
                            srcStrides[2] = _frame->linesize[2];

                            byte_ptrArray4 dstDataPtr = new byte_ptrArray4();
                            dstDataPtr[0] = (byte*)bitmapData.Scan0;
                            int_array4 dstStrides = new int_array4();
                            dstStrides[0] = bitmapData.Stride;

                            int scaleResult = ffmpeg.sws_scale(_swsContext, srcDataPtr, srcStrides, 0, height, dstDataPtr, dstStrides);
                            Console.WriteLine($"변환된 라인 수: {scaleResult}");

                            bitmap.UnlockBits(bitmapData);

                            // 디버깅용 - 시간 표시
                            using (Graphics g = Graphics.FromImage(bitmap))
                            {
                                g.DrawString(DateTime.Now.ToString("HH:mm:ss.fff"),
                                    new Font("Arial", 12), Brushes.Yellow, 10, 10);
                            }

                            _frameCount++;
                            Console.WriteLine($"비트맵 생성 완료: 해상도={bitmap.Width}x{bitmap.Height}, 포맷={bitmap.PixelFormat}");

                            // 이벤트 발생
                            FrameDecoded?.Invoke(this, bitmap);
                        }

                        if (!frameReceived)
                        {
                            Console.WriteLine("유효한 프레임이 디코딩되지 않았습니다. 다음 패킷을 기다립니다.");
                        }
                    }

                    sw.Stop();
                    if (_frameCount % 30 == 0)
                    {
                        Console.WriteLine($"디코딩 시간: {sw.ElapsedMilliseconds}ms, 총 프레임: {_frameCount}, 평균: {_performanceTimer.ElapsedMilliseconds / (double)_frameCount:F2}ms/프레임");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"디코딩 오류: {ex.Message}\n{ex.StackTrace}");
                }
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            _performanceTimer.Stop();

            Console.WriteLine($"FFmpeg 디코더 종료. 총 {_frameCount}개 프레임 디코딩, 평균 처리 시간: {(_frameCount > 0 ? _performanceTimer.ElapsedMilliseconds / (double)_frameCount : 0):F2}ms/프레임");

            lock (_decodeLock)
            {
                if (_packet != null)
                {
                    fixed (AVPacket** packet = &_packet)
                    {
                        ffmpeg.av_packet_free(packet);
                    }
                    _packet = null;
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

                if (_swsContext != null)
                {
                    ffmpeg.sws_freeContext(_swsContext);
                    _swsContext = null;
                }
            }

            GC.SuppressFinalize(this);
        }

        ~FFmpegDecoder()
        {
            Dispose();
        }
    }
}