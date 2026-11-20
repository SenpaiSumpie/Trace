using Trace.Infrastructure;

// The background worker: claims jobs from the queue in PostgreSQL and runs them.
// It shares the database with the API and nothing else; run as many instances as needed.
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddTraceInfrastructure();
builder.Services.AddTraceJobProcessing();

var host = builder.Build();
host.Run();
