using Sem.Web.Components.Shared;
using Sem.Web.Core;
var original=new[]{new PartDefinitionView("a","A","Play","preset-a"),new PartDefinitionView("b","B","Sing",null),new PartDefinitionView("c","C","Play",null)};
var rows=original.ToList();var count=0;
void Check(bool v,string label){if(!v)throw new Exception(label);count++;Console.WriteLine("PASS "+label);}
Check(DefinitionDraftOrder.MoveTo(rows,"a","c")&&rows.Select(x=>x.Id).SequenceEqual(new[]{"b","c","a"}),"First to last follows final target position");
Check(DefinitionDraftOrder.MoveTo(rows,"a","b")&&rows.SequenceEqual(original),"Last to first preserves sibling order");
Check(DefinitionDraftOrder.Step(rows,"a",1)&&rows.Select(x=>x.Id).SequenceEqual(new[]{"b","a","c"}),"Down button shares draft move semantics");
Check(DefinitionDraftOrder.Step(rows,"a",-1)&&rows.SequenceEqual(original),"Up button reverses move without new identity");
foreach(var (a,b) in new[]{("a","a"),("unknown","b"),("a","unknown"),("","c")})Check(!DefinitionDraftOrder.MoveTo(rows,a,b)&&rows.SequenceEqual(original),"Invalid/same drop changes nothing: "+a+"/"+b);
Check(!DefinitionDraftOrder.Step(rows,"a",-1)&&!DefinitionDraftOrder.Step(rows,"c",1)&&!DefinitionDraftOrder.Step(rows,"unknown",1),"Buttons cannot move beyond boundaries or unknown row");
Check(!DefinitionDraftOrder.Step(rows,"a",2)&&!DefinitionDraftOrder.Step(rows,"a",0),"Only one-step keyboard alternative is accepted");
for(var i=0;i<20;i++){DefinitionDraftOrder.MoveTo(rows,"a","c");DefinitionDraftOrder.MoveTo(rows,"a","b");}
Check(rows.SequenceEqual(original)&&rows[0].SourcePresetId=="preset-a","Repeated moves preserve full immutable row data and preset provenance");
Check(original.Select(x=>x.Id).SequenceEqual(new[]{"a","b","c"}),"Editing list does not mutate saved source array");
Check(!DefinitionDraftOrder.MoveTo(new List<PartDefinitionView>(),"a","b"),"Empty draft cannot be reordered");
Console.WriteLine("Definition draft order checks passed: "+count);
