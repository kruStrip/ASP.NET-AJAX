using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MuzLoto.Web.Data;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        // EF CLI can be launched either from the web-project directory or the
        // coursework root; configuration must resolve in both cases.
        var cwd = Directory.GetCurrentDirectory();
        var rootConfig = Path.Combine(cwd, "appsettings.json");
        var nestedConfig = Path.Combine(cwd, "src", "MuzLoto.Web", "appsettings.json");
        var config = new ConfigurationBuilder()
            .AddJsonFile(rootConfig, optional: true)
            .AddJsonFile(nestedConfig, optional: true)
            .AddEnvironmentVariables()
            .Build();
        var connection = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default is required");
        return new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connection).Options);
    }
}
