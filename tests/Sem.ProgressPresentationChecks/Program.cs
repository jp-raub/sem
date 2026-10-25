using Sem.Web.Core;
using Sem.Web.Components.Shared;
var checks=0;
void Check(bool v,string label){if(!v)throw new Exception(label);checks++;Console.WriteLine("PASS "+label);}
foreach(var(d,s)in new(long,string)[]{(-1,"none"),(0,"none"),(1,"low"),(59999,"low"),(60000,"medium"),(299999,"medium"),(300000,"high")})Check(ProgressPresentation.Severity(d)==s,"Strict delay boundary "+d);
var t=DateTimeOffset.Parse("2026-10-25T03:00:00Z");
var part=new TimedPart("song","Song","A","Artist","🎵","a",t,t.AddMinutes(1),t.AddMinutes(5),240,60);
var point=new ProgressPoint("Start",t,t.AddSeconds(-30),null);var row=new ProgressRow(part,part,new[]{point},"準備中",null);var next=new ProgressNext("song","Start","演奏開始");
Check(ProgressPresentation.Delay(row,point,next,t.AddSeconds(60))==60000,"Future recovery cannot hide overdue current point");
Check(ProgressPresentation.Delay(row,point,next,t.AddSeconds(-40))==0,"Unreached current point is not late");
Check(ProgressPresentation.Delay(row,point,next with{PartId="other"},t.AddHours(1))==0,"Other future part does not inherit wall-clock lateness");
Check(ProgressPresentation.Delay(row,point with{Actual=t},next,t.AddHours(1))==0,"Recorded point does not accrue ongoing delay");
Check(ProgressPresentation.Delay(row,point with{Latest=t.AddMinutes(5)},next,t)==300000,"Latest vs original delay retained before target");
Check(ProgressPresentation.DelayText(59999)=="1分00秒遅れ"&&ProgressPresentation.DelayText(60001)=="1分01秒遅れ","Ceiling seconds never rolls 59.999s into zero seconds");
Check(ProgressPresentation.DelayText(0)=="","No delay has no warning text");
Check(ProgressPresentation.PointLabel("Song","Start")=="演奏開始"&&ProgressPresentation.PointLabel("Song","End")=="演奏終了"&&ProgressPresentation.PointLabel("Transition","Start")=="進行開始"&&ProgressPresentation.PointLabel("Opening","Start")=="受付開始","Point labels do not invent song stages for transitions");
Check(ProgressPresentation.Duration(row)=="準備 1:00 ／ 演奏 4:00","Preparation precedes performance duration");
Check(row.Points[0]==point&&row.Original==part,"Presentation leaves original and actual records unchanged");
Console.WriteLine("Progress presentation checks passed: "+checks);
