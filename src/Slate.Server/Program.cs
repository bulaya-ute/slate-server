using Microsoft.EntityFrameworkCore;
using Slate.Server.Configuration;
using Slate.Server.Data;

var builder = WebApplication.CreateBuilder(args);

// Resolved lazily via DI (not read off builder.Configuration directly) so that test overrides
// registered through WebApplicationFactory.ConfigureWebHost/ConfigureAppConfiguration - which are
// only merged in when the host finishes building - are honored. Env vars (SLATE_DB_CONNECTION,
// SLATE_DATA_DIR, SLATE_JWT_SECRET, SLATE_SERVER_NAME) take precedence, falling back to
// appsettings for local dev.
builder.Services.AddSingleton(sp => SlateOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));

builder.Services.AddDbContext<SlateDbContext>((sp, options) =>
    options
        .UseNpgsql(sp.GetRequiredService<SlateOptions>().DbConnection)
        .UseSnakeCaseNamingConvention());

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();

// Exposed so WebApplicationFactory<Program> (integration tests) can bind to this entry point.
public partial class Program { }
