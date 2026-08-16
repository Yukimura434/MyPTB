using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;
using PhotoBooth.Database;
using Xunit;

namespace PhotoBooth.UnitTests
{
 public sealed class PrinterProfileRepositoryTests
 {
  [Theory]
  [InlineData("A5 (148 x 210 mm)","A5 148x210mm")]
  [InlineData("4 x 6 in","10 x 15 cm (4 x 6 in)")]
  public void Driver_paper_names_are_matched_by_canonical_size(string saved,string current)
  {
   Assert.True(PrinterPaperNames.Match(saved,current));
  }
  [Fact]
  public async Task Saved_profile_is_found_again_by_printer_id_and_preserves_color()
  {
   var root=Path.Combine(Path.GetTempPath(),"photobooth-printer-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
   try
   {
    var db=new SqliteDatabase(Path.Combine(root,"test.db"));db.Initialize();var repository=new SqlitePrinterProfileRepository(db);
    var profile=new PrinterProfile{Id=Guid.NewGuid(),Name="Brother",PrinterName="Brother DCP-T220 Printer",PrinterId="BROTHER|USB001|DRIVER",PaperSize="A4",DefaultCopies=1,PrintInColor=false,IsDefault=true};
    await repository.SaveAsync(profile,CancellationToken.None);
    var loaded=await repository.GetByPrinterIdAsync(profile.PrinterId,CancellationToken.None);
    Assert.NotNull(loaded);Assert.Equal(profile.Id,loaded.Id);Assert.False(loaded.PrintInColor);Assert.True(loaded.IsDefault);
   }
   finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
  }
 }
}
