using MQTTnet;

namespace IntercomServer;

internal class StateManager(
    DeviceManager devices,
    AlarmManager alarmManager,
    CallManager callManager,
    IMqttClient client
)
{
    private Device? _callingDevice;
    private readonly List<Device> _ringing = [];
    private IDisposable? _callingAlarm;

    public async Task HandleDeviceAction(Device device, DeviceAction action)
    {
        _callingAlarm?.Dispose();
        _callingAlarm = null;

        switch (action)
        {
            case DeviceAction.Click:
                if (callManager.InCall.Contains(device))
                    await EndCall();
                else if (callManager.InCall.Count > 0)
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

        callManager.StartCall();

        await JoinCall(_callingDevice);
        await JoinCall(device);
    }

    private async Task JoinCall(Device device)
    {
        callManager.AddDevice(device);

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
        foreach (var item in callManager.InCall)
        {
            await item.SetGreenLed(client, Constants.LedOff);
            await item.SetRecording(client, false);
        }
    }
}
