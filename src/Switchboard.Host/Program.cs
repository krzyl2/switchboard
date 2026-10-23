using System.Net;
using Microsoft.Extensions.Options;
using Switchboard.Application.Options;
using Switchboard.Host.Composition;
using Switchboard.Host.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(console =>
{
    console.SingleLine = true;
    console.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
});

builder.Services.AddSwitchboard();

// The ingress port is the only listener; explicit Listen overrides ASPNETCORE_URLS / HTTP_PORTS.
builder.WebHost.ConfigureKestrel(kestrel =>
{
    var ingress = kestrel.ApplicationServices.GetRequiredService<IOptions<IngressOptions>>().Value;
    kestrel.Listen(IPAddress.Any, ingress.Port);
});

var app = builder.Build();
StartupReport.Log(app.Services);
app.UseSwitchboardWeb();
app.Run();

/// <summary>Entry point; public so the in-memory host tests can start it.</summary>
public partial class Program;
