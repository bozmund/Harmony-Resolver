using Harmony.Resolver.Mcp.Tools;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient("resolver", c => c.BaseAddress = new Uri(builder.Configuration["Resolver:BaseUrl"] ?? "http://localhost:8080"));
builder.Services.AddMcpServer().WithHttpTransport().WithTools<DiagnosticTools>();
var app = builder.Build();
app.MapMcp("/mcp");
app.Run();
