using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Runtime;
using Sem.Web.Core;
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
    public DbSet<PasskeyAddition> PasskeyAdditions => Set<PasskeyAddition>();
    public DbSet<RevokedAuthSession> RevokedAuthSessions => Set<RevokedAuthSession>();
    public DbSet<ServiceSettings> ServiceSettings => Set<ServiceSettings>();
    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
    public DbSet<SessionEvent> SessionEvents => Set<SessionEvent>();
    public DbSet<EventAdmin> EventAdmins => Set<EventAdmin>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.Entity<RevokedAuthSession>().HasKey(x => x.TokenHash);
        b.Entity<RevokedAuthSession>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ServiceSettings>().ToTable("ServiceSettings", t => t.HasCheckConstraint("CK_ServiceSettings_Id", "\"Id\" = 1"));
        b.Entity<ServiceSettings>().Property(x => x.RowVersion).IsConcurrencyToken();
        b.Entity<UserProfile>().HasKey(x => x.UserId);
        b.Entity<UserProfile>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<UserProfile>().Property(x => x.RowVersion).IsConcurrencyToken();
        b.Entity<UserProfile>().ToTable("UserProfiles", t => t.HasCheckConstraint("CK_UserProfiles_SocialJson", "json_valid(\"SocialJson\")"));
        b.Entity<SessionEvent>().Property(x => x.RowVersion).IsConcurrencyToken();
        b.Entity<SessionEvent>().ToTable("SessionEvents", t => t.HasCheckConstraint("CK_SessionEvents_PhaseKind", "\"PhaseKind\" IN ('Hidden','View')"));
        b.Entity<EventAdmin>().HasKey(x => new { x.EventId, x.UserId });
        b.Entity<EventAdmin>().HasOne<SessionEvent>().WithMany(x => x.Admins).HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<EventAdmin>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Invitation>().HasIndex(i => i.TokenHash).IsUnique();
        b.Entity<EmailTicket>().HasIndex(t => t.Hash).IsUnique();
        b.Entity<PushDevice>().HasIndex(d => d.Endpoint).IsUnique();
        b.Entity<Participation>().HasIndex(p => new { p.EventId, p.UserId }).IsUnique();
        b.Entity<PasskeyAddition>().HasIndex(p => p.EmailTokenHash).IsUnique();
        b.Entity<PasskeyAddition>().HasIndex(p => new { p.UserId, p.SessionHash })
            .IsUnique().HasFilter("\"Cancelled\" = 0 AND \"CompletedAt\" IS NULL");
    }
}
