using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Sem.Web.Components.Shared;
using Sem.Web.Runtime;
int count=0;void Check(bool v,string label){if(!v)throw new Exception(label);count++;Console.WriteLine("PASS "+label);}
ClaimsPrincipal Who(string id,string stamp="stamp",string session="session")=>new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,id),new Claim("AspNet.Identity.SecurityStamp",stamp),new Claim(PasskeyAdditionService.SessionClaim,session)],"Identity.Application"));
var auth=new FakeAuthentication{Principal=Who("alice")};var editor=new ProbeEditor(auth);
await editor.Parameters();Check(editor.Loads==1&&editor.Clears==1,"Initial authenticated context loads once");editor.Draft="unsent";await editor.Parameters();Check(editor.Loads==1&&editor.Draft=="unsent","Same-context parent render cannot reset private editor draft");
var first=await editor.Capture();var second=await editor.Capture();Check(!editor.Accept(first)&&editor.Accept(second),"Later read/operation invalidates earlier response in same context");
editor.SelectEvent("event-b");Check(!editor.Accept(second),"Event parameter change rejects old response before async rebind");await editor.Parameters();Check(editor.Loads==2&&editor.Draft=="","Event switch clears old private draft");
var old=await editor.Capture();auth.Principal=Who("bob");await editor.Parameters();Check(!editor.Accept(old)&&editor.Loads==3,"Actor switch invalidates outstanding callbacks and reloads");
old=await editor.Capture();auth.Principal=Who("bob","new-stamp");Check(await editor.Capture() is null&&!editor.Accept(old),"Save-time changed security stamp reconciles but never submits old draft");
old=await editor.Capture();auth.Principal=Who("bob","new-stamp","new-session");Check(await editor.Capture() is null&&!editor.Accept(old),"Save-time session switch invalidates old draft");
editor.ConfigureEmbedding(true,"wrong-owner");await editor.Parameters();Check(await editor.Capture() is null,"Embedded mismatched owner key cannot load or submit private management state");
editor.ConfigureEmbedding(false,"");await editor.Parameters();old=await editor.Capture();editor.AdvanceGeneration();Check(!editor.Accept(old),"Parent context generation invalidates old response before rebind");await editor.Parameters();Check(editor.Draft=="","New generation clears private state");
old=await editor.Capture();auth.Principal=new ClaimsPrincipal();await editor.Parameters();Check(!editor.Accept(old)&&await editor.Capture() is null,"Anonymous identity clears private state and cannot submit");
auth.Principal=Who("alice");await editor.Parameters();old=await editor.Capture();editor.Dispose();Check(!editor.Accept(old)&&await editor.Capture() is null,"Disposed editor rejects late response and new operations");Console.WriteLine("Management UI boundary checks passed: "+count);
sealed class FakeAuthentication:AuthenticationStateProvider{public ClaimsPrincipal Principal=new();public override Task<AuthenticationState> GetAuthenticationStateAsync()=>Task.FromResult(new AuthenticationState(Principal));}
sealed class ProbeEditor:EventManagementComponent{
 public int Loads,Clears;public string Draft="";public ProbeEditor(AuthenticationStateProvider auth){EventId="event-a";Authentication=auth;IdentityOptions=Options.Create(new IdentityOptions());}
 public void SelectEvent(string id)=>EventId=id;public void ConfigureEmbedding(bool embedded,string key){Embedded=embedded;ContextIdentityKey=key;}public void AdvanceGeneration()=>ContextGeneration++;
 public Task Parameters()=>OnParametersSetAsync();public async Task<object?> Capture()=>await BeginOperation();public bool Accept(object? t)=>t is Ticket ticket&&Current(ticket);
 protected override void ClearPrivateState(){Clears++;Draft="";}
 protected override Task LoadCurrentAsync(){Loads++;return Task.CompletedTask;}
}
