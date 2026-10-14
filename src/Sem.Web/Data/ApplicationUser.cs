using Microsoft.AspNetCore.Identity;
namespace Sem.Web.Data;
public class ApplicationUser : IdentityUser
{
    public string DisplayName { get; set; } = "";
    public bool Enabled { get; set; }
    public bool IsAdmin { get; set; }
    public bool Bootstrap { get; set; }
    public string? InvitationId { get; set; }
}
