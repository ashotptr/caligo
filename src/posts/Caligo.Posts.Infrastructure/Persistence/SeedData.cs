using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Caligo.Posts.Domain.Posts;
using Caligo.Posts.Infrastructure.Identity;

namespace Caligo.Posts.Infrastructure.Persistence
{
    public static class SeedData
    {
        public static async Task Initialize(IServiceProvider serviceProvider, IConfiguration configuration)
        {
            var roleManager = serviceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            var userManager = serviceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var context = serviceProvider.GetRequiredService<ApplicationDbContext>();
            var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("SeedData");

            string[] roleNames = { "Admin", "Writer", "Reader" };
            
            foreach (var roleName in roleNames)
            {
                var roleExist = await roleManager.RoleExistsAsync(roleName);
                
                if (!roleExist)
                {
                    await roleManager.CreateAsync(new IdentityRole(roleName));
                }
            }

            var adminEmail = configuration["AdminUser:Email"];
            var adminPassword = configuration["AdminUser:Password"];

            if (string.IsNullOrWhiteSpace(adminEmail) || string.IsNullOrWhiteSpace(adminPassword))
            {
                logger.LogWarning("AdminUser:Email / AdminUser:Password are not configured, skipping admin and demo post seeding.");
                
                return;
            }

            var adminUser = await userManager.FindByEmailAsync(adminEmail);
            
            if (adminUser == null)
            {
                adminUser = new ApplicationUser
                {
                    UserName = adminEmail,
                    Email = adminEmail,
                };

                IdentityResult result = await userManager.CreateAsync(adminUser, adminPassword);

                if (result.Succeeded)
                {
                    await userManager.AddToRoleAsync(adminUser, "Admin");
                }
                else
                {
                    logger.LogWarning("Could not create admin user: {Errors}", string.Join("; ", result.Errors.Select(e => e.Description)));
                    
                    return;
                }
            }

            if (!context.BlogPosts.Any())
            {
                context.BlogPosts.AddRange(DemoPosts.Select(p => BlogPost.Publish(
                    p.title, p.content, p.tags, adminUser.Id, DateTime.UtcNow.AddDays(p.daysAgo * -1))));

                await context.SaveChangesAsync();
                
                logger.LogInformation("Seeded {Count} demo posts.", DemoPosts.Count);
            }
        }

        private static readonly List<(string title, List<string> tags, int daysAgo, string content)> DemoPosts = new()
        {
            ("Sample post", new List<string> { "sample" }, 1,
            @"Placeholder content, so the list, excerpt, tags, and Markdown renderer have
            something to show.

            **Bold**, *italic*, `inline code`, and a [link](https://github.com/ashotptr).

            - One
            - Two

            | Column | Column |
            | ------ | ------ |
            | Row    | cell   |

            ```csharp
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddDbContext<ApplicationDbContext>(o => o.UseNpgsql(cs));
            ```
            ")
        };
    }
}
