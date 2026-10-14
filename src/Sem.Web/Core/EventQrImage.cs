using QRCoder;
namespace Sem.Web.Core;
public static class EventQrImage {
 public static string Link(string publicUrl,string eventId,string token)=>new Uri(new Uri(publicUrl.TrimEnd('/')+"/"),"events/"+Uri.EscapeDataString(eventId)+"/reception#code="+Uri.EscapeDataString(token)).AbsoluteUri;
 public static byte[] Png(string publicUrl,string eventId,string token){using var data=QRCodeGenerator.GenerateQrCode(Link(publicUrl,eventId,token),QRCodeGenerator.ECCLevel.M);using var image=new PngByteQRCode(data);return image.GetGraphic(8);}
}
