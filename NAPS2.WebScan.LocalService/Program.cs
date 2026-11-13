// ============================================================================
// Program.cs: точка входа службы NAPS2.WebScan Service
//
// Что делает файл:
//   1. создаёт хост приложения (Host) с рабочим каталогом рядом с exe;
//   2. регистрирует хост как Windows-службу с именем "NAPS2.WebScan Service"
//      (именно это имя потом указывается в installer.iss и sc.exe);
//   3. подключает файловый журнал (FileLogger.cs);
//   4. запускает фоновую службу Worker (Worker.cs), которая ищет сканеры и
//      раздаёт их браузеру
//
// Сборка: dotnet publish (см. docs/INSTRUCTION.md), установка осуществляется
// только через установщик NAPS2.WebScan.LocalService.Setup.exe
// ============================================================================

using NAPS2.WebScan.LocalService;

// ContentRootPath = каталог, где лежит exe
// От него считаются appsettings.json, custom-scanners.json и папка logs рядом со службой
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Регистрация Windows-службы
// Имя должно совпадать с тем, которое создаёт установщик (installer.iss), иначе служба не запустится
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "NAPS2.WebScan Service";
});

// файловый журнал: сообщения службы + логирование самого NAPS2
builder.Logging.AddProvider(new FileLoggerProvider(
    FileLoggerProvider.GetRetentionDays(builder.Configuration)));

// Основная логика: поиск сканеров, eSCL-раздача, список устройств для браузера
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
