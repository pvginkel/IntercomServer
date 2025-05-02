using System.Windows;
using System.Windows.Media.Imaging;

namespace IntercomTest.SoundRendering;

internal class WaveRenderer(WriteableBitmap bitmap) : BlockSoundRenderer
{
    protected override void AddSamples(List<float> samples)
    {
        var amplitude = samples.Average(Math.Abs);

        bitmap.Lock();

        var height = bitmap.PixelHeight;
        var width = bitmap.PixelWidth;

        // 1) Scroll left by 1 px: copy pixels [1..W-1] → [0..W-2]
        int stride = bitmap.BackBufferStride;
        unsafe
        {
            var pBack = (byte*)bitmap.BackBuffer;
            for (int y = 0; y < height; y++)
            {
                Buffer.MemoryCopy(
                    pBack + (y * stride) + 4, // start at x=1
                    pBack + (y * stride), // dest at x=0
                    stride - 4, // dest buffer size
                    stride - 4
                ); // copy this many bytes
            }
        }

        // 2) Draw new vertical line at x = WIDTH-1
        int midY = height / 2;
        int lineHeight = (int)(amplitude * midY);
        unsafe
        {
            var p = (int*)bitmap.BackBuffer;
            for (int y = 0; y < height; y++)
            {
                int color = 0;
                if (y >= midY - lineHeight && y <= midY + lineHeight)
                    color = 0x00FF0000; // ARGB red
                p[y * width + (width - 1)] = color;
            }
        }

        bitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
        bitmap.Unlock();
    }
}
