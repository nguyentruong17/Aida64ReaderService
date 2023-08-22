using MQTTnet.Client.Connecting;
using MQTTnet.Client.Disconnecting;
using MQTTnet.Extensions.ManagedClient;
using MQTTnet;
using MQTTnet.Client.Options;

namespace Aida64ReaderService
{
    public sealed class MQTTClientService
    {
        private readonly IManagedMqttClient _mqttClient;

        private readonly String _clientId = "DotNet-Torrent7-C#";
        private readonly String _protocol = "ws";
        private readonly String _host = "192.168.0.62";
        private readonly String _port = "9001";
        private readonly String _topic = "bedroom/torrent7";

        public bool IsConnected { get; private set; }

        public MQTTClientService()
        {
            _mqttClient = new MqttFactory().CreateManagedMqttClient();

            IsConnected = false;

            // Set up handlers
            _mqttClient.ConnectedHandler = new MqttClientConnectedHandlerDelegate((e) =>
            {
                IsConnected = true;
                Console.WriteLine("Connected!!");
            });
            _mqttClient.DisconnectedHandler = new MqttClientDisconnectedHandlerDelegate((e) =>
            {
                IsConnected = false;
                Console.WriteLine("Disconnected!!");
            });
            _mqttClient.ConnectingFailedHandler = new ConnectingFailedHandlerDelegate((e) =>
            {
                IsConnected = false;
                Console.WriteLine("Connection failed check network or broker!");
            });
        }

        async public Task ExecuteStartAsync()
        {
            MqttClientOptionsBuilder builder = new MqttClientOptionsBuilder()
                                                .WithClientId(_clientId)
                                                .WithWebSocketServer($"{_protocol}://{_host}:{_port}");

            ManagedMqttClientOptions options = new ManagedMqttClientOptionsBuilder()
                                                .WithAutoReconnectDelay(TimeSpan.FromSeconds(10))
                                                .WithClientOptions(builder.Build())
                                                .Build();

            Console.WriteLine("Starting MQTTClientService...");
            await _mqttClient.StartAsync(options);
            Console.WriteLine("MQTTClientService Started!");
        }

        async public Task ExecutePublishAsync(String json)
        {
            try
            {
                await _mqttClient.PublishAsync(_topic, json);
            } catch (Exception ex)
            {
                Console.WriteLine("Error while trying to publish: ", ex.ToString());
            }
        }

        async public Task ExecuteStopAsync(CancellationToken cancellationToken)
        {
            Console.WriteLine("Stopping MQTTClientService Gracefully...");
            await _mqttClient.StopAsync();
            Console.WriteLine("MQTTClientService Gracefully Stopped! Cancellation Token: ", cancellationToken.ToString());
        }
    }
}
