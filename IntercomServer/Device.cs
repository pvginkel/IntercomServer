using System.Diagnostics;
using System.Text.Json.Nodes;

namespace IntercomServer;

[DebuggerDisplay("DeviceId = {DeviceId}, Online = {Online}, Configuration = {Configuration}")]
internal class Device(string deviceId)
{
    public string DeviceId { get; } = deviceId;
    public DeviceConfiguration? Configuration { get; private set; }
    public bool Online { get; set; }
    public bool RedLed { get; set; }
    public bool GreenLed { get; set; }
    public bool State { get; set; }

    public void ParseConfiguration(string json)
    {
        var node = JsonNode.Parse(json);

        var uniqueId = node?["unique_id"]?.ToJsonString();
        var manufacturer = node?["device"]?["manufacturer"]?.ToJsonString();
        var model = node?["device"]?["model"]?.ToJsonString();
        var name = node?["device"]?["name"]?.ToJsonString();

        Configuration = new DeviceConfiguration(uniqueId, manufacturer, model, name);
    }
}
