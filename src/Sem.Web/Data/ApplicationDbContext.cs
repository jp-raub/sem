using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Runtime;
namespace Sem.Web.Data;
public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<SiteState> SiteStates => Set<SiteState>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<InvitationRequest> InvitationRequests => Set<InvitationRequest>();
    public DbSet<EmailTicket> EmailTickets => Set<EmailTicket>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<Delivery> Deliveries => Set<Delivery>();
    public DbSet<PushDevice> PushDevices => Set<PushDevice>();
    public DbSet<TestEvent> Events => Set<TestEvent>();
    public DbSet<Participation> Participations => Set<Participation>();
    public DbSet<Audit> Audits => Set<Audit>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.Entity<Invitation>().HasIndex(i => i.TokenHash).IsUnique();
        b.Entity<EmailTicket>().HasIndex(t => t.Hash).IsUnique();
        b.Entity<PushDevice>().HasIndex(d => d.Endpoint).IsUnique();
        b.Entity<Participation>().HasIndex(p => new { p.EventId, p.UserId }).IsUnique();
    }
}
