using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Runtime;
using Sem.Web.Core;
namespace Sem.Web.Data;
public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<NotificationScheduleState> NotificationScheduleStates => Set<NotificationScheduleState>();
    public DbSet<NotificationReservation> NotificationReservations => Set<NotificationReservation>();
    public DbSet<SiteState> SiteStates => Set<SiteState>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<InvitationRequest> InvitationRequests => Set<InvitationRequest>();
    public DbSet<EmailTicket> EmailTickets => Set<EmailTicket>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<DailyDigestRun> DailyDigestRuns => Set<DailyDigestRun>();
    public DbSet<DailyDigestMail> DailyDigestMails => Set<DailyDigestMail>();
    public DbSet<BusinessNotification> BusinessNotifications => Set<BusinessNotification>();
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
    public DbSet<EventPhase> Phases => Set<EventPhase>();
    public DbSet<PartPresetState> PartPresetStates => Set<PartPresetState>();
    public DbSet<PartPreset> PartPresets => Set<PartPreset>();
    public DbSet<EventPartDefinition> EventPartDefinitions => Set<EventPartDefinition>();
    public DbSet<EventParticipantQr> EventParticipantQrs => Set<EventParticipantQr>();
    public DbSet<EventQrRead> EventQrReads => Set<EventQrRead>();
    public DbSet<EventQrOperation> EventQrOperations => Set<EventQrOperation>();
    public DbSet<EventReceptionState> EventReceptionStates => Set<EventReceptionState>();
    public DbSet<EventReception> EventReceptions => Set<EventReception>();
    public DbSet<ReceptionOperation> ReceptionOperations => Set<ReceptionOperation>();
    public DbSet<EventComment> EventComments => Set<EventComment>();
    public DbSet<CommentOperation> CommentOperations => Set<CommentOperation>();
    public DbSet<EventQuestion> EventQuestions => Set<EventQuestion>();
    public DbSet<EventQuestionAnswer> EventQuestionAnswers => Set<EventQuestionAnswer>();
    public DbSet<EventParticipantState> EventParticipantStates => Set<EventParticipantState>();
    public DbSet<EventSpectator> EventSpectators => Set<EventSpectator>();
    public DbSet<RegistrationOperation> RegistrationOperations => Set<RegistrationOperation>();
    public DbSet<EventProgressState> EventProgressStates => Set<EventProgressState>();
    public DbSet<ProgressMark> ProgressMarks => Set<ProgressMark>();
    public DbSet<ProgressEstimate> ProgressEstimates => Set<ProgressEstimate>();
    public DbSet<ProgressOperation> ProgressOperations => Set<ProgressOperation>();
    public DbSet<EventShorteningPolicy> EventShorteningPolicies => Set<EventShorteningPolicy>();
    public DbSet<ShorteningOperation> ShorteningOperations => Set<ShorteningOperation>();
    public DbSet<TimePart> TimeParts => Set<TimePart>();
    public DbSet<Song> Songs => Set<Song>();
    public DbSet<SongPart> SongParts => Set<SongPart>();
    public DbSet<ThemeCatalogEntry> ThemeCatalogEntries => Set<ThemeCatalogEntry>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.Entity<EventImportOperation>().HasOne<SessionEvent>().WithMany()
            .HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EventImportOperation>().HasOne<ApplicationUser>().WithMany()
            .HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EventImportOperation>().ToTable("EventImportOperations", t => {
            t.HasCheckConstraint("CK_EventImportOperation_Hash", "length(\"Id\")=36 AND length(\"RequestHash\")=64");
            t.HasCheckConstraint("CK_EventImportOperation_Result", "\"ResultVersion\">0 AND \"NewSongs\">=0 AND \"NewTransitions\">=0 AND \"NewSongs\"+\"NewTransitions\">0 AND \"SkippedRows\">=0 AND \"AcceptedAt\">=0");
        });
        b.Entity<ManualNotificationPushActivationOperation>().HasOne<ApplicationUser>().WithMany()
            .HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ManualNotificationPushActivationOperation>().ToTable("ManualNotificationPushActivationOperations", t => {
            t.HasCheckConstraint("CK_ManualPushActivationOperation_Request", "length(\"Id\")=36 AND \"ExpectedEpoch\">=0 AND \"ResultEpoch\">=0");
            t.HasCheckConstraint("CK_ManualPushActivationOperation_Result", "(\"Changed\"=1 AND \"ExpectedEpoch\"<9223372036854775807 AND \"ResultEpoch\"=\"ExpectedEpoch\"+1) OR (\"Changed\"=0 AND \"ResultEpoch\"=\"ExpectedEpoch\")");
        });
        b.Entity<ManualNotificationPushPreparation>().HasKey(x => x.RecipientId);
        b.Entity<ManualNotificationPushPreparation>().HasOne<ManualNotificationRecipient>().WithOne()
            .HasForeignKey<ManualNotificationPushPreparation>(x => x.RecipientId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ManualNotificationPushPreparation>().ToTable("ManualNotificationPushPreparations", t => {
            t.HasCheckConstraint("CK_ManualPushPreparation_Count", "\"DeviceCount\">=0 AND ((\"Outcome\"='Prepared' AND \"DeviceCount\">0) OR (\"Outcome\"<>'Prepared' AND \"DeviceCount\"=0))");
        });
        b.Entity<ManualNotificationPushDelivery>().HasOne<ManualNotificationPushPreparation>().WithMany()
            .HasForeignKey(x => x.RecipientId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ManualNotificationPushDelivery>().HasIndex(x => new { x.RecipientId, x.DeviceId }).IsUnique();
        b.Entity<ManualNotificationPushDelivery>().HasIndex(x => new { x.State, x.DueAt });
        b.Entity<ManualNotificationPushDelivery>().ToTable("ManualNotificationPushDeliveries", t => {
            t.HasCheckConstraint("CK_ManualPushDelivery_Digests", "length(\"Id\")=64 AND length(\"BindingFingerprint\")=64");
            t.HasCheckConstraint("CK_ManualPushDelivery_State", "\"State\" IN ('Pending','Claimed','Unknown','Accepted','Expired','Suppressed','Failed')");
            t.HasCheckConstraint("CK_ManualPushDelivery_Attempts", "\"Attempts\" BETWEEN 0 AND 5");
            t.HasCheckConstraint("CK_ManualPushDelivery_Claim", "(\"State\"='Claimed' AND \"Claim\" IS NOT NULL AND \"LeaseUntil\" IS NOT NULL AND \"Attempts\">0 AND \"LeaseUntil\"<=\"ExpiresAt\") OR (\"State\"<>'Claimed' AND \"Claim\" IS NULL AND \"LeaseUntil\" IS NULL)");
            t.HasCheckConstraint("CK_ManualPushDelivery_Accepted", "(\"State\"='Accepted' AND \"ProviderAcceptedAt\" IS NOT NULL) OR (\"State\"<>'Accepted' AND \"ProviderAcceptedAt\" IS NULL)");
        });
        b.Entity<ManualNotificationPushActivation>().Property(x => x.Epoch).IsConcurrencyToken();
        b.Entity<ManualNotificationPushActivation>().ToTable("ManualNotificationPushActivations", t => {
            t.HasCheckConstraint("CK_ManualPushActivation_State", "\"Id\"=1 AND \"Epoch\">0 AND \"Enabled\" IN (0,1)");
        });
        b.Entity<ManualNotificationPushAdmission>().HasKey(x => x.OperationId);
        b.Entity<ManualNotificationPushAdmission>().HasOne<ManualNotificationOperation>().WithOne()
            .HasForeignKey<ManualNotificationPushAdmission>(x => x.OperationId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ManualNotificationPushAdmission>().ToTable("ManualNotificationPushAdmissions", t => {
            t.HasCheckConstraint("CK_ManualPushAdmission_Time", "\"AcceptedAt\">=0 AND \"AcceptedAt\"<=9223372036854775207 AND \"ExpiresAt\"=\"AcceptedAt\"+600");
            t.HasCheckConstraint("CK_ManualPushAdmission_Epoch", "\"Epoch\">0");
        });
        b.Entity<ManualNotificationOperation>().HasAlternateKey(x => new { x.EventId, x.Id });
        b.Entity<ManualNotificationOperation>().HasIndex(x => new { x.EventId, x.ActorId, x.AcceptedAt, x.Id });
        b.Entity<ManualNotificationOperation>().HasOne<SessionEvent>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ManualNotificationOperation>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ManualNotificationOperation>().ToTable("ManualNotificationOperations", t => {
            t.HasCheckConstraint("CK_ManualNotificationOperation_Hashes", "length(\"RequestHash\")=64 AND length(\"PreviewFingerprint\")=64");
            t.HasCheckConstraint("CK_ManualNotificationOperation_Text", "length(trim(\"Subject\")) BETWEEN 1 AND 80 AND length(trim(\"Body\")) BETWEEN 1 AND 2000");
            t.HasCheckConstraint("CK_ManualNotificationOperation_Count", "\"RecipientCount\">0");
        });
        b.Entity<ManualNotificationRecipient>().HasOne<ManualNotificationOperation>().WithMany().HasForeignKey(x => new { x.EventId, x.OperationId }).HasPrincipalKey(x => new { x.EventId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ManualNotificationRecipient>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ManualNotificationRecipient>().HasOne<Message>().WithMany().HasForeignKey(x => x.Id).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ManualNotificationRecipient>().HasIndex(x => new { x.OperationId, x.UserId }).IsUnique();
        b.Entity<ManualNotificationRecipient>().ToTable("ManualNotificationRecipients", t => {
            t.HasCheckConstraint("CK_ManualNotificationRecipient_Id", "length(\"Id\")=64");
            t.HasCheckConstraint("CK_ManualNotificationRecipient_Pending", "\"PushState\"='Pending'");
        });
        b.Entity<EventParticipantQr>().HasKey(x=>new{x.EventId,x.UserId});
        b.Entity<EventParticipantQr>().Property(x=>x.RowVersion).IsConcurrencyToken();
        b.Entity<EventParticipantQr>().HasIndex(x=>x.TokenHash).IsUnique();
        b.Entity<EventParticipantQr>().HasOne<SessionEvent>().WithMany().HasForeignKey(x=>x.EventId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EventParticipantQr>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x=>x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EventParticipantQr>().ToTable("EventParticipantQrs",t=>{t.HasCheckConstraint("CK_ParticipantQr_Hash","length(\"TokenHash\")=64");t.HasCheckConstraint("CK_ParticipantQr_Version","\"RowVersion\">0");});
        b.Entity<EventQrRead>().HasAlternateKey(x=>new{x.EventId,x.UserId,x.Id});
        b.Entity<EventQrRead>().HasOne<SessionEvent>().WithMany().HasForeignKey(x=>x.EventId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EventQrRead>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x=>x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EventQrRead>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x=>x.ActorId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EventQrRead>().HasIndex(x=>new{x.EventId,x.At,x.Id});
        b.Entity<EventQrRead>().ToTable("EventQrReads",t=>t.HasCheckConstraint("CK_QrRead_Version","\"TokenVersion\">0"));
        b.Entity<EventQrOperation>().HasOne<SessionEvent>().WithMany().HasForeignKey(x=>x.EventId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EventQrOperation>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x=>x.ActorId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EventQrOperation>().ToTable("EventQrOperations",t=>{t.HasCheckConstraint("CK_QrOperation_Kind","\"Kind\" IN ('Issue','Read')");t.HasCheckConstraint("CK_QrOperation_Hash","length(\"RequestHash\")=64");});
        b.Entity<EventReceptionState>().HasKey(x=>new{x.EventId,x.UserId});
        b.Entity<EventReceptionState>().Property(x=>x.RowVersion).IsConcurrencyToken();
        b.Entity<EventReceptionState>().HasOne<SessionEvent>().WithMany().HasForeignKey(x=>x.EventId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EventReceptionState>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x=>x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EventReceptionState>().ToTable("EventReceptionStates",t=>t.HasCheckConstraint("CK_ReceptionState_Version","\"RowVersion\">=0"));
        b.Entity<EventReception>().HasOne<SessionEvent>().WithMany().HasForeignKey(x=>x.EventId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EventReception>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x=>x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EventReception>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x=>x.ActorId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EventReception>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x=>x.CancelledBy).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EventReception>().HasOne<EventQrRead>().WithMany().HasForeignKey(x=>new{x.EventId,x.UserId,x.ReadId}).HasPrincipalKey(x=>new{x.EventId,x.UserId,x.Id}).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EventReception>().HasIndex(x=>new{x.EventId,x.UserId}).IsUnique().HasFilter("\"CancelledAt\" IS NULL");
        b.Entity<EventReception>().HasIndex(x=>new{x.EventId,x.UserId,x.At,x.Id});
        b.Entity<EventReception>().ToTable("EventReceptions",t=>{
            t.HasCheckConstraint("CK_Reception_Method","(\"Method\"='Manual' AND \"ReadId\" IS NULL) OR (\"Method\"='QR' AND \"ReadId\" IS NOT NULL)");
            t.HasCheckConstraint("CK_Reception_Note","length(\"Note\")<=500");
            t.HasCheckConstraint("CK_Reception_Cancel","(\"CancelledAt\" IS NULL AND \"CancelledBy\" IS NULL) OR (\"CancelledAt\" IS NOT NULL AND \"CancelledBy\" IS NOT NULL)");
        });
        b.Entity<ReceptionOperation>().HasOne<SessionEvent>().WithMany().HasForeignKey(x=>x.EventId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ReceptionOperation>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x=>x.ActorId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ReceptionOperation>().HasOne<EventReception>().WithMany().HasForeignKey(x=>x.ReceptionId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ReceptionOperation>().ToTable("ReceptionOperations",t=>{
            t.HasCheckConstraint("CK_ReceptionOperation_Kind","\"Kind\" IN ('Complete','Cancel')");
            t.HasCheckConstraint("CK_ReceptionOperation_Hash","length(\"RequestHash\")=64");
        });
        b.Entity<EventComment>().HasOne<SessionEvent>().WithMany().HasForeignKey(x=>x.EventId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EventComment>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x=>x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EventComment>().HasIndex(x=>new{x.EventId,x.CreatedAt,x.Id});
        b.Entity<EventComment>().ToTable("EventComments",t=>t.HasCheckConstraint("CK_EventComments_Body","length(trim(\"Body\"))>=3"));
        b.Entity<CommentOperation>().HasOne<SessionEvent>().WithMany().HasForeignKey(x=>x.EventId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<CommentOperation>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x=>x.ActorId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<CommentOperation>().ToTable("CommentOperations",t=>{
            t.HasCheckConstraint("CK_CommentOperation_Kind","\"Kind\" IN ('Post','Delete')");
            t.HasCheckConstraint("CK_CommentOperation_Hash","length(\"RequestHash\")=64");
        });

        b.Entity<EventPushDelivery>().HasOne<SessionEvent>().WithMany().HasForeignKey(x=>x.EventId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EventPushDelivery>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x=>x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EventPushDelivery>().HasIndex(x=>new{x.EventId,x.EntryId,x.UserId,x.DeviceId}).IsUnique();
        b.Entity<EventPushDelivery>().HasIndex(x=>new{x.Status,x.Due});
        b.Entity<EventPushDelivery>().ToTable("EventPushDeliveries",t=>{
            t.HasCheckConstraint("CK_EventPushDelivery_Status","\"Status\" IN ('Pending','Sending','Accepted','Cancelled','Failed')");
            t.HasCheckConstraint("CK_EventPushDelivery_Kind","\"Kind\" IN ('OpeningReminder','SongPreparation','PartStart')");
            t.HasCheckConstraint("CK_EventPushDelivery_Version","\"RuleVersion\">0 AND \"Attempts\" BETWEEN 0 AND 5");
            t.HasCheckConstraint("CK_EventPushDelivery_Claim","(\"Status\"='Sending' AND \"Claim\" IS NOT NULL AND \"LeaseUntil\" IS NOT NULL) OR (\"Status\"<>'Sending' AND \"Claim\" IS NULL AND \"LeaseUntil\" IS NULL)");
            t.HasCheckConstraint("CK_EventPushDelivery_Accepted","(\"Status\"='Accepted' AND \"AcceptedAt\" IS NOT NULL) OR (\"Status\"<>'Accepted' AND \"AcceptedAt\" IS NULL)");
        });
        b.Entity<EventPushRule>().HasKey(x=>new{x.EventId,x.Kind});
        b.Entity<EventPushRule>().Property(x=>x.Version).IsConcurrencyToken();
        b.Entity<EventPushRule>().HasOne<SessionEvent>().WithMany().HasForeignKey(x=>x.EventId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EventPushRule>().ToTable("EventPushRules",t=>{
            t.HasCheckConstraint("CK_EventPushRule_Kind","\"Kind\" IN ('OpeningReminder','SongPreparation')");
            t.HasCheckConstraint("CK_EventPushRule_Version","\"Version\">0");
            t.HasCheckConstraint("CK_EventPushRule_Arming","(\"Enabled\"=1 AND \"ArmedAt\" IS NOT NULL AND \"ArmedProgressVersion\" IS NOT NULL AND \"ArmedProgressVersion\">=0) OR (\"Enabled\"=0 AND \"ArmedAt\" IS NULL AND \"ArmedProgressVersion\" IS NULL)");
        });
        b.Entity<PartStartPreference>().HasKey(x=>new{x.EventId,x.UserId,x.PartId});
        b.Entity<PartStartPreference>().Property(x=>x.Version).IsConcurrencyToken();
        b.Entity<PartStartPreference>().HasOne<SessionEvent>().WithMany().HasForeignKey(x=>x.EventId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<PartStartPreference>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x=>x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<PartStartPreference>().ToTable("PartStartPreferences",t=>{
            t.HasCheckConstraint("CK_PartStartPreference_Version","\"Version\">0");
            t.HasCheckConstraint("CK_PartStartPreference_Enabled","\"Enabled\" IN (0,1)");
            t.HasCheckConstraint("CK_PartStartPreference_Part","length(\"PartId\") BETWEEN 1 AND 100");
        });
        b.Entity<NotificationScheduleState>().HasKey(x=>x.EventId);
        b.Entity<NotificationScheduleState>().Property(x=>x.Version).IsConcurrencyToken();
        b.Entity<NotificationScheduleState>().HasOne<SessionEvent>().WithOne().HasForeignKey<NotificationScheduleState>(x=>x.EventId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<NotificationScheduleState>().ToTable("NotificationScheduleStates",t=>t.HasCheckConstraint("CK_NotificationSchedule_Versions","\"Version\">0 AND \"EventVersion\">=0 AND \"ProgressVersion\">=0"));
        b.Entity<NotificationReservation>().HasKey(x=>new{x.EventId,x.Id});
        b.Entity<NotificationReservation>().HasOne<NotificationScheduleState>().WithMany().HasForeignKey(x=>x.EventId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<NotificationReservation>().HasIndex(x=>new{x.State,x.DueAt});
        b.Entity<NotificationReservation>().ToTable("NotificationReservations",t=>{
            t.HasCheckConstraint("CK_Reservation_State","\"State\" IN ('Planned','Due','WaitingActual','Cancelled')");
            t.HasCheckConstraint("CK_Reservation_Reason","(\"State\"='Cancelled' AND \"CancelReason\" IS NOT NULL AND \"CancelReason\" IN ('PartRemoved','TargetStarted')) OR (\"State\"<>'Cancelled' AND \"CancelReason\" IS NULL)");
            t.HasCheckConstraint("CK_Reservation_Time","(\"State\"='WaitingActual' AND \"DueAt\" IS NULL AND \"ReferenceAt\" IS NULL) OR (\"State\" IN ('Planned','Due') AND \"DueAt\" IS NOT NULL AND \"ReferenceAt\" IS NOT NULL) OR \"State\"='Cancelled'");
            t.HasCheckConstraint("CK_Reservation_Kind","\"Kind\" IN ('OpeningReminder','SongPreparation')");
            t.HasCheckConstraint("CK_Reservation_Version","\"Version\">0");
        });
        b.Entity<DailyDigestRun>().HasKey(x=>x.Day);
        b.Entity<DailyDigestRun>().ToTable("DailyDigestRuns",t=>t.HasCheckConstraint("CK_DailyDigestRun_Day","length(\"Day\")=10 AND date(\"Day\") IS NOT NULL AND date(\"Day\")=\"Day\""));
        b.Entity<DailyDigestMail>().HasIndex(x=>new{x.Day,x.UserId}).IsUnique();
        b.Entity<DailyDigestMail>().HasIndex(x=>new{x.Status,x.Due});
        b.Entity<DailyDigestMail>().HasOne<DailyDigestRun>().WithMany().HasForeignKey(x=>x.Day).OnDelete(DeleteBehavior.Restrict);
        b.Entity<DailyDigestMail>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x=>x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<DailyDigestMail>().ToTable("DailyDigestMails",t=>{
            t.HasCheckConstraint("CK_DailyDigestMail_Status","\"Status\" IN ('Pending','Sending','Sent','Failed','Cancelled')");
            t.HasCheckConstraint("CK_DailyDigestMail_Attempts","\"Attempts\" BETWEEN 0 AND 5");
            t.HasCheckConstraint("CK_DailyDigestMail_Claim","(\"Status\"='Sending' AND \"Claim\" IS NOT NULL AND \"LeaseUntil\" IS NOT NULL) OR (\"Status\"<>'Sending' AND \"Claim\" IS NULL AND \"LeaseUntil\" IS NULL)");
            t.HasCheckConstraint("CK_DailyDigestMail_Sent","(\"Status\"='Sent' AND \"SentAt\" IS NOT NULL) OR (\"Status\"<>'Sent' AND \"SentAt\" IS NULL)");
        });
        b.Entity<BusinessNotification>().HasOne<RegistrationOperation>().WithOne().HasForeignKey<BusinessNotification>(x=>x.Id).OnDelete(DeleteBehavior.Restrict);
        b.Entity<BusinessNotification>().HasOne<SessionEvent>().WithMany().HasForeignKey(x=>x.EventId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<BusinessNotification>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x=>x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<BusinessNotification>().HasIndex(x=>new{x.Status,x.Due});
        b.Entity<BusinessNotification>().ToTable("BusinessNotifications",t=>{
            t.HasCheckConstraint("CK_BusinessNotifications_Kind","\"Kind\" IN ('Register','Release')");
            t.HasCheckConstraint("CK_BusinessNotifications_Status","\"Status\" IN ('Pending','Sending','Sent','Failed','Cancelled')");
            t.HasCheckConstraint("CK_BusinessNotifications_Attempts","\"Attempts\" BETWEEN 0 AND 5");
            t.HasCheckConstraint("CK_BusinessNotifications_Claim","(\"Status\"='Sending' AND \"Claim\" IS NOT NULL AND \"LeaseUntil\" IS NOT NULL) OR (\"Status\"<>'Sending' AND \"Claim\" IS NULL AND \"LeaseUntil\" IS NULL)");
            t.HasCheckConstraint("CK_BusinessNotifications_Sent","(\"Status\"='Sent' AND \"SentAt\" IS NOT NULL) OR (\"Status\"<>'Sent' AND \"SentAt\" IS NULL)");
        });
        b.Entity<RevokedAuthSession>().HasKey(x => x.TokenHash);
        b.Entity<RevokedAuthSession>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ServiceSettings>().ToTable("ServiceSettings", t => t.HasCheckConstraint("CK_ServiceSettings_Id", "\"Id\" = 1"));
        b.Entity<ServiceSettings>().Property(x => x.RowVersion).IsConcurrencyToken();
        b.Entity<UserProfile>().HasKey(x => x.UserId);
        b.Entity<UserProfile>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<UserProfile>().Property(x => x.RowVersion).IsConcurrencyToken();
        b.Entity<UserProfile>().ToTable("UserProfiles", t => t.HasCheckConstraint("CK_UserProfiles_SocialJson", "json_valid(\"SocialJson\")"));
        b.Entity<SessionEvent>().Property(x => x.RowVersion).IsConcurrencyToken();
        b.Entity<SessionEvent>().Property(x=>x.DefaultPrepSeconds).HasDefaultValue(600).HasSentinel(-1);
        b.Entity<SessionEvent>().ToTable("SessionEvents", t => t.HasCheckConstraint("CK_SessionEvents_PhaseKind", "\"PhaseKind\" IN ('Hidden','View')"));
        b.Entity<EventPhase>().ToTable("Phases", t => {
            t.HasCheckConstraint("CK_Phases_Kind", "\"Kind\" IN ('Hidden','View','Custom')");
            t.HasCheckConstraint("CK_Phases_MaxPicks", "\"MaxPicks\" >= -1");
            t.HasCheckConstraint("CK_Phases_Active", "\"IsActive\" IN (0,1) AND (\"IsActive\"=0 OR \"DeletedAt\" IS NULL)");
            t.HasCheckConstraint("CK_Phases_Default", "\"Kind\"='Custom' OR (\"MaxPicks\"=0 AND \"ActivateAt\" IS NULL AND \"DeactivateAt\" IS NULL AND \"DeletedAt\" IS NULL)");
        });
        b.Entity<EventPhase>().HasOne<SessionEvent>().WithMany(e => e.Phases).HasForeignKey(p => p.EventId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<EventPhase>().HasIndex(p => p.EventId).IsUnique().HasFilter("\"IsActive\"=1");
        b.Entity<EventPhase>().HasIndex(p => new { p.EventId, p.Kind }).IsUnique().HasFilter("\"Kind\" IN ('Hidden','View')");
        b.Entity<PartPresetState>().ToTable("PartPresetStates", t => t.HasCheckConstraint("CK_PartPresetStates_Id", "\"Id\" = 1"));
        b.Entity<PartPresetState>().Property(x => x.RowVersion).IsConcurrencyToken();
        b.Entity<PartPreset>().ToTable("PartPresets", t => {
            t.HasCheckConstraint("CK_PartPresets_Category", "\"Category\" IN ('Sing','Play')");
            t.HasCheckConstraint("CK_PartPresets_Name", "length(\"Name\") BETWEEN 1 AND 40");
        });
        b.Entity<PartPreset>().HasIndex(x=>x.NormalizedName).IsUnique().HasFilter("\"DeletedAt\" IS NULL");
        b.Entity<EventPartDefinition>().ToTable("EventPartDefinitions", t => {
            t.HasCheckConstraint("CK_EventPartDefinitions_Category", "\"Category\" IN ('Sing','Play')");
            t.HasCheckConstraint("CK_EventPartDefinitions_Name", "length(\"Name\") BETWEEN 1 AND 40");
        });
        b.Entity<EventPartDefinition>().HasOne<SessionEvent>().WithMany().HasForeignKey(x=>x.EventId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<EventPartDefinition>().HasIndex(x=>new {x.EventId,x.NormalizedName}).IsUnique().HasFilter("\"DeletedAt\" IS NULL");
        b.Entity<EventQuestion>().ToTable("EventQuestions",t=> {
            t.HasCheckConstraint("CK_EventQuestions_Type","\"Type\" IN ('YesNo','Text')");
            t.HasCheckConstraint("CK_EventQuestions_Text","length(\"Text\") BETWEEN 1 AND 200");
            t.HasCheckConstraint("CK_EventQuestions_PublicText","\"PublicText\"=0 OR (\"Type\"='Text' AND \"Public\"=1)");
        });
        b.Entity<EventQuestion>().HasOne<SessionEvent>().WithMany().HasForeignKey(x=>x.EventId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EventQuestion>().HasIndex(x=>new{x.EventId,x.SortOrder});
        b.Entity<EventQuestionAnswer>().HasKey(x=>new{x.QuestionId,x.UserId});
        b.Entity<EventQuestionAnswer>().HasOne<EventQuestion>().WithMany().HasForeignKey(x=>x.QuestionId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EventQuestionAnswer>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x=>x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EventQuestionAnswer>().ToTable("EventQuestionAnswers",t=>t.HasCheckConstraint("CK_EventQuestionAnswers_Value","\"Value\" IS NULL OR length(\"Value\") <= 2000"));
        b.Entity<SessionEvent>().ToTable("SessionEvents", t => t.HasCheckConstraint("CK_SessionEvents_DefaultPrep", "\"DefaultPrepSeconds\" >= 0"));
        b.Entity<SessionEvent>().ToTable("SessionEvents",t=>t.HasCheckConstraint("CK_SessionEvents_Capacity","\"Capacity\" IS NULL OR \"Capacity\" >= 0"));
        b.Entity<SongPart>().Property(x=>x.RowVersion).IsConcurrencyToken();
        b.Entity<SongPart>().ToTable("SongParts",t=>t.HasCheckConstraint("CK_SongParts_Version","\"RowVersion\" >= 0"));
        b.Entity<EventParticipantState>().HasKey(x=>new{x.EventId,x.UserId});
        b.Entity<EventParticipantState>().Property(x=>x.RowVersion).IsConcurrencyToken();
        b.Entity<EventParticipantState>().HasOne<SessionEvent>().WithMany().HasForeignKey(x=>x.EventId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EventParticipantState>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x=>x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EventSpectator>().HasKey(x=>new{x.EventId,x.UserId});
        b.Entity<EventSpectator>().HasOne<SessionEvent>().WithMany().HasForeignKey(x=>x.EventId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EventSpectator>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x=>x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EventSpectator>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x=>x.RegisteredBy).OnDelete(DeleteBehavior.Restrict);
        b.Entity<RegistrationOperation>().HasOne<SessionEvent>().WithMany().HasForeignKey(x=>x.EventId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<RegistrationOperation>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x=>x.ActorId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<RegistrationOperation>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x=>x.SubjectId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<RegistrationOperation>().HasOne<EventPhase>().WithMany().HasForeignKey(x=>new{x.EventId,x.PhaseId}).HasPrincipalKey(x=>new{x.EventId,x.Id}).OnDelete(DeleteBehavior.Restrict);
        b.Entity<RegistrationOperation>().ToTable("RegistrationOperations",t=> {
            t.HasCheckConstraint("CK_RegistrationOperations_Kind","\"Kind\" IN ('Register','Spectate','Answers','Release','RemoveSpectator')");
            t.HasCheckConstraint("CK_RegistrationOperations_Result","json_valid(\"ResultJson\")");
            t.HasCheckConstraint("CK_RegistrationOperations_Reason","length(\"Reason\") <= 500");
        });
        b.Entity<SessionEvent>().Property(x=>x.ClockFormat).HasDefaultValue("seconds");
        b.Entity<SessionEvent>().Property(x=>x.DurationFormat).HasDefaultValue("units");
        b.Entity<SessionEvent>().Property(x=>x.SongIcon).HasDefaultValue("🎵");
        b.Entity<SessionEvent>().Property(x=>x.RoundStarts).HasDefaultValue(true).HasSentinel(true);
        b.Entity<SessionEvent>().ToTable("SessionEvents",t=>t.HasCheckConstraint("CK_SessionEvents_TimeDisplay", "\"RoundStarts\" IN (0,1) AND \"ClockFormat\" IN ('seconds','minutes') AND \"DurationFormat\" IN ('units','colon') AND \"SongIcon\" IN ('🎵','💿','🎤')"));
        b.Entity<EventProgressState>().HasKey(x=>x.EventId);
        b.Entity<EventProgressState>().Property(x=>x.RowVersion).IsConcurrencyToken();
        b.Entity<EventProgressState>().HasOne<SessionEvent>().WithMany().HasForeignKey(x=>x.EventId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EventProgressState>().ToTable("EventProgressStates",t=>t.HasCheckConstraint("CK_ProgressState", "\"RowVersion\">=0 AND \"SourceVersion\">=0 AND json_valid(\"PlanJson\")"));
        b.Entity<ProgressOperation>().HasAlternateKey(x=>new{x.EventId,x.Id});
        b.Entity<ProgressOperation>().HasOne<EventProgressState>().WithMany().HasForeignKey(x=>x.EventId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ProgressOperation>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x=>x.ActorId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ProgressOperation>().ToTable("ProgressOperations",t=>t.HasCheckConstraint("CK_ProgressOperation", "\"Kind\" IN ('Record','Estimate') AND json_valid(\"ResultJson\")"));
        b.Entity<ProgressMark>().HasKey(x=>new{x.EventId,x.PartId,x.Point});
        b.Entity<ProgressMark>().HasOne<EventProgressState>().WithMany().HasForeignKey(x=>x.EventId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ProgressMark>().HasOne<ProgressOperation>().WithMany().HasForeignKey(x=>new{x.EventId,x.OperationId}).HasPrincipalKey(x=>new{x.EventId,x.Id}).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ProgressMark>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x=>x.ActorId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ProgressMark>().ToTable("ProgressMarks",t=>t.HasCheckConstraint("CK_ProgressMark_Point", "\"Point\" IN ('PrepStart','Start','End')"));
        b.Entity<ProgressEstimate>().HasKey(x=>new{x.EventId,x.PartId});
        b.Entity<ProgressEstimate>().HasOne<EventProgressState>().WithMany().HasForeignKey(x=>x.EventId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ProgressEstimate>().ToTable("ProgressEstimates",t=>t.HasCheckConstraint("CK_ProgressEstimate", "(\"PrepSeconds\" IS NULL OR \"PrepSeconds\">=0) AND (\"DurationSeconds\" IS NULL OR \"DurationSeconds\">=0)"));
        b.Entity<TimePart>().HasAlternateKey(x=>new{x.EventId,x.Id});
        b.Entity<EventShorteningPolicy>().HasKey(x=>new{x.EventId,x.PartId,x.Scope});
        b.Entity<EventShorteningPolicy>().HasOne<TimePart>().WithMany().HasForeignKey(x=>new{x.EventId,x.PartId}).HasPrincipalKey(x=>new{x.EventId,x.Id}).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EventShorteningPolicy>().ToTable("EventShorteningPolicies",t=>t.HasCheckConstraint("CK_ShorteningPolicy", "\"Scope\" IN ('Plan','Live') AND \"MinPrepSeconds\">=0 AND \"MinDurationSeconds\">=0 AND \"AllowPrep\" IN (0,1) AND \"AllowDuration\" IN (0,1)"));
        b.Entity<ShorteningOperation>().HasOne<SessionEvent>().WithMany().HasForeignKey(x=>x.EventId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ShorteningOperation>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x=>x.ActorId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ShorteningOperation>().ToTable("ShorteningOperations",t=>t.HasCheckConstraint("CK_ShorteningOperation", "\"Kind\" IN ('Policy','Approve') AND json_valid(\"ResultJson\")"));
        b.Entity<TimePart>().HasOne<SessionEvent>().WithMany().HasForeignKey(x=>x.EventId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<TimePart>().HasOne<Song>().WithMany().HasForeignKey(x=>new{x.EventId,x.SongId}).HasPrincipalKey(x=>new{x.EventId,x.Id}).OnDelete(DeleteBehavior.Restrict);
        b.Entity<TimePart>().HasIndex(x=>x.SongId).IsUnique().HasFilter("\"SongId\" IS NOT NULL");
        b.Entity<TimePart>().HasIndex(x=>new{x.EventId,x.SortOrder});
        b.Entity<TimePart>().ToTable("TimeParts",t=>{
            t.HasCheckConstraint("CK_TimeParts_Kind", "(\"Kind\"='Song' AND \"SongId\" IS NOT NULL AND \"Name\"='' AND \"DurationSeconds\"=0) OR (\"Kind\"='Transition' AND \"SongId\" IS NULL AND length(\"Name\") BETWEEN 1 AND 200 AND \"DurationSeconds\">=0 AND \"Icon\" IS NULL)");
            t.HasCheckConstraint("CK_TimeParts_Icon", "\"Icon\" IS NULL OR \"Icon\" IN ('🎵','💿','🎤')");
        });
        b.Entity<Song>().HasAlternateKey(x=>new{x.EventId,x.Id});
        b.Entity<EventPartDefinition>().HasAlternateKey(x=>new{x.EventId,x.Id});
        b.Entity<EventPhase>().HasAlternateKey(x=>new{x.EventId,x.Id});
        b.Entity<Song>().HasOne<SessionEvent>().WithMany().HasForeignKey(x=>x.EventId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Song>().ToTable("Songs", t=> {
            t.HasCheckConstraint("CK_Songs_Title", "length(\"Title\") BETWEEN 1 AND 200");
            t.HasCheckConstraint("CK_Songs_Artist", "length(\"Artist\") <= 200");
            t.HasCheckConstraint("CK_Songs_Seconds", "\"PerformanceSeconds\" >= 1 AND (\"PrepSeconds\" IS NULL OR \"PrepSeconds\" >= 0)");
        });
        b.Entity<Song>().HasIndex(x=>new{x.EventId,x.SortOrder});
        b.Entity<SongPart>().HasOne<Song>().WithMany().HasForeignKey(x=>new{x.EventId,x.SongId}).HasPrincipalKey(x=>new{x.EventId,x.Id}).OnDelete(DeleteBehavior.Restrict);
        b.Entity<SongPart>().HasOne<EventPartDefinition>().WithMany().HasForeignKey(x=>new{x.EventId,x.PartDefinitionId}).HasPrincipalKey(x=>new{x.EventId,x.Id}).OnDelete(DeleteBehavior.Restrict);
        b.Entity<SongPart>().HasOne<EventPhase>().WithMany().HasForeignKey(x=>new{x.EventId,x.PhaseId}).HasPrincipalKey(x=>new{x.EventId,x.Id}).OnDelete(DeleteBehavior.Restrict);
        b.Entity<SongPart>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x=>x.AssignedUserId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<SongPart>().ToTable("SongParts",t=>t.HasCheckConstraint("CK_SongParts_Assignment","(\"AssignedUserId\" IS NULL AND \"PhaseId\" IS NULL) OR (\"AssignedUserId\" IS NOT NULL AND \"PhaseId\" IS NOT NULL)"));
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
        b.Entity<ThemeCatalogEntry>().HasKey(x => x.Id);
        b.Entity<ThemeCatalogEntry>().Property(x => x.Id).HasMaxLength(64);
        b.Entity<ThemeCatalogEntry>().Property(x => x.Name).HasMaxLength(30);
        b.Entity<ThemeCatalogEntry>().Property(x => x.Accent).HasMaxLength(7);
        b.Entity<ThemeCatalogEntry>().Property(x => x.Background).HasMaxLength(7);
        b.Entity<ThemeCatalogEntry>().Property(x => x.Surface).HasMaxLength(7);
        b.Entity<ThemeCatalogEntry>().Property(x => x.Text).HasMaxLength(7);
        b.Entity<ThemeCatalogEntry>().Property(x => x.RowVersion).IsConcurrencyToken();
        b.Entity<ThemeCatalogEntry>().ToTable("ThemeCatalogEntries", t => {
            t.HasCheckConstraint("CK_ThemeCatalog_Id", "length(\"Id\") BETWEEN 1 AND 64 AND \"Id\" NOT GLOB '*[^a-z0-9-]*' AND \"Id\" NOT GLOB '-*' AND \"Id\" NOT GLOB '*-' AND \"Id\" NOT GLOB '*--*'");
            t.HasCheckConstraint("CK_ThemeCatalog_Name", "length(\"Name\") BETWEEN 1 AND 30 AND length(trim(\"Name\")) >= 1");
            t.HasCheckConstraint("CK_ThemeCatalog_Colors", "length(\"Accent\")=7 AND \"Accent\" GLOB '#[0-9A-F][0-9A-F][0-9A-F][0-9A-F][0-9A-F][0-9A-F]' AND length(\"Background\")=7 AND \"Background\" GLOB '#[0-9A-F][0-9A-F][0-9A-F][0-9A-F][0-9A-F][0-9A-F]' AND length(\"Surface\")=7 AND \"Surface\" GLOB '#[0-9A-F][0-9A-F][0-9A-F][0-9A-F][0-9A-F][0-9A-F]' AND length(\"Text\")=7 AND \"Text\" GLOB '#[0-9A-F][0-9A-F][0-9A-F][0-9A-F][0-9A-F][0-9A-F]'");
            t.HasCheckConstraint("CK_ThemeCatalog_FlagsAndVersion", "\"BuiltIn\" IN (0,1) AND \"RowVersion\">=0");
        });
    }
}
