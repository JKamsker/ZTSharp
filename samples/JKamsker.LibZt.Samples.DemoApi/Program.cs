using System.Globalization;

var port = 5005;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--port")
    {
        if (i + 1 >= args.Length)
        {
            throw new InvalidOperationException("Missing value for --port.");
        }

        i++;
        port = int.Parse(args[i], NumberStyles.None, CultureInfo.InvariantCulture);
        if (port is < 1 or > ushort.MaxValue)
        {
            throw new InvalidOperationException("Invalid --port value.");
        }
    }
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

var app = builder.Build();

app.MapGet("/", () => Results.Text("demo-api ok\n", "text/plain"));
app.MapGet("/hello", () => Results.Json(new { message = "hello", atUtc = DateTimeOffset.UtcNow }));
app.MapGet("/healthz", () => Results.Ok("ok"));

app.Run();

