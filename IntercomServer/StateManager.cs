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

    public StateManager(DeviceManager devices, AlarmManager alarmManager, IMqttClient client)
    {
        _devices = devices;
        _alarmManager = alarmManager;
        _client = client;

        _devices.DeviceRemoved += async (_, e) =>
        {
            _ringing.Remove(e.Device);

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

    public async Task HandleDeviceAction(Device device, DeviceAction action)
    {
        _callingAlarm?.Dispose();
        _callingAlarm = null;

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
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }
    }

    private async Task RequestCall(Device device)
    {
        Logger.Information("Device {Device} requested a call", device.DeviceId);

        _ringing.Clear();
        _ringing.AddRange(_devices.GetAllEnabled().Where(p => p != device));

        if (_ringing.Count == 0)
        {
            await device.SetRedLed(_client, Constants.CallNotPossibleAction);
            return;
        }

        _callingDevice = device;

        await device.SetRedLed(_client, Constants.CallingCallerAction);

        foreach (var item in _ringing)
        {
            await item.SetRedLed(_client, Constants.CallingCalledAction);
        }

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

        _callingDevice = null;
        _ringing.Clear();
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

        _callingDevice = null;
        _ringing.Clear();
    }

    private async Task JoinCall(Device device)
    {
        Logger.Information("Device {Device} joined the call", device.DeviceId);

        // Whenever someone joins the call, they need to subscribe to all other
        // devices' streams, and all other devices need to subscribe to the new
        // ones.

        foreach (var item in _inCall)
        {
            await item.SubscribeStream(_client, $"intercom/client/{device.DeviceId}/stream/out");
            await device.SubscribeStream(_client, $"intercom/client/{item.DeviceId}/stream/out");
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
            await item.UnsubscribeStream(_client, $"intercom/client/{device.DeviceId}/stream/out");
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
                await item.UnsubscribeStream(
                    _client,
                    $"intercom/client/{other.DeviceId}/stream/out"
                );
            }
        }

        _inCall.Clear();
    }
}
