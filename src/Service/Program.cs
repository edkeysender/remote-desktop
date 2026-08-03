using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Service;

// AllViewer Windows Service. Runs as LocalSystem (installed with `sc create ...
// start= auto`). When launched by the SCM it runs as a service; when run directly
// from a console it runs interactively (handy for debugging the supervisor).

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "AllViewerService");
builder.Services.AddHostedService<Supervisor>();

// Also log to the Windows Event Log when running as a service.
builder.Logging.AddEventLog(o => o.SourceName = "AllViewer Service");

var host = builder.Build();
host.Run();
