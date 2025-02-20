using System.Windows;

namespace IntercomTest;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var configuration = new ServerConfiguration
        {
            Host = Environment.GetEnvironmentVariable("MQTT_HOST"),
            Port = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MQTT_PORT"))
                ? null
                : int.Parse(Environment.GetEnvironmentVariable("MQTT_PORT")!),
            Username = Environment.GetEnvironmentVariable("MQTT_USERNAME"),
            Password = Environment.GetEnvironmentVariable("MQTT_PASSWORD")
        };
    }
}
