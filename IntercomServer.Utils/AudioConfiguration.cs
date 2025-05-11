namespace IntercomServer.Utils;

public record AudioConfiguration(
    double VolumeScaleLow,
    double VolumeScaleHigh,
    bool EnableAudioProcessing,
    int AudioBufferMs,
    int MicrophoneGainBits,
    bool RecordingAutoVolumeEnabled,
    double RecordingSmoothingFactor,
    bool PlaybackAutoVolumeEnabled,
    double PlaybackTargetDb
);
