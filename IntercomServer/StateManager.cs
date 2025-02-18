using MQTTnet;

namespace IntercomServer;

internal class StateManager(DeviceManager devices, AlarmManager alarmManager, IMqttClient client)
{
    private Device? _callingDevice;
    private readonly List<Device> _ringing = [];
    private readonly List<Device> _inCall = [];
    private IDisposable? _callingAlarm;

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

        _inCall.Clear();
        _inCall.Add(_callingDevice);
        _inCall.Add(device);

        foreach (var item in _inCall)
        {
            await item.SetGreenLed(client, Constants.LedOn);
            await item.SetRecording(client, true);
        }
    }

    private async Task JoinCall(Device device)
    {
        _inCall.Add(device);

        await device.SetGreenLed(client, Constants.LedOn);
        await device.SetRecording(client, true);
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
        }
    }
}
