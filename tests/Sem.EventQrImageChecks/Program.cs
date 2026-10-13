using Sem.Web.Core;
int passed=0;void Check(bool valid,string label){if(!valid)throw new Exception(label);passed++;Console.WriteLine("PASS "+label);}
var token=new string('A',64);var link=EventQrImage.Link("https://example.test/","event",token);var uri=new Uri(link);
Check(uri.Scheme=="https"&&uri.Host=="example.test"&&uri.AbsolutePath=="/events/event/reception","Generated QR stays on configured application and event path");Check(uri.Query==""&&uri.Fragment=="#code="+token,"Secret code is fragment-only never server query string");Check(EventQrImage.Link("https://example.test","e /&?",token).Contains("e%20%2F%26%3F"),"Event ID is URL encoded not route or query injection");
var png=EventQrImage.Png("https://example.test/","event",token);Check(png.Take(8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}),"QR image is an actual PNG generated without System.Drawing");Check(png.Length>500,"QR PNG contains encoded matrix image");
Directory.CreateDirectory("verification");File.WriteAllBytes("verification/event-qr-fixture.png",png);File.WriteAllText("verification/event-qr-fixture.txt",link);Console.WriteLine("Event QR image checks passed: "+passed);
