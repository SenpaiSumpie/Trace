// Composition root for the background worker process.
// No hosted services are registered yet: durable jobs arrive with the P0 job infrastructure.
var builder = Host.CreateApplicationBuilder(args);

var host = builder.Build();
host.Run();
