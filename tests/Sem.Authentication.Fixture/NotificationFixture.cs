using Sem.Web.Core;
using Sem.Web.Data;
public static class NotificationFixture {
 public static async Task Seed(ApplicationDbContext db){
  db.Set<PartStartPreference>().AddRange(
   new(){EventId="public",UserId="synthetic-user",PartId="opening",Enabled=true,Version=1},
   new(){EventId="public",UserId="synthetic-user",PartId="time",Enabled=true,Version=1},
   new(){EventId="public",UserId="synthetic-member",PartId="transition",Enabled=true,Version=1},
   new(){EventId="public",UserId="synthetic-user",PartId="deleted-or-missing",Enabled=true,Version=1},
   new(){EventId="hidden",UserId="synthetic-user",PartId="PRIVATE_HIDDEN_PREFERENCE",Enabled=true,Version=1});
  db.Set<EventPushRule>().AddRange(new(){EventId="public",Kind="OpeningReminder",Enabled=true,Version=1,ArmedAt=1,ArmedProgressVersion=0},new(){EventId="public",Kind="SongPreparation",Enabled=false,Version=1});
  await db.SaveChangesAsync();
 }
}
