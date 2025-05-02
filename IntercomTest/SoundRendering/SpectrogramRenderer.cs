using System.Windows;
using System.Windows.Media.Imaging;
using NAudio.Dsp;
using Window = MathNet.Numerics.Window;

namespace IntercomTest.SoundRendering;

internal class SpectrogramRenderer : BlockSoundRenderer
{
    private const int LineWidth = 2;
    private const float MinDb = -100f;
    private const float MaxDb = 0f;

    private readonly WriteableBitmap _bitmap;
    private readonly Complex[] _fftBuffer;
    private readonly float[] _window;

    public SpectrogramRenderer(WriteableBitmap bitmap)
        : base(1024)
    {
        _bitmap = bitmap;
        _window = Window.Hann(BlockSize).Select(p => (float)p).ToArray();
        _fftBuffer = new Complex[BlockSize];
    }

    protected override void AddSamples(List<float> samples)
    {
        // 2) If you want overlap, you could keep a ring of past samples—omitted here

        // 3) Copy into FFT buffer + window
        for (int i = 0; i < BlockSize; i++)
        {
            _fftBuffer[i].X = samples[i] * _window[i];
            _fftBuffer[i].Y = 0;
        }

        // 4) Do FFT (log2(FFT_SIZE) passes)
        FastFourierTransform.FFT(true, (int)Math.Log(BlockSize, 2), _fftBuffer);

        // 5) Compute magnitudes in dB and normalize to [0…1]
        float[] mags = new float[BlockSize / 2];

        for (int i = 0; i < mags.Length; i++)
        {
            float mag = (float)
                Math.Sqrt(_fftBuffer[i].X * _fftBuffer[i].X + _fftBuffer[i].Y * _fftBuffer[i].Y);
            float db = 20f * (float)Math.Log10(mag);
            // clamp and scale
            float norm = Math.Clamp((db - MinDb) / (MaxDb - MinDb), 0f, 1f);
            mags[i] = norm;
        }

        DrawSpectrogramColumn(mags);
    }

    private void DrawSpectrogramColumn(float[] magnitudes)
    {
        _bitmap.Lock();

        var height = _bitmap.PixelHeight;
        var width = _bitmap.PixelWidth;

        unsafe
        {
            // scroll left by LineWidth pixels
            int stride = _bitmap.BackBufferStride;

            byte* pBack = (byte*)_bitmap.BackBuffer;
            for (int y = 0; y < height; y++)
            {
                Buffer.MemoryCopy(
                    pBack + y * stride + 4 * LineWidth,
                    pBack + y * stride,
                    stride - 4 * LineWidth,
                    stride - 4 * LineWidth
                );
            }

            // Draw the line.

            int* pixels = (int*)_bitmap.BackBuffer;
            int bins = magnitudes.Length;

            for (int y = 0; y < height; y++)
            {
                var v = magnitudes[bins - 1 - (int)((float)bins / height * y)];
                var intensity = (byte)(v * 255);

                for (int i = LineWidth; i > 0; i--)
                {
                    pixels[y * stride / 4 + width - i] =
                        (255 << 24) | (intensity << 16) | (intensity << 8) | intensity;
                }
            }
        }

        _bitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
        _bitmap.Unlock();
    }
}
