using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Mogu.Core.Data;

/// <summary>Lets <c>dotnet ef</c> create the context without booting the web host.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MoguDbContext>
{
    public MoguDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MoguDbContext>()
            .UseSqlite("Data Source=mogu.db")
            .Options;
        return new MoguDbContext(options);
    }
}
