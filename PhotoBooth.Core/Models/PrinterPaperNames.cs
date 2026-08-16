using System;
using System.Linq;

namespace PhotoBooth.Core.Models
{
 public static class PrinterPaperNames
 {
  public static bool Match(string left,string right)
  {
   if(string.Equals(left,right,StringComparison.OrdinalIgnoreCase))return true;
   var a=Canonical(left);var b=Canonical(right);return !string.IsNullOrWhiteSpace(a)&&string.Equals(a,b,StringComparison.OrdinalIgnoreCase);
  }
  public static string Canonical(string value)
  {
   if(string.IsNullOrWhiteSpace(value))return null;var compact=new string(value.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
   for(var i=10;i>=0;i--){var key="A"+i;if(compact.StartsWith(key,StringComparison.Ordinal))return key;}
   if(compact.StartsWith("LETTER",StringComparison.Ordinal))return "LETTER";if(compact.StartsWith("LEGAL",StringComparison.Ordinal))return "LEGAL";
   if(compact.Contains("4X6")||compact.Contains("10X15"))return "4X6";if(compact.Contains("5X7")||compact.Contains("13X18"))return "5X7";return compact;
  }
 }
}
