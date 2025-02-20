using IntercomServer.Utils;
using IntercomServer.Utils.Audio;

namespace IntercomServer;

internal class Constants
{
    public static readonly DeviceLedAction CallNotPossibleAction =
        new(DeviceLedState.On, Duration: 1000);
    public static readonly DeviceLedAction CallingCallerAction =
        new(DeviceLedState.Blink, On: 500, Off: 500);
    public static readonly DeviceLedAction CallingCalledAction =
        new(DeviceLedState.Blink, On: 500, Off: 500);
    public static readonly DeviceLedAction CallRejectedAction =
        new(DeviceLedState.Blink, Duration: 2000, On: 200, Off: 200);
    public static readonly DeviceLedAction LedOff = new(DeviceLedState.Off);
    public static readonly DeviceLedAction LedOn = new(DeviceLedState.On);
    public static readonly TimeSpan AttemptCallDuration = TimeSpan.FromSeconds(15);

    public static readonly AudioFormat AudioFormat = new(AudioChannelLayout.Mono, 16000, 16);
    public static readonly TimeSpan AudioLeadBuffer = TimeSpan.FromMilliseconds(30);
    public static readonly TimeSpan AudioTrailBuffer = TimeSpan.FromMilliseconds(70);
    public static readonly TimeSpan OutStreamInterval = TimeSpan.FromMilliseconds(10);
}
