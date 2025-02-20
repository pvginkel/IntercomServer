namespace IntercomServer.Utils.Audio;

public static class AudioUtils
{
    public static void MixInBuffer(AudioFormat audioFormat, byte[] outBuffer, byte[] inBuffer)
    {
        if (audioFormat.BitRate != 16)
        {
            throw new ArgumentOutOfRangeException(
                nameof(audioFormat),
                "Only 16 bites per samples is supported"
            );
        }

        if (inBuffer.Length != outBuffer.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inBuffer),
                "In buffer length must equal out buffer length"
            );
        }

        for (int i = 0; i < inBuffer.Length; i += 2)
        {
            var inSample = (inBuffer[i] << 8) | inBuffer[i + 1];
            var outSample = (outBuffer[i] << 8) | outBuffer[i + 1];

            outSample += inSample;

            if (outSample > ushort.MaxValue)
                outSample = ushort.MaxValue;

            outBuffer[i] = (byte)(outSample >> 8);
            outBuffer[i + 1] = (byte)outSample;
        }
    }
}
