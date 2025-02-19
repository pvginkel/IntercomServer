namespace IntercomServer.Audio;

internal record AudioFormat(AudioChannelLayout ChannelLayout, int SampleRate, int BitRate)
{
    public int BytesPerSecond
    {
        get
        {
            var channelBytes = ChannelLayout switch
            {
                AudioChannelLayout.Mono => 1,
                AudioChannelLayout.Stereo => 2,
                _ => throw new ArgumentOutOfRangeException()
            };

            return SampleRate * channelBytes * (BitRate / 8);
        }
    }
};

internal enum AudioChannelLayout
{
    Mono,
    Stereo
}
