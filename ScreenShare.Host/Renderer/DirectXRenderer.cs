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
using SDBitmap = System.Drawing.Bitmap;
using System.Drawing;

namespace ScreenShare.Host.Rendering
{
    // Simple vertex structure with position and texture coordinates
    [StructLayout(LayoutKind.Sequential)]
    public struct Vertex
    {
        public Vector3 Position;
        public Vector2 TexCoord;

        public Vertex(Vector3 position, Vector2 texCoord)
        {
            Position = position;
            TexCoord = texCoord;
        }
    }

    public class SimplifiedDirectXRenderer : IDisposable
    {
        // Core DirectX objects
        private D3D11.Device _device;
        private D3D11.DeviceContext _context;
        private SwapChain _swapChain;
        private D3D11.RenderTargetView _renderTargetView;
        private D3D11.Texture2D _texture;
        private D3D11.ShaderResourceView _textureView;
        private D3D11.SamplerState _samplerState;
        private D3D11.Buffer _vertexBuffer;
        private D3D11.VertexShader _vertexShader;
        private D3D11.PixelShader _pixelShader;
        private D3D11.InputLayout _inputLayout;

        // Control and state
        private Control _control;
        private int _width;
        private int _height;
        private bool _disposed;
        private readonly object _renderLock = new object();
        private bool _textureCreated;

        public SimplifiedDirectXRenderer(Control control, int width, int height)
        {
            _control = control;
            _width = width;
            _height = height;
            _disposed = false;
            _textureCreated = false;

            try
            {
                InitializeDirectX();
                CreateShaders();
                CreateVertexBuffer();
                CreateSamplerState();

                // Handle control resize
                _control.Resize += (s, e) => ResizeSwapChain();
                _control.HandleDestroyed += (s, e) => Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"DirectX renderer initialization error: {ex.Message}");
                Dispose();
                throw;
            }
        }

        private void InitializeDirectX()
        {
            // Create swap chain description
            var swapChainDesc = new SwapChainDescription
            {
                BufferCount = 1,
                Usage = Usage.RenderTargetOutput,
                OutputHandle = _control.Handle,
                IsWindowed = true,
                ModeDescription = new ModeDescription(
                    _control.ClientSize.Width > 0 ? _control.ClientSize.Width : 1,
                    _control.ClientSize.Height > 0 ? _control.ClientSize.Height : 1,
                    new Rational(60, 1), Format.R8G8B8A8_UNorm),
                SampleDescription = new SampleDescription(1, 0),
                Flags = SwapChainFlags.None,
                SwapEffect = SwapEffect.Discard
            };

            // Create device and swap chain
            D3D11.Device.CreateWithSwapChain(
                DriverType.Hardware,
                D3D11.DeviceCreationFlags.BgraSupport,
                new[] { FeatureLevel.Level_10_0 },
                swapChainDesc,
                out _device,
                out _swapChain);

            _context = _device.ImmediateContext;

            // Create render target view from the back buffer
            using (var backBuffer = _swapChain.GetBackBuffer<D3D11.Texture2D>(0))
            {
                _renderTargetView = new D3D11.RenderTargetView(_device, backBuffer);
            }

            // Set viewport
            _context.Rasterizer.SetViewport(0, 0, _control.ClientSize.Width, _control.ClientSize.Height);
        }

        private void CreateShaders()
        {
            // Simple vertex shader code
            string vertexShaderCode = @"
                struct VS_INPUT
                {
                    float3 Position : POSITION;
                    float2 TexCoord : TEXCOORD0;
                };

                struct PS_INPUT
                {
                    float4 Position : SV_POSITION;
                    float2 TexCoord : TEXCOORD0;
                };

                PS_INPUT VS(VS_INPUT input)
                {
                    PS_INPUT output;
                    output.Position = float4(input.Position, 1.0f);
                    output.TexCoord = input.TexCoord;
                    return output;
                }";

            // Simple pixel shader code
            string pixelShaderCode = @"
                Texture2D shaderTexture : register(t0);
                SamplerState samplerState : register(s0);

                struct PS_INPUT
                {
                    float4 Position : SV_POSITION;
                    float2 TexCoord : TEXCOORD0;
                };

                float4 PS(PS_INPUT input) : SV_Target
                {
                    return shaderTexture.Sample(samplerState, input.TexCoord);
                }";

            // Compile and create shaders
            using (var vertexShaderBytecode = ShaderBytecode.Compile(vertexShaderCode, "VS", "vs_4_0"))
            {
                _vertexShader = new D3D11.VertexShader(_device, vertexShaderBytecode);

                // Create input layout
                var inputElements = new[]
                {
                    new D3D11.InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                    new D3D11.InputElement("TEXCOORD", 0, Format.R32G32_Float, 12, 0)
                };
                _inputLayout = new D3D11.InputLayout(_device, vertexShaderBytecode, inputElements);
            }

            using (var pixelShaderBytecode = ShaderBytecode.Compile(pixelShaderCode, "PS", "ps_4_0"))
            {
                _pixelShader = new D3D11.PixelShader(_device, pixelShaderBytecode);
            }
        }

        private void CreateVertexBuffer()
        {
            // Create a full-screen quad
            var vertices = new[]
            {
                // Bottom left triangle
                new Vertex(new Vector3(-1, 1, 0), new Vector2(0, 0)),
                new Vertex(new Vector3(1, 1, 0), new Vector2(1, 0)),
                new Vertex(new Vector3(-1, -1, 0), new Vector2(0, 1)),
                
                // Top right triangle
                new Vertex(new Vector3(-1, -1, 0), new Vector2(0, 1)),
                new Vertex(new Vector3(1, 1, 0), new Vector2(1, 0)),
                new Vertex(new Vector3(1, -1, 0), new Vector2(1, 1))
            };

            // Create vertex buffer
            var vertexBufferDesc = new D3D11.BufferDescription
            {
                Usage = D3D11.ResourceUsage.Default,
                SizeInBytes = Marshal.SizeOf<Vertex>() * vertices.Length,
                BindFlags = D3D11.BindFlags.VertexBuffer
            };

            _vertexBuffer = D3D11.Buffer.Create(_device, vertices, vertexBufferDesc);
        }

        private void CreateSamplerState()
        {
            // Create a simple linear sampler
            var samplerDesc = new D3D11.SamplerStateDescription
            {
                Filter = D3D11.Filter.MinMagMipLinear,
                AddressU = D3D11.TextureAddressMode.Clamp,
                AddressV = D3D11.TextureAddressMode.Clamp,
                AddressW = D3D11.TextureAddressMode.Clamp,
                ComparisonFunction = D3D11.Comparison.Never,
                MinimumLod = 0,
                MaximumLod = float.MaxValue
            };

            _samplerState = new D3D11.SamplerState(_device, samplerDesc);
        }

        private void CreateTexture(int width, int height)
        {
            if (_textureCreated)
            {
                // Dispose of existing texture resources
                Utilities.Dispose(ref _textureView);
                Utilities.Dispose(ref _texture);
            }

            _width = width;
            _height = height;

            // Create texture for displaying the bitmap
            var textureDesc = new D3D11.Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = D3D11.ResourceUsage.Dynamic,
                BindFlags = D3D11.BindFlags.ShaderResource,
                CpuAccessFlags = D3D11.CpuAccessFlags.Write
            };

            _texture = new D3D11.Texture2D(_device, textureDesc);
            _textureView = new D3D11.ShaderResourceView(_device, _texture);
            _textureCreated = true;
        }

        private void ResizeSwapChain()
        {
            if (_disposed || _control.ClientSize.Width <= 0 || _control.ClientSize.Height <= 0)
                return;

            lock (_renderLock)
            {
                try
                {
                    // Release render target and views
                    _context.OutputMerger.SetRenderTargets((D3D11.RenderTargetView)null);
                    Utilities.Dispose(ref _renderTargetView);

                    // Resize swap chain buffers
                    _swapChain.ResizeBuffers(
                        0,
                        Math.Max(1, _control.ClientSize.Width),
                        Math.Max(1, _control.ClientSize.Height),
                        Format.Unknown,
                        SwapChainFlags.None);

                    // Recreate render target view
                    using (var backBuffer = _swapChain.GetBackBuffer<D3D11.Texture2D>(0))
                    {
                        _renderTargetView = new D3D11.RenderTargetView(_device, backBuffer);
                    }

                    // Update viewport
                    _context.Rasterizer.SetViewport(0, 0, _control.ClientSize.Width, _control.ClientSize.Height);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Resize error: {ex.Message}");
                }
            }
        }

        public void RenderFrame(SDBitmap bitmap)
        {
            if (_disposed || bitmap == null)
                return;

            lock (_renderLock)
            {
                try
                {
                    // Create or update texture if needed
                    if (!_textureCreated || _width != bitmap.Width || _height != bitmap.Height)
                    {
                        CreateTexture(bitmap.Width, bitmap.Height);
                    }

                    // Update texture with bitmap data
                    var bitmapData = bitmap.LockBits(
                        new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height),
                        System.Drawing.Imaging.ImageLockMode.ReadOnly,
                        System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                    try
                    {
                        // Map the texture to get direct CPU access
                        var dataBox = _context.MapSubresource(
                            _texture,
                            0,
                            D3D11.MapMode.WriteDiscard,
                            D3D11.MapFlags.None);

                        // Copy bitmap data to texture
                        for (int row = 0; row < bitmap.Height; row++)
                        {
                            IntPtr sourcePtr = IntPtr.Add(bitmapData.Scan0, row * bitmapData.Stride);
                            IntPtr destPtr = IntPtr.Add(dataBox.DataPointer, row * dataBox.RowPitch);
                            SharpDX.Utilities.CopyMemory(destPtr, sourcePtr, bitmap.Width * 4);
                        }

                        _context.UnmapSubresource(_texture, 0);
                    }
                    finally
                    {
                        bitmap.UnlockBits(bitmapData);
                    }

                    // Clear the render target
                    _context.ClearRenderTargetView(_renderTargetView, new Color4(0, 0, 0, 1));

                    // Set up rendering pipeline
                    _context.OutputMerger.SetRenderTargets(_renderTargetView);
                    _context.InputAssembler.InputLayout = _inputLayout;
                    _context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
                    _context.InputAssembler.SetVertexBuffers(0, new D3D11.VertexBufferBinding(_vertexBuffer, Marshal.SizeOf<Vertex>(), 0));

                    // Set shaders
                    _context.VertexShader.Set(_vertexShader);
                    _context.PixelShader.Set(_pixelShader);

                    // Set texture and sampler
                    _context.PixelShader.SetShaderResource(0, _textureView);
                    _context.PixelShader.SetSampler(0, _samplerState);

                    // Draw the quad (6 vertices = 2 triangles)
                    _context.Draw(6, 0);

                    // Present the frame
                    _swapChain.Present(0, PresentFlags.None);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Render error: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            lock (_renderLock)
            {
                // Dispose DirectX resources in the correct order
                Utilities.Dispose(ref _vertexBuffer);
                Utilities.Dispose(ref _inputLayout);
                Utilities.Dispose(ref _vertexShader);
                Utilities.Dispose(ref _pixelShader);
                Utilities.Dispose(ref _samplerState);
                Utilities.Dispose(ref _textureView);
                Utilities.Dispose(ref _texture);
                Utilities.Dispose(ref _renderTargetView);
                Utilities.Dispose(ref _swapChain);
                Utilities.Dispose(ref _context);
                Utilities.Dispose(ref _device);
            }

            GC.SuppressFinalize(this);
        }

        ~SimplifiedDirectXRenderer()
        {
            Dispose();
        }
    }
}