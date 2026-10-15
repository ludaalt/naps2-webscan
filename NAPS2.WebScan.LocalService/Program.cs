using NAPS2.WebScan.LocalService;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "NAPS2.WebScan Service";
});

// файловый журнал: сообщения службы + логирование самого NAPS2 
builder.Logging.AddProvider(new FileLoggerProvider(
    FileLoggerProvider.GetRetentionDays(builder.Configuration)));

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();