// ScreenShare.Client/Encoder/FFmpegEncoder.cs
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
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
        private readonly object _encodeLock = new object();
        private Stopwatch _performanceTimer = new Stopwatch();
        private long _frameCount = 0;
        private bool _isDisposed = false;

        public event EventHandler<byte[]> FrameEncoded;

        public FFmpegEncoder(int width, int height, int quality = 70)
        {
            _width = width;
            _height = height;
            _quality = quality;

            InitializeFFmpeg();
            _performanceTimer.Start();
        }

        private void InitializeFFmpeg()
        {
            try
            {
                Console.WriteLine("FFmpeg 초기화 시작");

                // H.264 코덱 선택
                _codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_H264);
                if (_codec == null)
                    throw new Exception("H.264 코덱을 찾을 수 없습니다.");

                // 코덱 컨텍스트 초기화
                _context = ffmpeg.avcodec_alloc_context3(_codec);
                _context->width = _width;
                _context->height = _height;
                _context->time_base = new AVRational { num = 1, den = 30 };
                _context->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
                _context->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
                _context->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;  // 낮은 지연 설정

                // 품질 설정 (CRF 방식, 낮을수록 품질 높음)
                int crf = 30 - (_quality * 25 / 100); // 70% -> CRF 약 12
                if (crf < 0) crf = 0;
                if (crf > 51) crf = 51;

                // 옵션 설정
                AVDictionary* opts = null;
                ffmpeg.av_dict_set(&opts, "preset", "ultrafast", 0);
                ffmpeg.av_dict_set(&opts, "tune", "zerolatency", 0);
                ffmpeg.av_dict_set(&opts, "crf", crf.ToString(), 0);
                ffmpeg.av_dict_set(&opts, "threads", "auto", 0);  // 멀티스레딩 활성화

                // 코덱 열기
                int result = ffmpeg.avcodec_open2(_context, _codec, &opts);
                if (result < 0)
                {
                    string errorMsg = GetErrorMessage(result);
                    throw new Exception($"코덱을 열 수 없습니다: {errorMsg}");
                }

                // 프레임 초기화
                _frame = ffmpeg.av_frame_alloc();
                _frame->format = (int)_context->pix_fmt;
                _frame->width = _width;
                _frame->height = _height;

                // 프레임 버퍼 할당
                result = ffmpeg.av_frame_get_buffer(_frame, 32);
                if (result < 0)
                {
                    string errorMsg = GetErrorMessage(result);
                    throw new Exception($"프레임 버퍼 할당 실패: {errorMsg}");
                }

                // 패킷 초기화
                _packet = ffmpeg.av_packet_alloc();

                // SWS 컨텍스트 초기화 (RGBA -> YUV420P 변환)
                _swsContext = ffmpeg.sws_getContext(
                    _width, _height, AVPixelFormat.AV_PIX_FMT_BGRA,
                    _width, _height, AVPixelFormat.AV_PIX_FMT_YUV420P,
                    ffmpeg.SWS_FAST_BILINEAR, null, null, null);

                if (_swsContext == null)
                    throw new Exception("SWS 컨텍스트 초기화 실패");

                Console.WriteLine("FFmpeg 초기화 완료");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FFmpeg 초기화 오류: {ex.Message}");
                Dispose();
                throw;
            }
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

                    // BGRA -> YUV420P 변환
                    ffmpeg.sws_scale(_swsContext, srcDataPtr, srcStrides, 0, _height, dstDataPtr, dstStrides);

                    // 프레임 인코딩
                    _frame->pts = _frameCount++;

                    int ret = ffmpeg.avcodec_send_frame(_context, _frame);
                    if (ret < 0)
                    {
                        string errorMsg = GetErrorMessage(ret);
                        Console.WriteLine($"프레임 인코딩 실패: {errorMsg}");
                        return;
                    }

                    while (ret >= 0)
                    {
                        ret = ffmpeg.avcodec_receive_packet(_context, _packet);
                        if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                            break;

                        if (ret < 0)
                        {
                            string errorMsg = GetErrorMessage(ret);
                            Console.WriteLine($"패킷 수신 실패: {errorMsg}");
                            break;
                        }

                        // 인코딩된 데이터 복사
                        byte[] encodedData = new byte[_packet->size];
                        Marshal.Copy((IntPtr)_packet->data, encodedData, 0, _packet->size);

                        // 이벤트 발생
                        FrameEncoded?.Invoke(this, encodedData);

                        ffmpeg.av_packet_unref(_packet);
                    }

                    sw.Stop();
                    if (_frameCount % 30 == 0)
                    {
                        Console.WriteLine($"인코딩 시간: {sw.ElapsedMilliseconds}ms, 총 프레임: {_frameCount}, 평균: {_performanceTimer.ElapsedMilliseconds / (double)_frameCount:F2}ms/프레임");
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

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            _performanceTimer.Stop();

            Console.WriteLine($"FFmpeg 인코더 종료. 총 {_frameCount}개 프레임 인코딩, 평균 처리 시간: {(_frameCount > 0 ? _performanceTimer.ElapsedMilliseconds / (double)_frameCount : 0):F2}ms/프레임");

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

        ~FFmpegEncoder()
        {
            Dispose();
        }
    }
}