using System;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.DXGI;
using SharpDX.Direct3D;
using D3D11 = SharpDX.Direct3D11;
using SharpDX.Mathematics.Interop;
using SDColor = System.Drawing.Color;
using SDRectangle = System.Drawing.Rectangle;
using SDBitmap = System.Drawing.Bitmap;
using SharpDXColor = SharpDX.Color;
using SharpDXRectangle = SharpDX.Rectangle;
using SharpDXMatrix = SharpDX.Matrix;
using SharpDXVector2 = SharpDX.Vector2;
using SharpDXVector4 = SharpDX.Vector4;

namespace ScreenShare.Host.Rendering
{
    // 버텍스 구조체 정의 - 메모리 레이아웃 명시
    [StructLayout(LayoutKind.Sequential)]
    public struct VertexPositionTexture
    {
        public SharpDXVector4 Position;
        public SharpDXVector2 TexCoord;

        public VertexPositionTexture(SharpDXVector4 position, SharpDXVector2 texCoord)
        {
            Position = position;
            TexCoord = texCoord;
        }
    }

    public class DirectXRenderer : IDisposable
    {
        // DirectX 리소스
        private D3D11.Device _device;
        private D3D11.DeviceContext _deviceContext;
        private SwapChain _swapChain;
        private D3D11.Texture2D _renderTarget;
        private D3D11.RenderTargetView _renderTargetView;
        private D3D11.ShaderResourceView _textureView;
        private D3D11.Texture2D _stagingTexture;
        private D3D11.SamplerState _samplerState;

        // Shader 리소스
        private D3D11.VertexShader _vertexShader;
        private D3D11.PixelShader _pixelShader;
        private D3D11.InputLayout _inputLayout;
        private D3D11.Buffer _vertexBuffer;
        private D3D11.Buffer _constantBuffer;

        // 렌더링 상태
        private int _width;
        private int _height;
        private Control _renderTarget2D;
        private bool _isInitialized;
        private bool _isDisposed;
        private readonly object _renderLock = new object();

        // 렌더러 구성 옵션
        private bool _stretchToFit = true;
        private SDColor _backgroundColor = SDColor.Black;
        private bool _vsyncEnabled = false;

        // 성능 추적
        private System.Diagnostics.Stopwatch _frameTimer = new System.Diagnostics.Stopwatch();
        private long _frameCount = 0;

        /// <summary>
        /// DirectX 렌더러 초기화
        /// </summary>
        /// <param name="renderTarget">렌더링할 Windows Forms 컨트롤</param>
        /// <param name="width">초기 너비</param>
        /// <param name="height">초기 높이</param>
        public DirectXRenderer(Control renderTarget, int width, int height)
        {
            _renderTarget2D = renderTarget;
            _width = width;
            _height = height;

            try
            {
                InitializeDirectX();
                InitializeShaders();
                InitializeBuffers();

                _isInitialized = true;
                _frameTimer.Start();

                // 컨트롤 이벤트 구독
                _renderTarget2D.Resize += OnRenderTargetResize;
                _renderTarget2D.HandleDestroyed += OnRenderTargetDestroyed;
                Console.WriteLine("DirectX 렌더러 초기화 완료");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"DirectX 렌더러 초기화 오류: {ex.Message}");
                Dispose();
                throw;
            }
        }

        /// <summary>
        /// DirectX 리소스 초기화
        /// </summary>
        private void InitializeDirectX()
        {
            // 스왑 체인 설명 생성
            var swapChainDesc = new SwapChainDescription
            {
                BufferCount = 2,
                Usage = Usage.RenderTargetOutput,
                OutputHandle = _renderTarget2D.Handle,
                IsWindowed = true,
                ModeDescription = new ModeDescription(
                    _renderTarget2D.ClientSize.Width,
                    _renderTarget2D.ClientSize.Height,
                    new Rational(60, 1), Format.R8G8B8A8_UNorm),
                SampleDescription = new SampleDescription(1, 0),
                Flags = SwapChainFlags.None,
                SwapEffect = SwapEffect.Discard
            };

            // 디바이스와 스왑 체인 생성
            D3D11.Device.CreateWithSwapChain(
                DriverType.Hardware,
                D3D11.DeviceCreationFlags.BgraSupport,
                new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_9_3 },
                swapChainDesc,
                out _device,
                out _swapChain);

            // Get immediate context
            _deviceContext = _device.ImmediateContext;

            // 렌더 타겟 설정
            using (var backBuffer = _swapChain.GetBackBuffer<D3D11.Texture2D>(0))
            {
                _renderTargetView = new D3D11.RenderTargetView(_device, backBuffer);
            }

            // 뷰포트 설정
            var viewport = new Viewport
            {
                X = 0,
                Y = 0,
                Width = _renderTarget2D.ClientSize.Width,
                Height = _renderTarget2D.ClientSize.Height,
                MinDepth = 0.0f,
                MaxDepth = 1.0f
            };
            _deviceContext.Rasterizer.SetViewport(viewport);

            // Alt+Enter 전체 화면 비활성화
            using (var factory = _swapChain.GetParent<Factory>())
            {
                factory.MakeWindowAssociation(_renderTarget2D.Handle, WindowAssociationFlags.IgnoreAltEnter);
            }

            // 샘플러 상태 생성
            var samplerDesc = new D3D11.SamplerStateDescription
            {
                Filter = D3D11.Filter.MinMagMipLinear,
                AddressU = D3D11.TextureAddressMode.Clamp,
                AddressV = D3D11.TextureAddressMode.Clamp,
                AddressW = D3D11.TextureAddressMode.Clamp,
                MipLodBias = 0,
                MaximumAnisotropy = 1,
                ComparisonFunction = D3D11.Comparison.Never,
                BorderColor = new RawColor4(0, 0, 0, 0),
                MinimumLod = 0,
                MaximumLod = float.MaxValue
            };

            _samplerState = new D3D11.SamplerState(_device, samplerDesc);
        }

        /// <summary>
        /// 셰이더 초기화
        /// </summary>
        private void InitializeShaders()
        {
            // 간단한 버텍스 셰이더 (HLSL)
            string vertexShaderCode = @"
                cbuffer ConstantBuffer : register(b0)
                {
                    matrix World;
                    matrix View;
                    matrix Projection;
                }

                struct VS_INPUT
                {
                    float4 Pos : POSITION;
                    float2 Tex : TEXCOORD0;
                };

                struct PS_INPUT
                {
                    float4 Pos : SV_POSITION;
                    float2 Tex : TEXCOORD0;
                };

                PS_INPUT VS(VS_INPUT input)
                {
                    PS_INPUT output = (PS_INPUT)0;
                    output.Pos = mul(input.Pos, World);
                    output.Pos = mul(output.Pos, View);
                    output.Pos = mul(output.Pos, Projection);
                    output.Tex = input.Tex;
                    return output;
                }";

            // 간단한 픽셀 셰이더 (HLSL)
            string pixelShaderCode = @"
                Texture2D shaderTexture;
                SamplerState samplerState;

                struct PS_INPUT
                {
                    float4 Pos : SV_POSITION;
                    float2 Tex : TEXCOORD0;
                };

                float4 PS(PS_INPUT input) : SV_Target
                {
                    return shaderTexture.Sample(samplerState, input.Tex);
                }";

            // HLSL 셰이더 코드를 바이트코드로 컴파일
#if DEBUG
            var shaderFlags = ShaderFlags.Debug;
#else
            var shaderFlags = ShaderFlags.OptimizationLevel3;
#endif

            // 버텍스 쉐이더
            using (var vsBlob = ShaderBytecode.Compile(vertexShaderCode, "VS", "vs_4_0", shaderFlags))
            {
                _vertexShader = new D3D11.VertexShader(_device, vsBlob.Bytecode);

                // 입력 레이아웃 생성
                var elements = new[]
                {
                    new D3D11.InputElement("POSITION", 0, Format.R32G32B32A32_Float, 0, 0, D3D11.InputClassification.PerVertexData, 0),
                    new D3D11.InputElement("TEXCOORD", 0, Format.R32G32_Float, 16, 0, D3D11.InputClassification.PerVertexData, 0)
                };

                _inputLayout = new D3D11.InputLayout(_device, vsBlob.Bytecode, elements);
            }

            // 픽셀 쉐이더
            using (var psBlob = ShaderBytecode.Compile(pixelShaderCode, "PS", "ps_4_0", shaderFlags))
            {
                _pixelShader = new D3D11.PixelShader(_device, psBlob.Bytecode);
            }
        }

        /// <summary>
        /// 버텍스 및 상수 버퍼 초기화
        /// </summary>
        private void InitializeBuffers()
        {
            // 명시적 구조체를 사용한 사각형 메쉬 정의
            var vertices = new[]
            {
                // 3D position (X, Y, Z, W), 2D texture coordinates (U, V)
                new VertexPositionTexture(new SharpDXVector4(-1.0f, -1.0f, 0.0f, 1.0f), new SharpDXVector2(0.0f, 1.0f)),  // Bottom Left
                new VertexPositionTexture(new SharpDXVector4(-1.0f,  1.0f, 0.0f, 1.0f), new SharpDXVector2(0.0f, 0.0f)),  // Top Left
                new VertexPositionTexture(new SharpDXVector4( 1.0f, -1.0f, 0.0f, 1.0f), new SharpDXVector2(1.0f, 1.0f)),  // Bottom Right
                new VertexPositionTexture(new SharpDXVector4( 1.0f, -1.0f, 0.0f, 1.0f), new SharpDXVector2(1.0f, 1.0f)),  // Bottom Right
                new VertexPositionTexture(new SharpDXVector4(-1.0f,  1.0f, 0.0f, 1.0f), new SharpDXVector2(0.0f, 0.0f)),  // Top Left
                new VertexPositionTexture(new SharpDXVector4( 1.0f,  1.0f, 0.0f, 1.0f), new SharpDXVector2(1.0f, 0.0f))   // Top Right
            };

            // 버텍스 버퍼 생성
            var vertexBufferDesc = new D3D11.BufferDescription
            {
                Usage = D3D11.ResourceUsage.Default,
                SizeInBytes = Utilities.SizeOf<VertexPositionTexture>() * vertices.Length,
                BindFlags = D3D11.BindFlags.VertexBuffer,
                CpuAccessFlags = D3D11.CpuAccessFlags.None,
                OptionFlags = D3D11.ResourceOptionFlags.None,
                StructureByteStride = 0
            };

            using (var vertexData = new DataStream(vertexBufferDesc.SizeInBytes, true, true))
            {
                foreach (var vertex in vertices)
                {
                    vertexData.Write(vertex);
                }
                vertexData.Position = 0;

                _vertexBuffer = new D3D11.Buffer(_device, vertexData, vertexBufferDesc);
            }

            // 상수 버퍼 생성 (변환 행렬용)
            var constantBufferDesc = new D3D11.BufferDescription
            {
                Usage = D3D11.ResourceUsage.Default,
                SizeInBytes = Utilities.SizeOf<SharpDXMatrix>() * 3, // World, View, Projection
                BindFlags = D3D11.BindFlags.ConstantBuffer,
                CpuAccessFlags = D3D11.CpuAccessFlags.None
            };

            _constantBuffer = new D3D11.Buffer(_device, constantBufferDesc);

            // 변환 행렬 설정
            SharpDXMatrix worldMatrix = SharpDXMatrix.Identity;
            SharpDXMatrix viewMatrix = SharpDXMatrix.Identity;
            SharpDXMatrix projectionMatrix = SharpDXMatrix.Identity;

            // 상수 버퍼 업데이트
            _deviceContext.UpdateSubresource(ref worldMatrix, _constantBuffer, 0);
            _deviceContext.UpdateSubresource(ref viewMatrix, _constantBuffer, Utilities.SizeOf<SharpDXMatrix>());
            _deviceContext.UpdateSubresource(ref projectionMatrix, _constantBuffer, Utilities.SizeOf<SharpDXMatrix>() * 2);
        }

        /// <summary>
        /// 디코딩된 비디오 프레임 렌더링
        /// </summary>
        /// <param name="bitmap">렌더링할 비트맵</param>
        public void RenderFrame(SDBitmap bitmap)
        {
            if (_isDisposed || !_isInitialized)
                return;

            try
            {
                Console.WriteLine($"렌더 프레임 시작: bitmap={bitmap != null}");

                // 테스트용 - 비트맵이 없으면 빨간색 테스트 화면 표시
                if (bitmap == null)
                {
                    // 화면을 빨간색으로 채우기
                    _deviceContext.ClearRenderTargetView(_renderTargetView, new RawColor4(1.0f, 0.0f, 0.0f, 1.0f));
                    _swapChain.Present(0, PresentFlags.None);
                    return;
                }

                lock (_renderLock)
                {
                    try
                    {
                        // 비트맵 크기 확인 및 텍스처 크기 조정
                        if (_width != bitmap.Width || _height != bitmap.Height)
                        {
                            Console.WriteLine($"텍스처 크기 조정: {_width}x{_height} -> {bitmap.Width}x{bitmap.Height}");
                            ResizeTexture(bitmap.Width, bitmap.Height);
                        }

                        // 비트맵을 텍스처로 업로드
                        UpdateTexture(bitmap);

                        // 화면 지우기
                        RawColor4 backgroundColor = new RawColor4(
                            _backgroundColor.R / 255.0f,
                            _backgroundColor.G / 255.0f,
                            _backgroundColor.B / 255.0f,
                            _backgroundColor.A / 255.0f);
                        _deviceContext.ClearRenderTargetView(_renderTargetView, backgroundColor);
                        Console.WriteLine($"화면 지우기 완료: {backgroundColor.R}, {backgroundColor.G}, {backgroundColor.B}");

                        // 렌더 타겟 설정
                        _deviceContext.OutputMerger.SetRenderTargets(_renderTargetView);

                        // 설정 확인
                        Console.WriteLine($"렌더 타겟 설정: {_renderTargetView != null}");
                        Console.WriteLine($"텍스처 뷰 설정: {_textureView != null}");
                        Console.WriteLine($"버텍스 버퍼 설정: {_vertexBuffer != null}");

                        // 셰이더 및 입력 레이아웃 설정
                        _deviceContext.InputAssembler.InputLayout = _inputLayout;
                        _deviceContext.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
                        _deviceContext.InputAssembler.SetVertexBuffers(0, new D3D11.VertexBufferBinding(_vertexBuffer, Utilities.SizeOf<VertexPositionTexture>(), 0));
                        _deviceContext.VertexShader.SetConstantBuffer(0, _constantBuffer);
                        _deviceContext.VertexShader.Set(_vertexShader);
                        _deviceContext.PixelShader.Set(_pixelShader);

                        // 텍스처 및 샘플러 설정
                        _deviceContext.PixelShader.SetShaderResource(0, _textureView);
                        _deviceContext.PixelShader.SetSampler(0, _samplerState);

                        // 렌더링
                        _deviceContext.Draw(6, 0);
                        Console.WriteLine("Draw 호출 완료");

                        // 스왑 체인 프레젠트
                        _swapChain.Present(_vsyncEnabled ? 1 : 0, PresentFlags.None);
                        Console.WriteLine("Present 호출 완료");

                        // 성능 추적
                        _frameCount++;
                        if (_frameCount % 100 == 0)
                        {
                            double secondsElapsed = _frameTimer.ElapsedMilliseconds / 1000.0;
                            double fps = _frameCount / secondsElapsed;
                            Console.WriteLine($"DirectX 렌더링 성능: {fps:F1} fps");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"렌더링 루프 내부 오류: {ex.Message}\n{ex.StackTrace}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"렌더링 오류: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// 텍스처 리사이징
        /// </summary>
        private void ResizeTexture(int width, int height)
        {
            lock (_renderLock)
            {
                _width = width;
                _height = height;

                // 기존 리소스 해제
                Utilities.Dispose(ref _textureView);
                Utilities.Dispose(ref _stagingTexture);

                // 새 텍스처 생성
                var textureDesc = new D3D11.Texture2DDescription
                {
                    Width = width,
                    Height = height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = D3D11.ResourceUsage.Default,
                    BindFlags = D3D11.BindFlags.ShaderResource,
                    CpuAccessFlags = D3D11.CpuAccessFlags.None,
                    OptionFlags = D3D11.ResourceOptionFlags.None
                };

                var stagingDesc = new D3D11.Texture2DDescription
                {
                    Width = width,
                    Height = height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = D3D11.ResourceUsage.Staging,
                    BindFlags = D3D11.BindFlags.None,
                    CpuAccessFlags = D3D11.CpuAccessFlags.Write,
                    OptionFlags = D3D11.ResourceOptionFlags.None
                };

                using (var texture = new D3D11.Texture2D(_device, textureDesc))
                {
                    _textureView = new D3D11.ShaderResourceView(_device, texture);
                }

                _stagingTexture = new D3D11.Texture2D(_device, stagingDesc);

                Console.WriteLine($"텍스처 크기 변경: {width}x{height}");
            }
        }

        /// <summary>
        /// 비트맵 데이터를 텍스처로 업데이트
        /// </summary>
        private void UpdateTexture(SDBitmap bitmap)
        {
            if (bitmap == null || _stagingTexture == null)
                return;

            try
            {
                Console.WriteLine($"텍스처 업데이트 시작: {bitmap.Width}x{bitmap.Height}, PixelFormat: {bitmap.PixelFormat}");

                var rect = new SDRectangle(0, 0, bitmap.Width, bitmap.Height);
                var bitmapData = bitmap.LockBits(
                    rect,
                    System.Drawing.Imaging.ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                try
                {
                    // CPU에서 스테이징 텍스처로 데이터 복사
                    var dataBox = _deviceContext.MapSubresource(
                        _stagingTexture,
                        0,
                        D3D11.MapMode.Write,
                        D3D11.MapFlags.None);

                    Console.WriteLine($"맵핑된 데이터: RowPitch={dataBox.RowPitch}, 비트맵 Stride={bitmapData.Stride}");

                    int stride = bitmapData.Stride;
                    int stagingStride = dataBox.RowPitch;

                    // 첫 번째 픽셀 값 확인 (디버깅용)
                    unsafe
                    {
                        byte* firstPixel = (byte*)bitmapData.Scan0;
                        Console.WriteLine($"첫 번째 픽셀 값: R={firstPixel[2]}, G={firstPixel[1]}, B={firstPixel[0]}, A={firstPixel[3]}");
                    }

                    for (int y = 0; y < bitmap.Height; y++)
                    {
                        IntPtr sourcePtr = bitmapData.Scan0 + y * stride;
                        IntPtr destPtr = dataBox.DataPointer + y * stagingStride;
                        Utilities.CopyMemory(destPtr, sourcePtr, Math.Min(stride, stagingStride));
                    }

                    _deviceContext.UnmapSubresource(_stagingTexture, 0);
                    Console.WriteLine("스테이징 텍스처 언맵핑 완료");

                    // 스테이징 텍스처에서 GPU 텍스처로 복사
                    using (var texture = _textureView.Resource.QueryInterface<D3D11.Texture2D>())
                    {
                        _deviceContext.CopyResource(_stagingTexture, texture);
                        Console.WriteLine("GPU 텍스처에 복사 완료");
                    }
                }
                finally
                {
                    bitmap.UnlockBits(bitmapData);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"텍스처 업데이트 오류: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// 렌더 타겟 크기 변경 이벤트 처리
        /// </summary>
        private void OnRenderTargetResize(object sender, EventArgs e)
        {
            if (_isDisposed || !_isInitialized)
                return;

            lock (_renderLock)
            {
                try
                {
                    // 렌더 타겟 해제
                    Utilities.Dispose(ref _renderTargetView);

                    // 스왑 체인 리사이즈
                    _swapChain.ResizeBuffers(
                        0,
                        _renderTarget2D.ClientSize.Width,
                        _renderTarget2D.ClientSize.Height,
                        Format.Unknown,
                        SwapChainFlags.None);

                    // 새 렌더 타겟 생성
                    using (var backBuffer = _swapChain.GetBackBuffer<D3D11.Texture2D>(0))
                    {
                        _renderTargetView = new D3D11.RenderTargetView(_device, backBuffer);
                    }

                    // 뷰포트 업데이트
                    var viewport = new Viewport
                    {
                        X = 0,
                        Y = 0,
                        Width = _renderTarget2D.ClientSize.Width,
                        Height = _renderTarget2D.ClientSize.Height,
                        MinDepth = 0.0f,
                        MaxDepth = 1.0f
                    };
                    _deviceContext.Rasterizer.SetViewport(viewport);

                    Console.WriteLine($"렌더 타겟 크기 변경: {_renderTarget2D.ClientSize.Width}x{_renderTarget2D.ClientSize.Height}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"렌더 타겟 리사이즈 오류: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 렌더 타겟 파괴 이벤트 처리
        /// </summary>
        private void OnRenderTargetDestroyed(object sender, EventArgs e)
        {
            Dispose();
        }

        /// <summary>
        /// VSync 설정
        /// </summary>
        public void SetVSync(bool enabled)
        {
            _vsyncEnabled = enabled;
        }

        /// <summary>
        /// 배경색 설정
        /// </summary>
        public void SetBackgroundColor(SDColor color)
        {
            _backgroundColor = color;
        }

        /// <summary>
        /// 스트레치 모드 설정
        /// </summary>
        public void SetStretchMode(bool stretchToFit)
        {
            _stretchToFit = stretchToFit;
        }

        /// <summary>
        /// DirectX 리소스 해제
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            _frameTimer.Stop();

            Console.WriteLine($"DirectX 렌더러 종료. 총 {_frameCount}개 프레임 렌더링");

            lock (_renderLock)
            {
                try
                {
                    if (_renderTarget2D != null)
                    {
                        _renderTarget2D.Resize -= OnRenderTargetResize;
                        _renderTarget2D.HandleDestroyed -= OnRenderTargetDestroyed;
                    }

                    Utilities.Dispose(ref _vertexBuffer);
                    Utilities.Dispose(ref _constantBuffer);
                    Utilities.Dispose(ref _inputLayout);
                    Utilities.Dispose(ref _vertexShader);
                    Utilities.Dispose(ref _pixelShader);
                    Utilities.Dispose(ref _samplerState);
                    Utilities.Dispose(ref _textureView);
                    Utilities.Dispose(ref _stagingTexture);
                    Utilities.Dispose(ref _renderTargetView);
                    Utilities.Dispose(ref _renderTarget);
                    Utilities.Dispose(ref _swapChain);
                    Utilities.Dispose(ref _deviceContext);
                    Utilities.Dispose(ref _device);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"DirectX 리소스 해제 오류: {ex.Message}");
                }
            }

            GC.SuppressFinalize(this);
        }

        ~DirectXRenderer()
        {
            Dispose();
        }
    }
}