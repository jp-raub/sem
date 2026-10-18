using Sem.Web.Core;
using Sem.Web.Components.Shared;
int count=0;
void Check(bool value,string label){if(!value)throw new Exception(label);Console.WriteLine("PASS "+label);count++;}
ParticipationSlot Slot(string id,string? owner=null,long version=1)=>new(id,"song","Song",id,"Play",owner,owner,null,version);
var previous=new ParticipationPlan("event","Event",1,"phase","Custom","Phase",2,0,null,0,true,"me",false,
    [Slot("a"),Slot("b")],[new("q","Question","YesNo",true,"NO")],[],1);
var selected=new HashSet<string>{"a","b"};
var answers=new Dictionary<string,string?>{{"q","YES"}};
var same=ParticipationDraftRefresh.Reconcile(previous,previous,selected,answers,false);
Check(same.Selected.SetEquals(selected)&&same.Answers["q"]=="YES"&&!same.RequiresReview,"Unchanged plan retains unsent selections and answer");
var latest=previous with{Slots=[Slot("a","other",2),Slot("b")],ParticipantCount=1};
var occupied=ParticipationDraftRefresh.Reconcile(previous,latest,selected,answers,false);
Check(occupied.Selected.SetEquals(["b"])&&occupied.RemovedSlots.SequenceEqual(["Song / a"]),"Other participant removes only the occupied selection and names it");
Check(occupied.Answers["q"]=="YES"&&!occupied.RequiresReview,"Other participant allocation preserves answer without overwriting participant version");
Check(selected.Count==2&&answers["q"]=="YES", "Reconciliation never mutates caller-owned draft collections");
var ownChange=ParticipationDraftRefresh.Reconcile(previous,previous with{ParticipantVersion=2,Questions=[previous.Questions[0] with{OwnValue="YES"}]},selected,answers,false);
Check(ownChange.RequiresReview&&ownChange.Answers["q"]=="YES","Same subject changed elsewhere requires review instead of silent overwrite");
var structural=ParticipationDraftRefresh.Reconcile(previous,previous with{RowVersion=2},selected,answers,false);
Check(structural.RequiresReview&&structural.Selected.SetEquals(selected),"Structural version change retains draft but requires explicit review");
var noDraft=ParticipationDraftRefresh.Reconcile(previous,previous with{ParticipantVersion=2,Questions=[previous.Questions[0] with{OwnValue="YES"}]},new HashSet<string>(),new Dictionary<string,string?>{{"q","NO"}},false);
Check(!noDraft.RequiresReview&&noDraft.Answers["q"]=="YES","Untouched answers follow saved state when no local draft exists");
var commentDraft=ParticipationDraftRefresh.Reconcile(previous,previous with{RowVersion=2},new HashSet<string>(),new Dictionary<string,string?>{{"q","NO"}},false,true);
Check(commentDraft.RequiresReview,"Unsent comment or administrative input also requires structural review");
var typeChange=ParticipationDraftRefresh.Reconcile(previous,previous with{Questions=[new("q","Changed","Text",false,null)]},selected,answers,false);
Check(typeChange.RequiresReview&&typeChange.Answers["q"] is null,"Incompatible question type does not reuse an old answer");
var deleted=ParticipationDraftRefresh.Reconcile(previous,previous with{Slots=[Slot("b")],Questions=[]},selected,answers,false);
Check(deleted.Selected.SetEquals(["b"])&&deleted.RemovedSlots.Length==1&&deleted.Answers.Count==0&&deleted.RequiresReview,"Removed slot and question are reconciled by active IDs");
var recycled=ParticipationDraftRefresh.Reconcile(previous,previous with{Slots=[Slot("a") with{SongId="different"},Slot("b")]},selected,answers,false);
Check(!recycled.Selected.Contains("a"),"Reused slot ID cannot silently change song selection");
var released=previous with{Slots=[Slot("a","me"),Slot("b","me")]};
Check(ParticipationDraftRefresh.Reconcile(released,released,selected,answers,true).Selected.Count==2,"Valid manager release selections survive reads");
Check(!ParticipationDraftRefresh.Reconcile(released,released with{Slots=[Slot("a","other"),Slot("b","me")]},selected,answers,true).Selected.Contains("a"),"Release target reassignment removes only invalid selection");
Check(ParticipationDraftRefresh.Reconcile(released,released with{CanManage=false},selected,answers,true).Selected.Count==0,"Lost manager privilege removes release selections");
Check(ParticipationDraftRefresh.Reconcile(previous,previous with{Slots=[Slot("a",version:2),Slot("b")]},selected,answers,false).RequiresReview,"Changed selected-slot version requires review");
foreach(var different in new[]{previous with{EventId="other"},previous with{OwnUserId="other"}}){bool denied=false;try{ParticipationDraftRefresh.Reconcile(previous,different,selected,answers,false);}catch(InvalidOperationException){denied=true;}Check(denied,"Different event or subject cannot be reconciled");}
// Exercise the actual component's early-read guards without an injected service:
// reaching Authentication/Core here would fail instead of silently passing a source assertion.
foreach(var guard in new[]{"Pending","Uncertain","Busy","RefreshDisposed"})
{
    var component=new Sem.Web.Components.Pages.Participation();
    var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
    void Set(string name,object? value)=>component.GetType().GetField(name,flags)!.SetValue(component,value);
    var pending=new ParticipationEdit(Guid.NewGuid().ToString(),1,"phase","Register",[new("a",1)],[new("q","YES")]);
    Set("Plan",previous);Set("InteractiveReady",true);Set("Selected",selected);Set("Values",answers);
    Set(guard,guard=="Pending"?pending:true);
    await (Task)component.GetType().GetMethod("RefreshDraft",flags)!.Invoke(component,null)!;
    Check(ReferenceEquals(component.GetType().GetField("Selected",flags)!.GetValue(component),selected)
        && ReferenceEquals(component.GetType().GetField("Values",flags)!.GetValue(component),answers),
        $"Actual component skips refresh and preserves draft when {guard}");
    if(guard=="Pending")Check(ReferenceEquals(component.GetType().GetField("Pending",flags)!.GetValue(component),pending)
        &&pending.RequestId.Length>0,"Pending operation instance and requestId remain immutable without a service read");
}
Console.WriteLine($"Participation draft refresh checks passed: {count}");
