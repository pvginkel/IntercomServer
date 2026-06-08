using IntercomServer.ChatGpt;
using IntercomServer.Utils;
using MQTTnet;
using Serilog;

namespace IntercomServer;

internal class StateManager
{
    private static readonly ILogger Logger = Log.ForContext<StateManager>();

    private Device? _callingDevice;
    private readonly List<Device> _ringing = [];
    private IDisposable? _callingAlarm;
    private readonly List<Device> _inCall = [];
    private readonly DeviceManager _devices;
    private readonly AlarmManager _alarmManager;
    private readonly IMqttClient _client;
    private readonly PlaybackManager _playbackManager;
    private readonly ConversationManager _conversation;
    private readonly AudioEndpointResolver _audioEndpointResolver;
    private CancellationTokenSource? _ringingPlayback;

    public bool IsAutoAccept { get; set; }

    public StateManager(
        DeviceManager devices,
        AlarmManager alarmManager,
        IMqttClient client,
        PlaybackManager playbackManager,
        ConversationManager conversation,
        AudioEndpointResolver audioEndpointResolver
    )
    {
        _devices = devices;
        _alarmManager = alarmManager;
        _client = client;
        _playbackManager = playbackManager;
        _conversation = conversation;
        _audioEndpointResolver = audioEndpointResolver;

        // When a conversation ends (hang-up, model goodbye, error, disconnect), reset the
        // device that was chatting.
        _conversation.SessionEnded += async (_, device) => await CleanupChat(device);

        _devices.DeviceRemoved += async (_, e) =>
        {
            _ringing.Remove(e.Device);

            await StopChat(e.Device);

            try
            {
                await LeaveCall(e.Device);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to leave call");
            }
        };
    }

    // A device that has gone offline (typically surfaced by its retained last-will state
    // message, "online": false) can no longer hang up from its own button, so stop any
    // conversation it was holding here.
    public async Task HandleDeviceState(Device device)
    {
        if (device.State?.Online == false)
            await StopChat(device);
    }

    // A device announces it is ready right after (re)booting. If it was chatting before the
    // reboot, that conversation is now orphaned — the device will never send the click that
    // ends it — so stop it here.
    public Task HandleDeviceReady(Device device) => StopChat(device);

    private async Task StopChat(Device device)
    {
        if (!_conversation.IsChatting(device))
            return;

        try
        {
            await _conversation.EndAsync(device);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to end ChatGPT conversation");
        }
    }

    public async Task HandleDeviceAction(Device device, DeviceAction action)
    {
        _callingAlarm?.Dispose();
        _callingAlarm = null;

        // A device that is in a ChatGPT conversation uses its button only to control that
        // conversation: a click hangs up. While chatting it is excluded from incoming
        // calls (see RequestCall), but other devices are unaffected — they can still call
        // each other and start their own conversations.
        if (_conversation.IsChatting(device))
        {
            if (action == DeviceAction.Click)
                await _conversation.EndAsync(device);
            return;
        }

        switch (action)
        {
            case DeviceAction.Click:
                if (_inCall.Contains(device))
                    await EndCall();
                else if (_inCall.Count > 0)
                    await JoinCall(device);
                else if (_callingDevice == null)
                    await RequestCall(device);
                else if (_callingDevice == device)
                    await CancelCall();
                else
                    await AcceptCall(device);
                break;

            case DeviceAction.LongClick:
                if (_callingDevice != null)
                    await RejectCall();
                else if (_inCall.Count == 0)
                    await StartChat(device);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }
    }

    private async Task StartChat(Device device)
    {
        Logger.Information("Device {Device} started a ChatGPT conversation", device.DeviceId);

        var started = false;

        try
        {
            // Route the device's audio to/from the server, then start the conversation.
            await device.AddEndpoint(_client, _audioEndpointResolver.Endpoint);
            await device.SetGreenLed(_client, Constants.LedOn);
            await device.SetRecording(_client, true);

            started = await _conversation.StartAsync(device);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to start ChatGPT conversation for device {Device}", device.DeviceId);
        }

        // On a failed start nothing will raise SessionEnded, so undo the device control
        // here. On success, CleanupChat runs later via the SessionEnded handler.
        if (!started)
            await CleanupChat(device);
    }

    private async Task CleanupChat(Device device)
    {
        try
        {
            await device.RemoveEndpoint(_client, _audioEndpointResolver.Endpoint);
            await device.SetRecording(_client, false);
            await device.SetGreenLed(_client, Constants.LedOff);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to reset device {Device} after ChatGPT", device.DeviceId);
        }
    }

    private async Task RequestCall(Device device)
    {
        Logger.Information("Device {Device} requested a call", device.DeviceId);

        // A device that is in a ChatGPT conversation is busy and is not rung.
        _ringing.Clear();
        _ringing.AddRange(
            _devices.GetAllEnabled().Where(p => p != device && !_conversation.IsChatting(p))
        );

        if (_ringing.Count == 0)
        {
            await device.SetRedLed(_client, Constants.CallNotPossibleAction);
            return;
        }

        _callingDevice = device;

        if (IsAutoAccept)
        {
            foreach (var ringing in _ringing.ToList())
            {
                if (_inCall.Count > 0)
                    await JoinCall(ringing);
                else
                    await AcceptCall(ringing);
            }
            return;
        }

        await device.SetRedLed(_client, Constants.CallingCallerAction);

        foreach (var item in _ringing)
        {
            await item.SetRedLed(_client, Constants.CallingCalledAction);
        }

        _ringingPlayback = new CancellationTokenSource();
        _playbackManager.StartPlayback(
            _ringing,
            Constants.AudioFiles.Ring,
            new PlaybackConfiguration(true),
            _ringingPlayback.Token
        );

        _callingAlarm = _alarmManager.SetAlarm(Constants.AttemptCallDuration, CancelCall);
    }

    private async Task CancelCall()
    {
        Logger.Information("Call was cancelled");

        if (_callingDevice == null)
            return;

        await _callingDevice.SetRedLed(_client, Constants.LedOff);

        foreach (var item in _ringing)
        {
            await item.SetRedLed(_client, Constants.LedOff);
        }

        StopRinging();
    }

    private async Task AcceptCall(Device device)
    {
        Logger.Information("Device {Device} accepted the call", device.DeviceId);

        if (_callingDevice == null)
            return;

        await _callingDevice.SetRedLed(_client, Constants.LedOff);

        foreach (var item in _ringing)
        {
            await item.SetRedLed(_client, Constants.LedOff);
        }

        await JoinCall(_callingDevice);
        await JoinCall(device);

        StopRinging();
    }

    private void StopRinging()
    {
        _callingDevice = null;
        _ringing.Clear();

        if (_ringingPlayback != null)
        {
            _ringingPlayback.Cancel();
            _ringingPlayback = null;
        }
    }

    private async Task JoinCall(Device device)
    {
        Logger.Information("Device {Device} joined the call", device.DeviceId);

        // Whenever someone joins the call, they need to subscribe to all other
        // devices' streams, and all other devices need to subscribe to the new
        // ones.

        foreach (var item in _inCall)
        {
            await item.AddEndpoint(_client, device.Configuration!.Endpoint!);
            await device.AddEndpoint(_client, item.Configuration!.Endpoint!);
        }

        await device.SetGreenLed(_client, Constants.LedOn);
        await device.SetRecording(_client, true);

        _inCall.Add(device);
    }

    private async Task LeaveCall(Device device)
    {
        Logger.Information("Device {Device} left the call", device.DeviceId);

        if (!_inCall.Contains(device))
            return;

        foreach (var item in _inCall.Where(p => p != device))
        {
            await item.RemoveEndpoint(_client, device.Configuration!.Endpoint!);
        }

        _inCall.Remove(device);
    }

    private async Task RejectCall()
    {
        Logger.Information("Call was rejected");

        if (_callingDevice == null)
            return;

        var callingDevice = _callingDevice;

        await CancelCall();

        await callingDevice.SetRedLed(_client, Constants.CallRejectedAction);

        _playbackManager.StartPlayback([callingDevice], Constants.AudioFiles.Rejected);
    }

    private async Task EndCall()
    {
        Logger.Information("Call was ended");

        foreach (var item in _inCall)
        {
            await item.SetGreenLed(_client, Constants.LedOff);
            await item.SetRecording(_client, false);

            foreach (var other in _inCall.Where(p => item != p))
            {
                await item.RemoveEndpoint(_client, other.Configuration!.Endpoint!);
            }
        }

        _inCall.Clear();
    }
}
