using FlowLedger.Worker;

var builder = Host.CreateApplicationBuilder(args);
WorkerHostBuilderFactory.Configure(builder);
var host = builder.Build();
host.Run();
