namespace IntercomTest.SoundRendering;

internal abstract class BlockSoundRenderer(int blockSize = 200) : ISoundRenderer
{
    private readonly List<float> _block = [];

    public int BlockSize { get; } = blockSize;

    public void AddData(byte[] data)
    {
        // Offset 4 bytes because the first 4 bytes are the packet index.

        for (int offset = 4; offset < data.Length; offset += 2)
        {
            _block.Add(BitConverter.ToInt16(data, offset) / (float)short.MaxValue);

            if (_block.Count == BlockSize)
            {
                AddSamples(_block);
                _block.Clear();
            }
        }
    }

    protected abstract void AddSamples(List<float> samples);
}
