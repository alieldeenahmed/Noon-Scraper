using Microsoft.EntityFrameworkCore;

namespace NoonScraper.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    
}
