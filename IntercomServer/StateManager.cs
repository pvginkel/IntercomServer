using MQTTnet;

namespace IntercomServer;

internal class StateManager(DeviceManager devices, AlarmManager alarmManager, IMqttClient client)
{
    private Device? _callingDevice;
    private readonly List<Device> _ringing = [];
    private IDisposable? _callingAlarm;
    private readonly List<Device> _inCall = [];

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
        _ringing.Clear();
        _ringing.AddRange(devices.GetAllEnabled().Where(p => p != device));

        if (_ringing.Count == 0)
        {
            await device.SetRedLed(client, Constants.CallNotPossibleAction);
            return;
        }

        _callingDevice = device;

        await device.SetRedLed(client, Constants.CallingCallerAction);

        foreach (var item in _ringing)
        {
            await item.SetRedLed(client, Constants.CallingCalledAction);
        }

        _callingAlarm = alarmManager.SetAlarm(Constants.AttemptCallDuration, CancelCall);
    }

    private async Task CancelCall()
    {
        if (_callingDevice == null)
            return;

        await _callingDevice.SetRedLed(client, Constants.LedOff);

        foreach (var item in _ringing)
        {
            await item.SetRedLed(client, Constants.LedOff);
        }

        _ringing.Clear();
    }

    private async Task AcceptCall(Device device)
    {
        if (_callingDevice == null)
            return;

        await _callingDevice.SetRedLed(client, Constants.LedOff);

        foreach (var item in _ringing)
        {
            await item.SetRedLed(client, Constants.LedOff);
        }

        _ringing.Clear();

        await JoinCall(_callingDevice);
        await JoinCall(device);
    }

    private async Task JoinCall(Device device)
    {
        // Whenever someone joins the call, they need to subscribe to all other
        // devices' streams, and all other devices need to subscribe to the new
        // ones.

        foreach (var item in _inCall)
        {
            await item.SubscribeStream(client, $"intercom/client/{device.DeviceId}/stream/out");
            await device.SubscribeStream(client, $"intercom/client/{item.DeviceId}/stream/out");
        }

        await device.SetGreenLed(client, Constants.LedOn);
        await device.SetRecording(client, true);

        _inCall.Add(device);
    }

    private async Task RejectCall()
    {
        if (_callingDevice == null)
            return;

        var callingDevice = _callingDevice;

        await CancelCall();

        await callingDevice.SetRedLed(client, Constants.CallRejectedAction);
    }

    private async Task EndCall()
    {
        foreach (var item in _inCall)
        {
            await item.SetGreenLed(client, Constants.LedOff);
            await item.SetRecording(client, false);

            foreach (var other in _inCall.Where(p => item != p))
            {
                await item.UnsubscribeStream(
                    client,
                    $"intercom/client/{other.DeviceId}/stream/out"
                );
            }
        }

        _inCall.Clear();
    }
}
