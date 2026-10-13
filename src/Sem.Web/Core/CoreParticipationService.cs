using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;
using Sem.Web.Runtime;
namespace Sem.Web.Core;
public sealed record ParticipationSlot(string Id,string SongId,string Title,string PartName,string Category,string? AssignedUserId,string? AssignedName,string? PhaseId,long RowVersion);
public sealed record ParticipationQuestion(string Id,string Text,string Type,bool Required,string? OwnValue,bool Public=false,bool PublicText=false);
public sealed record SpectatorView(string UserId,string Name);
public sealed record ParticipationPlan(string EventId,string Title,long RowVersion,string PhaseId,string PhaseKind,string PhaseName,int MaxPicks,int OwnPhaseSongCount,int? Capacity,int ParticipantCount,bool CanManage,string OwnUserId,bool OwnSpectator,ParticipationSlot[] Slots,ParticipationQuestion[] Questions,SpectatorView[] Spectators,long ParticipantVersion);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SlotChoice(string Id,long RowVersion);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AnswerEdit(string QuestionId,string? Value);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ParticipationEdit(string RequestId,long RowVersion,string PhaseId,string Kind,SlotChoice[] Slots,AnswerEdit[] Answers,string? SubjectId=null,bool CompatibilityException=false,string Reason="",long ParticipantVersion=0,[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Comment=null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CapacityEdit(long RowVersion,int? Capacity);
public sealed record ParticipationItem(string Id,bool Success,string Code);
public sealed record ParticipationResult(string RequestId,int Succeeded,ParticipationItem[] Items,bool Replayed=false);
public sealed partial class CoreFoundationService {
 private async Task<(SessionEvent Event,ApplicationUser Actor,EventPhase Phase,bool Manager)> ParticipantEvent(ApplicationDbContext db,ClaimsPrincipal principal,string id,CancellationToken ct){
  var actor=await Actor(db,principal,ct);var e=await db.SessionEvents.Include(x=>x.Admins).Include(x=>x.Phases).SingleOrDefaultAsync(x=>x.Id==id,ct);if(e is null||e.Phases.Count(x=>x.IsActive)!=1)throw new UnauthorizedAccessException();var manager=actor.IsAdmin||e.Admins.Any(x=>x.UserId==actor.Id);var phase=e.ManualPhaseOverride?e.Phases.Single(x=>x.IsActive):Automatic(e,clock.GetUtcNow());if(phase.Kind=="Hidden"&&!manager)throw new UnauthorizedAccessException();return(e,actor,phase,manager);
 }
 private static async Task<HashSet<string>> Participants(ApplicationDbContext db,string id,CancellationToken ct){var users=await db.SongParts.Where(x=>x.EventId==id&&x.AssignedUserId!=null).Select(x=>x.AssignedUserId!).Distinct().ToListAsync(ct);users.AddRange(await db.EventSpectators.Where(x=>x.EventId==id).Select(x=>x.UserId).ToListAsync(ct));return users.ToHashSet(StringComparer.Ordinal);}
 public async Task<ParticipationPlan> ReadParticipationAsync(ClaimsPrincipal principal,string id,CancellationToken ct=default,string? subjectId=null){
  await using var db=await factory.CreateDbContextAsync(ct);var(e,actor,phase,manager)=await ParticipantEvent(db,principal,id,ct);var subject=subjectId??actor.Id;if(subject!=actor.Id&&!manager)throw new UnauthorizedAccessException();if(!await db.Users.AnyAsync(x=>x.Id==subject&&x.Enabled&&x.EmailConfirmed,ct))throw new ValidationException("有効な対象者を選択してください。");
  var slots=await (from p in db.SongParts join s in db.Songs on p.SongId equals s.Id join d in db.EventPartDefinitions on p.PartDefinitionId equals d.Id join u in db.Users on p.AssignedUserId equals u.Id into people from u in people.DefaultIfEmpty() where p.EventId==id&&s.DeletedAt==null&&d.DeletedAt==null orderby s.SortOrder,s.Id,d.SortOrder,p.Id select new ParticipationSlot(p.Id,s.Id,s.Title,d.Name,d.Category,p.AssignedUserId,u==null?null:u.DisplayName,p.PhaseId,p.RowVersion)).ToArrayAsync(ct);
  var own=await db.EventQuestionAnswers.Where(x=>x.UserId==actor.Id&&subject==actor.Id&&db.EventQuestions.Any(q=>q.Id==x.QuestionId&&q.EventId==id)).ToDictionaryAsync(x=>x.QuestionId,x=>x.Value,ct);var questions=await db.EventQuestions.Where(x=>x.EventId==id&&x.DeletedAt==null&&!x.Disabled).OrderBy(x=>x.SortOrder).ThenBy(x=>x.Id).ToArrayAsync(ct);
  var spectators=await(from s in db.EventSpectators join u in db.Users on s.UserId equals u.Id where s.EventId==id orderby u.DisplayName,u.Id select new SpectatorView(u.Id,u.DisplayName)).ToArrayAsync(ct);
  return new(id,e.Title,e.RowVersion,phase.Id,phase.Kind,phase.Name,phase.MaxPicks,slots.Where(x=>x.AssignedUserId==subject&&x.PhaseId==phase.Id).Select(x=>x.SongId).Distinct().Count(),e.Capacity,(await Participants(db,id,ct)).Count,manager,subject,spectators.Any(x=>x.UserId==subject),slots,questions.Select(x=>new ParticipationQuestion(x.Id,x.Text,x.Type,x.Required,own.GetValueOrDefault(x.Id),x.Public,x.Type=="Text"&&x.Public&&x.PublicText)).ToArray(),spectators,await db.EventParticipantStates.Where(x=>x.EventId==id&&x.UserId==subject).Select(x=>x.RowVersion).SingleOrDefaultAsync(ct));
 }
 public async Task SetCapacityAsync(ClaimsPrincipal principal,string id,CapacityEdit edit,CancellationToken ct=default){if(edit.Capacity<0)throw new ValidationException("人数は0以上または制限なしで指定してください。");await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);var e=await Managed(db,principal,id,ct);var actor=await Actor(db,principal,ct);if(e.RowVersion!=edit.RowVersion)throw new CoreConflictException();if(edit.Capacity is int cap&&cap<(await Participants(db,id,ct)).Count)throw new ValidationException("現在の参加人数より小さい上限には設定できません。");e.Capacity=edit.Capacity;e.RowVersion++;Audit(db,actor.Id,"EventCapacityChanged",id);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);}
 public async Task<ParticipationResult> SubmitParticipationAsync(ClaimsPrincipal principal,string id,ParticipationEdit edit,CancellationToken ct=default){
  if(!Guid.TryParse(edit.RequestId,out _)||edit.RowVersion<0||edit.ParticipantVersion<0||edit.Kind is not ("Register" or "Spectate" or "Answers" or "Release" or "RemoveSpectator")||edit.Slots is null||edit.Answers is null||edit.Slots.Length>100||edit.Answers.Length>100||edit.Slots.Any(x=>x is null||x.RowVersion<0)||edit.Answers.Any(x=>x is null)||edit.Slots.Select(x=>x.Id).Distinct().Count()!=edit.Slots.Length||edit.Answers.Select(x=>x.QuestionId).Distinct().Count()!=edit.Answers.Length)throw new ValidationException("操作・識別子・選択数・回答を確認してください。");
  if((edit.Kind is "Register" or "Release")&&edit.Slots.Length==0||(edit.Kind is "Spectate" or "Answers" or "RemoveSpectator")&&edit.Slots.Length!=0||(edit.Kind is "Release" or "RemoveSpectator")&&edit.Answers.Length!=0||edit.CompatibilityException&&edit.Kind!="Register")throw new ValidationException("選択枠・回答と操作の組み合わせを確認してください。");var reason=Text(edit.Reason,500,"理由");var comment=string.IsNullOrWhiteSpace(edit.Comment)?null:CommentBody(edit.Comment);if(comment is not null&&edit.Kind is "Release" or "RemoveSpectator")throw new ValidationException("解除操作とコメントは別々に行ってください。");
  await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);var(e,actor,phase,manager)=await ParticipantEvent(db,principal,id,ct);var subject=edit.SubjectId??actor.Id;
  if((subject!=actor.Id||edit.CompatibilityException||edit.Kind is "Release" or "RemoveSpectator")&&!manager)throw new UnauthorizedAccessException();if(edit.Kind is "Spectate" or "Answers"&&subject!=actor.Id)throw new ValidationException("観覧と回答だけの更新は本人が行ってください。");if((edit.CompatibilityException||edit.Kind is "Release" or "RemoveSpectator")&&reason.Length==0)throw new ValidationException("例外登録・管理者解除には理由が必要です。");
  var user=await db.Users.SingleOrDefaultAsync(x=>x.Id==subject&&x.Enabled&&x.EmailConfirmed,ct);if(user is null)throw new ValidationException("有効な対象者を選択してください。");var fingerprint=SemEndpoints.Hash(JsonSerializer.Serialize(edit));var previous=await db.RegistrationOperations.SingleOrDefaultAsync(x=>x.Id==edit.RequestId,ct);if(previous is not null){if(previous.ActorId!=actor.Id||previous.EventId!=id||previous.RequestHash!=fingerprint)throw new ValidationException("操作Idは再利用できません。");return JsonSerializer.Deserialize<ParticipationResult>(previous.ResultJson)! with{Replayed=true};}
  if(comment is not null)RequireCommentOpen(e,manager);if(await db.CommentOperations.AnyAsync(x=>x.Id==edit.RequestId,ct))throw new ValidationException("操作Idは再利用できません。");
  var state=await db.EventParticipantStates.SingleOrDefaultAsync(x=>x.EventId==id&&x.UserId==subject,ct);if((state?.RowVersion??0)!=edit.ParticipantVersion)throw new CoreConflictException();
  if(e.RowVersion!=edit.RowVersion||phase.Id!=edit.PhaseId)throw new CoreConflictException();if(edit.Kind is "Register" or "Spectate" or "Answers"&&phase.Kind!="Custom"||edit.Kind=="Register"&&phase.MaxPicks==0)throw new ValidationException("現在のフェーズではこの申込み／回答更新を受け付けていません。");
  var parts=await db.SongParts.Where(x=>x.EventId==id).ToListAsync(ct);var spectator=await db.EventSpectators.SingleOrDefaultAsync(x=>x.EventId==id&&x.UserId==subject,ct);var participants=await Participants(db,id,ct);var results=new List<ParticipationItem>();var now=Utc(clock.GetUtcNow());
  var questions=await db.EventQuestions.Where(x=>x.EventId==id&&x.DeletedAt==null&&!x.Disabled).ToListAsync(ct);var answers=new Dictionary<string,string?>();
  if(edit.Kind is "Register" or "Spectate" or "Answers"){
   if(edit.Answers.Any(x=>!questions.Any(q=>q.Id==x.QuestionId)))throw new ValidationException("現在有効な設問だけに回答してください。");var retained=await db.EventQuestionAnswers.Where(x=>x.UserId==subject&&questions.Select(q=>q.Id).Contains(x.QuestionId)).ToDictionaryAsync(x=>x.QuestionId,x=>x.Value,ct);foreach(var q in questions){var input=edit.Answers.SingleOrDefault(x=>x.QuestionId==q.Id);var value=Text(input is null?retained.GetValueOrDefault(q.Id):input.Value,2000,"回答");if(q.Required&&value.Length==0||q.Type=="YesNo"&&value.Length>0&&value is not ("YES" or "NO"))throw new ValidationException("必須回答とYES／NOの形式を確認してください。");answers[q.Id]=value.Length==0?null:value;}
  }
  if(edit.Kind is "Register" or "Spectate"&&!participants.Contains(subject)&&e.Capacity is int cap&&participants.Count>=cap)throw new ValidationException("イベントの参加人数上限に達しています。");
  if(edit.Kind=="Register"){
   var definitions=await db.EventPartDefinitions.Where(x=>x.EventId==id&&x.DeletedAt==null).ToDictionaryAsync(x=>x.Id,x=>x.Category,ct);var activeSongs=await db.Songs.Where(x=>x.EventId==id&&x.DeletedAt==null).Select(x=>x.Id).ToArrayAsync(ct);
   var eligible=edit.Slots.Select(c=>(Choice:c,Part:parts.SingleOrDefault(p=>p.Id==c.Id))).Where(x=>x.Part is not null&&x.Part.AssignedUserId==null&&x.Part.RowVersion==x.Choice.RowVersion&&activeSongs.Contains(x.Part.SongId)&&definitions.ContainsKey(x.Part.PartDefinitionId)).Select(x=>x.Part!).ToArray();
   foreach(var group in eligible.GroupBy(x=>x.SongId)){var categories=parts.Where(x=>x.SongId==group.Key&&x.AssignedUserId==subject).Concat(group).Select(x=>definitions.GetValueOrDefault(x.PartDefinitionId,"invalid")).ToArray();if(categories.Length>1&&(categories.Length!=2||categories.Count(x=>x=="Sing")!=1||categories.Count(x=>x=="Play")!=1)&&!edit.CompatibilityException)throw new ValidationException("兼務は1枠、または声1＋楽器1です。管理者例外には指定と理由が必要です。");}
   var counted=parts.Where(x=>x.AssignedUserId==subject&&x.PhaseId==phase.Id).Select(x=>x.SongId).ToHashSet();
   foreach(var choice in edit.Slots){var p=parts.SingleOrDefault(x=>x.Id==choice.Id);var code=p is null||!activeSongs.Contains(p.SongId)||!definitions.ContainsKey(p.PartDefinitionId)?"Unavailable":p.AssignedUserId!=null?"Occupied":p.RowVersion!=choice.RowVersion?"StaleSlot":!counted.Contains(p.SongId)&&phase.MaxPicks>=0&&counted.Count>=phase.MaxPicks?"PhaseLimit":"Registered";if(code=="Registered"){p!.AssignedUserId=subject;p.PhaseId=phase.Id;p.RowVersion++;counted.Add(p.SongId);}results.Add(new(choice.Id,code=="Registered",code));}
   if(results.Any(x=>x.Success)&&spectator is not null)db.EventSpectators.Remove(spectator);
  }else if(edit.Kind=="Spectate"){
   if(parts.Any(x=>x.AssignedUserId==subject))throw new ValidationException("演奏登録がある場合は観覧申込みできません。管理者へ解除を依頼してください。");if(spectator is null){db.EventSpectators.Add(new(){EventId=id,UserId=subject,RegisteredAt=now,RegisteredBy=actor.Id});results.Add(new(subject,true,"SpectatorRegistered"));}else results.Add(new(subject,false,"AlreadySpectator"));
  }else if(edit.Kind=="Answers"){
   if(!participants.Contains(subject))throw new ValidationException("参加登録のある本人のみ回答を更新できます。");results.Add(new(subject,true,"AnswersUpdated"));
  }else if(edit.Kind=="Release"){
   foreach(var c in edit.Slots){var p=parts.SingleOrDefault(x=>x.Id==c.Id);if(p is null||p.AssignedUserId!=subject||p.RowVersion!=c.RowVersion)throw new CoreConflictException();}foreach(var c in edit.Slots){var p=parts.Single(x=>x.Id==c.Id);p.AssignedUserId=p.PhaseId=null;p.RowVersion++;results.Add(new(p.Id,true,"Released"));}
  }else{
   if(spectator is null)throw new CoreConflictException();db.EventSpectators.Remove(spectator);results.Add(new(subject,true,"SpectatorRemoved"));
  }
  if(results.Any(x=>x.Success)&&edit.Kind is "Register" or "Spectate" or "Answers"){
   foreach(var q in questions){var answer=await db.EventQuestionAnswers.SingleOrDefaultAsync(x=>x.QuestionId==q.Id&&x.UserId==subject,ct);if(answer is null){answer=new(){QuestionId=q.Id,UserId=subject};db.EventQuestionAnswers.Add(answer);}answer.Value=answers[q.Id];answer.UpdatedAt=now;}
  }
  if(results.Any(x=>x.Success)){if(state is null){state=new(){EventId=id,UserId=subject};db.EventParticipantStates.Add(state);}state.RowVersion++;}

  if(edit.Kind is "Register" or "Release"&&results.Any(x=>x.Success)){
   var successful=results.Where(x=>x.Success).Select(x=>x.Id).ToArray();
   var snapshot=await(from p in db.SongParts join song in db.Songs on p.SongId equals song.Id join definition in db.EventPartDefinitions on p.PartDefinitionId equals definition.Id where p.EventId==id&&successful.Contains(p.Id) orderby song.SortOrder,song.Id,definition.SortOrder,p.Id select new BusinessSlot(p.Id,song.Id,song.Title,definition.Name)).ToArrayAsync(ct);
   if(snapshot.Length!=successful.Length)throw new InvalidOperationException("Business notification snapshot incomplete");
   businessNotifications.Enqueue(db,edit.RequestId,e,actor,subject,edit.Kind,reason,snapshot,clock.GetUtcNow());
  }
  // Register comments remain independent of per-slot failures; spectator/answer comments require their success.
  if(comment is not null&&(edit.Kind=="Register"||results.Any(x=>x.Success)&&edit.Kind is "Spectate" or "Answers")){
   RequireCommentOpen(e,manager);AddComment(db,e,actor.Id,edit.RequestId,comment,manager);results.Add(new(edit.RequestId,true,"CommentCreated"));
  }
  var result=new ParticipationResult(edit.RequestId,results.Count(x=>x.Success),results.ToArray());db.RegistrationOperations.Add(new(){Id=edit.RequestId,EventId=id,ActorId=actor.Id,SubjectId=subject,PhaseId=phase.Id,Kind=edit.Kind,Reason=reason,At=now,RequestHash=fingerprint,ResultJson=JsonSerializer.Serialize(result)});Audit(db,actor.Id,"Participation"+edit.Kind,edit.RequestId);
  await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return result;
 }
}
