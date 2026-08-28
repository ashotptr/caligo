using Microsoft.AspNetCore.Identity;

namespace Blog.Posts.Infrastructure.Identity
{
    public class ApplicationUser : IdentityUser
    {
        // Refresh tokens used to live here as plaintext, one per user. They now live in
        // UserSessions, hashed, so that sign-in works on several devices and a database
        // leak does not hand out usable tokens.
    }
}
