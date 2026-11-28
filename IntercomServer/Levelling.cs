using System.Buffers.Binary;

namespace IntercomServer;

/// <summary>
/// The level the ambient and the occasional sound play at: a track's integrated loudness brought
/// to a target, held down so that its sample peak stays at −1 dBFS or below.
/// </summary>
internal static class Levelling
{
    // The level, in dBFS, the gain holds a track's peak at.
    private const double PeakCeiling = -1;

    // FFmpeg prints a level to a tenth of a dB, so a track's own peak lies up to this far above
    // the one its meter gives.
    private const double PeakRounding = 0.05;

    /// <summary>
    /// Applies to the 16-bit PCM of <paramref name="measured"/>, in place, the gain that brings its
    /// integrated loudness to <paramref name="loudness"/> LUFS, held down so that its sample peak
    /// stays at −1 dBFS or below, and returns that gain in dB.
    /// </summary>
    public static double Apply(MeasuredAudio measured, double loudness)
    {
        // Silence measures a peak of negative infinity, so its gain is the loudness's alone.
        var gain = Math.Min(loudness - measured.Loudness, PeakCeiling - (measured.Peak + PeakRounding));
        var factor = Math.Pow(10, gain / 20);
        for (var i = 0; i < measured.Pcm.Length; i += 2)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(measured.Pcm.AsSpan(i));
            BinaryPrimitives.WriteInt16LittleEndian(measured.Pcm.AsSpan(i), (short)Math.Round(sample * factor));
        }

        return gain;
    }
}
