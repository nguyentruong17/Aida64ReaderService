using Aida64ReaderService;

IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        services.AddSingleton<MQTTClientService>();
        services.AddHostedService<Worker>();
    })
    .Build();

await host.RunAsync();
