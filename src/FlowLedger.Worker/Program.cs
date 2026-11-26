using FlowLedger.Worker;

var builder = Host.CreateApplicationBuilder(args);

// Container probe (no shell/curl in the image): `dotnet FlowLedger.Worker.dll --healthcheck`.
if (args.Contains("--healthcheck"))
{
    return WorkerHeartbeat.IsHealthy(WorkerHeartbeat.PathFrom(builder.Configuration), WorkerHeartbeat.MaxAge, DateTimeOffset.UtcNow) ? 0 : 1;
}

WorkerHostBuilderFactory.Configure(builder);
var host = builder.Build();
host.Run();
return 0;
