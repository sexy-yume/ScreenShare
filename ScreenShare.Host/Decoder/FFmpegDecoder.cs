using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using System.Threading;
using FFmpeg.AutoGen;

namespace ScreenShare.Host.Decoder
{
    public unsafe class FFmpegDecoder : IDisposable
    {
        // Core FFmpeg components
        private AVCodec* _codec;
        private AVCodecContext* _context;
        private AVFrame* _frame;
        private AVPacket* _packet;
        private SwsContext* _swsContext;

        // Dimensions and state tracking
        private int _width;
        private int _height;
        private bool _isInitialized;
        private readonly object _decodeLock = new object();
        private bool _isDisposed = false;

        // Performance tracking
        private readonly Stopwatch _performanceTimer = new Stopwatch();
        private long _frameCount = 0;
        private long _totalDecodeTime = 0;
        private long _totalSwscaleTime = 0;
        private long _totalBitmapTime = 0;
        private readonly TimeSpan _logInterval = TimeSpan.FromSeconds(10);
        private DateTime _lastLogTime = DateTime.MinValue;

        // Decoder options
        private bool _useHardwareAcceleration = true;
        private int _threadCount = 2;
        private bool _enableVerboseLogging = false;

        // Events
        public event EventHandler<Bitmap> FrameDecoded;
        public event EventHandler<DecoderPerformanceMetrics> PerformanceUpdated;

        public class DecoderPerformanceMetrics
        {
            public double AverageDecodeTimeMs { get; set; }
            public double AverageSwscaleTimeMs { get; set; }
            public double AverageBitmapTimeMs { get; set; }
            public double TotalProcessingTimeMs { get; set; }
            public long TotalFramesDecoded { get; set; }
            public double DecodeFps { get; set; }
        }

        public FFmpegDecoder(bool useHardwareAcceleration = true, int threadCount = 2)
        {
            _useHardwareAcceleration = useHardwareAcceleration;
            _threadCount = threadCount;

            InitializeFFmpeg();
            _performanceTimer.Start();
        }

        public void SetVerboseLogging(bool enabled)
        {
            _enableVerboseLogging = enabled;
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

        private void InitializeFFmpeg()
        {
            try
            {
                Console.WriteLine("Initializing FFmpeg decoder");

                // Find H.264 codec
                _codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
                if (_codec == null)
                    throw new Exception("H.264 codec not found");

                // Initialize codec context
                _context = ffmpeg.avcodec_alloc_context3(_codec);

                // Set low-latency flags
                _context->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
                _context->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;

                // Set threading model
                _context->thread_count = _threadCount;
                _context->thread_type = ffmpeg.FF_THREAD_SLICE;  // Use slice threading for lower latency

                // Reduce error sensitivity
                _context->err_recognition = ffmpeg.AV_EF_EXPLODE; // Only fail on critical errors

                // Open codec with appropriate options
                AVDictionary* opts = null;
                ffmpeg.av_dict_set(&opts, "threads", _threadCount.ToString(), 0);
                ffmpeg.av_dict_set(&opts, "refcounted_frames", "1", 0);

                // Fast decoding settings
                ffmpeg.av_dict_set(&opts, "flags", "low_delay", 0);
                ffmpeg.av_dict_set(&opts, "flags2", "fast", 0);

                // Latency-focused settings
                ffmpeg.av_dict_set(&opts, "strict", "experimental", 0);

                int result = ffmpeg.avcodec_open2(_context, _codec, &opts);
                if (result < 0)
                {
                    string errorMsg = GetErrorMessage(result);
                    throw new Exception($"Failed to open codec: {errorMsg}");
                }

                // Allocate frame
                _frame = ffmpeg.av_frame_alloc();

                // Allocate packet
                _packet = ffmpeg.av_packet_alloc();

                Console.WriteLine("FFmpeg decoder initialized successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FFmpeg decoder initialization error: {ex.Message}");
                Dispose();
                throw;
            }
        }

        public Bitmap DecodeFrame(byte[] data, int width, int height, long frameId)
        {
            if (_isDisposed || data == null || data.Length == 0)
                return null;

            Bitmap result = null;

            Stopwatch sw = Stopwatch.StartNew();
            Stopwatch sectionTimer = new Stopwatch();
            long decodeTime = 0, swscaleTime = 0, bitmapTime = 0;

            lock (_decodeLock)
            {
                try
                {
                    LogVerbose($"Starting decode: size={data.Length}, dims={width}x{height}");

                    // Update dimensions if needed
                    if (_width != width || _height != height || !_isInitialized)
                    {
                        LogVerbose($"Resizing decoder to {width}x{height}");
                        _width = width;
                        _height = height;

                        // Free existing scaler if any
                        if (_swsContext != null)
                        {
                            ffmpeg.sws_freeContext(_swsContext);
                        }

                        // Create new scaler context - use fast bilinear for low latency
                        _swsContext = ffmpeg.sws_getContext(
                            width, height, AVPixelFormat.AV_PIX_FMT_YUV420P,
                            width, height, AVPixelFormat.AV_PIX_FMT_BGRA,
                            ffmpeg.SWS_FAST_BILINEAR, null, null, null);

                        if (_swsContext == null)
                            throw new Exception("Failed to initialize SWS context");

                        _isInitialized = true;
                    }

                    // Set packet data
                    sectionTimer.Start();
                    fixed (byte* ptr = data)
                    {
                        _packet->data = ptr;
                        _packet->size = data.Length;
                        _packet->pts = frameId;
                        _packet->dts = frameId;

                        // Send packet to decoder
                        int ret = ffmpeg.avcodec_send_packet(_context, _packet);
                        if (ret < 0)
                        {
                            string errorMsg = GetErrorMessage(ret);
                            LogVerbose($"Packet decode failed: {errorMsg}, code: {ret}");
                            return null;
                        }

                        // Receive decoded frame
                        ret = ffmpeg.avcodec_receive_frame(_context, _frame);
                        if (ret < 0)
                        {
                            if (ret != ffmpeg.AVERROR(ffmpeg.EAGAIN) && ret != ffmpeg.AVERROR_EOF)
                            {
                                string errorMsg = GetErrorMessage(ret);
                                LogVerbose($"Frame receive failed: {errorMsg}, code: {ret}");
                            }
                            return null;
                        }
                    }
                    sectionTimer.Stop();
                    decodeTime = sectionTimer.ElapsedTicks;
                    LogVerbose($"Frame decoded: format={_frame->format}, dims={_frame->width}x{_frame->height}");

                    // Create bitmap
                    sectionTimer.Restart();
                    result = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    var bitmapData = result.LockBits(
                        new Rectangle(0, 0, width, height),
                        System.Drawing.Imaging.ImageLockMode.WriteOnly,
                        System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                    // Pointers for YUV -> RGB conversion
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
                    sectionTimer.Stop();
                    bitmapTime = sectionTimer.ElapsedTicks;

                    // Scale image
                    sectionTimer.Restart();
                    ffmpeg.sws_scale(_swsContext, srcDataPtr, srcStrides, 0, height, dstDataPtr, dstStrides);
                    sectionTimer.Stop();
                    swscaleTime = sectionTimer.ElapsedTicks;

                    result.UnlockBits(bitmapData);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Decode error: {ex.Message}\n{ex.StackTrace}");
                    if (result != null)
                    {
                        result.Dispose();
                        result = null;
                    }
                }
                finally
                {
                    sw.Stop();

                    // Convert from ticks to ms
                    double ticksToMs = 1000.0 / Stopwatch.Frequency;
                    double decodeMs = decodeTime * ticksToMs;
                    double swscaleMs = swscaleTime * ticksToMs;
                    double bitmapMs = bitmapTime * ticksToMs;
                    double totalMs = sw.ElapsedTicks * ticksToMs;

                    // Update performance stats
                    _frameCount++;
                    _totalDecodeTime += decodeTime;
                    _totalSwscaleTime += swscaleTime;
                    _totalBitmapTime += bitmapTime;

                    // Log performance periodically
                    DateTime now = DateTime.Now;
                    if ((now - _lastLogTime) > _logInterval || _enableVerboseLogging)
                    {
                        double totalSeconds = _performanceTimer.ElapsedMilliseconds / 1000.0;
                        double fps = _frameCount / totalSeconds;

                        var metrics = new DecoderPerformanceMetrics
                        {
                            AverageDecodeTimeMs = (_totalDecodeTime * ticksToMs) / _frameCount,
                            AverageSwscaleTimeMs = (_totalSwscaleTime * ticksToMs) / _frameCount,
                            AverageBitmapTimeMs = (_totalBitmapTime * ticksToMs) / _frameCount,
                            TotalProcessingTimeMs = totalMs,
                            TotalFramesDecoded = _frameCount,
                            DecodeFps = fps
                        };

                        Console.WriteLine(
                            $"Decoder stats: Frames={_frameCount}, FPS={fps:F1}, " +
                            $"Decode={metrics.AverageDecodeTimeMs:F2}ms, " +
                            $"Scale={metrics.AverageSwscaleTimeMs:F2}ms, " +
                            $"Bitmap={metrics.AverageBitmapTimeMs:F2}ms, " +
                            $"Total={totalMs:F2}ms");

                        PerformanceUpdated?.Invoke(this, metrics);

                        _lastLogTime = now;
                    }
                    else if (_enableVerboseLogging)
                    {
                        LogVerbose($"Frame {frameId} timing: Decode={decodeMs:F2}ms, Scale={swscaleMs:F2}ms, Bitmap={bitmapMs:F2}ms, Total={totalMs:F2}ms");
                    }
                }
            }

            return result;
        }

        private void LogVerbose(string message)
        {
            if (_enableVerboseLogging)
            {
                Console.WriteLine($"[DECODER] {message}");
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            _performanceTimer.Stop();

            double totalSeconds = _performanceTimer.ElapsedMilliseconds / 1000.0;
            double fps = totalSeconds > 0 ? _frameCount / totalSeconds : 0;
            double decodeAvg = _frameCount > 0 ? (_totalDecodeTime * 1000.0 / Stopwatch.Frequency) / _frameCount : 0;

            Console.WriteLine(
                $"FFmpeg decoder disposed. Total frames: {_frameCount}, Avg FPS: {fps:F1}, " +
                $"Avg decode time: {decodeAvg:F2}ms");

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