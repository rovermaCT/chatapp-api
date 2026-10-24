using Microsoft.AspNetCore.Identity;

namespace ChatApp.Api.Entities;

public class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}
