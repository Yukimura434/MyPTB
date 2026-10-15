using System;using System.Collections.Generic;using System.Linq;using System.Threading;using System.Threading.Tasks;using PhotoBooth.Core.Models;using PhotoBooth.Core.Persistence;
namespace PhotoBooth.Database
{
 public sealed class SqliteFrameRepository:IFrameRepository
 {
        public Task ReplacePinnedAsync(IReadOnlyCollection<Guid> ids, CancellationToken token)
        {
            if (ids == null) throw new ArgumentNullException(nameof(ids));
            token.ThrowIfCancellationRequested();
            using (var connection = db.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                // Clear legacy collection unions in the same commit as all pin changes.
                command.CommandText = "UPDATE Frames SET IsPinned=0; UPDATE WorkflowSettings SET SelectedFrameCollections='' WHERE Id=1";
                command.ExecuteNonQuery();
                command.CommandText = "UPDATE Frames SET IsPinned=1 WHERE Id=$id";
                var parameter = command.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Text);
                foreach (var id in ids.Distinct())
                {
                    token.ThrowIfCancellationRequested();
                    parameter.Value = id.ToString();
                    if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Mục đã bị xóa. Vui lòng tải lại thư viện.");
                }
                token.ThrowIfCancellationRequested();
                transaction.Commit();
            }
            return Task.CompletedTask;
        }

  readonly SqliteDatabase db;public SqliteFrameRepository(SqliteDatabase db){this.db=db;}
  public async Task<Frame> GetAsync(Guid id,CancellationToken t)=>(await GetAllAsync(t)).FirstOrDefault(x=>x.Id==id);
  public Task<IReadOnlyList<Frame>> GetAllAsync(CancellationToken t)
  {
   t.ThrowIfCancellationRequested();var list=new List<Frame>();var legacy=new Dictionary<Guid,string>();
   using(var c=db.OpenConnection())using(var q=c.CreateCommand()){q.CommandText="SELECT Id,Name,SourcePath,PixelWidth,PixelHeight,Slots,ThumbnailPath,IsPinned,CreatedAtUtc,EventId,QrEnabled,QrX,QrY,QrSize FROM Frames";using(var r=q.ExecuteReader())while(r.Read()){t.ThrowIfCancellationRequested();var f=new Frame{Id=Guid.Parse(r.GetString(0)),Name=r.GetString(1),SourcePath=r.IsDBNull(2)?null:r.GetString(2),PixelWidth=r.GetInt32(3),PixelHeight=r.GetInt32(4),ThumbnailPath=r.IsDBNull(6)?null:r.GetString(6),IsPinned=r.GetInt32(7)!=0,CreatedAtUtc=DateTime.Parse(r.GetString(8)).ToUniversalTime(),EventId=r.IsDBNull(9)?(Guid?)null:Guid.Parse(r.GetString(9))};f.PrintedQr=new FrameQrSettings{Enabled=r.GetInt32(10)!=0,X=r.GetDouble(11),Y=r.GetDouble(12),Size=r.GetDouble(13)}.Normalize(f.PixelWidth,f.PixelHeight);legacy[f.Id]=r.IsDBNull(5)?null:r.GetString(5);list.Add(f);}}
   var slots=LoadAllSlots(t);foreach(var frame in list){List<FrameSlot> normalized;frame.Slots=slots.TryGetValue(frame.Id,out normalized)&&normalized.Count>0?normalized:Parse(legacy[frame.Id]);}
   return Task.FromResult<IReadOnlyList<Frame>>(list);
  }
  public Task SaveAsync(Frame f,CancellationToken t){using(var c=db.OpenConnection())using(var tx=c.BeginTransaction()){using(var q=c.CreateCommand()){q.Transaction=tx;q.CommandText="INSERT INTO Frames (Id,Name,SourcePath,PixelWidth,PixelHeight,Slots,ThumbnailPath,IsPinned,CreatedAtUtc,EventId,QrEnabled,QrX,QrY,QrSize) VALUES($id,$name,$path,$w,$h,NULL,$thumb,$pin,$created,$event,$qe,$qx,$qy,$qs) ON CONFLICT(Id) DO UPDATE SET Name=excluded.Name,SourcePath=excluded.SourcePath,PixelWidth=excluded.PixelWidth,PixelHeight=excluded.PixelHeight,Slots=excluded.Slots,ThumbnailPath=excluded.ThumbnailPath,IsPinned=excluded.IsPinned,CreatedAtUtc=excluded.CreatedAtUtc,EventId=excluded.EventId,QrEnabled=excluded.QrEnabled,QrX=excluded.QrX,QrY=excluded.QrY,QrSize=excluded.QrSize";var qr=(f.PrintedQr??new FrameQrSettings()).Normalize(f.PixelWidth,f.PixelHeight);q.Parameters.AddWithValue("$qe",qr.Enabled?1:0);q.Parameters.AddWithValue("$qx",qr.X);q.Parameters.AddWithValue("$qy",qr.Y);q.Parameters.AddWithValue("$qs",qr.Size);q.Parameters.AddWithValue("$id",f.Id.ToString());q.Parameters.AddWithValue("$name",f.Name);q.Parameters.AddWithValue("$path",(object)f.SourcePath??DBNull.Value);q.Parameters.AddWithValue("$w",f.PixelWidth);q.Parameters.AddWithValue("$h",f.PixelHeight);q.Parameters.AddWithValue("$thumb",(object)f.ThumbnailPath??DBNull.Value);q.Parameters.AddWithValue("$pin",f.IsPinned?1:0);q.Parameters.AddWithValue("$created",f.CreatedAtUtc.ToString("O"));q.Parameters.AddWithValue("$event",f.EventId.HasValue?(object)f.EventId.Value.ToString():DBNull.Value);q.ExecuteNonQuery();}using(var d=c.CreateCommand()){d.Transaction=tx;d.CommandText="DELETE FROM FrameSlots WHERE FrameId=$id";d.Parameters.AddWithValue("$id",f.Id.ToString());d.ExecuteNonQuery();}foreach(var s in f.Slots??new FrameSlot[0])using(var q=c.CreateCommand()){q.Transaction=tx;q.CommandText="INSERT INTO FrameSlots (Id,FrameId,SlotIndex,X,Y,Width,Height) VALUES($id,$frame,$index,$x,$y,$w,$h)";q.Parameters.AddWithValue("$id",(s.Id==Guid.Empty?Guid.NewGuid():s.Id).ToString());q.Parameters.AddWithValue("$frame",f.Id.ToString());q.Parameters.AddWithValue("$index",s.Index);q.Parameters.AddWithValue("$x",s.X);q.Parameters.AddWithValue("$y",s.Y);q.Parameters.AddWithValue("$w",s.Width);q.Parameters.AddWithValue("$h",s.Height);q.ExecuteNonQuery();}tx.Commit();}return Task.CompletedTask;}
  public Task DeleteAsync(Guid id,CancellationToken t){using(var c=db.OpenConnection())using(var q=c.CreateCommand()){q.CommandText="DELETE FROM Frames WHERE Id=$id";q.Parameters.AddWithValue("$id",id.ToString());q.ExecuteNonQuery();}return Task.CompletedTask;}
  Dictionary<Guid,List<FrameSlot>> LoadAllSlots(CancellationToken token){var result=new Dictionary<Guid,List<FrameSlot>>();using(var c=db.OpenConnection())using(var q=c.CreateCommand()){q.CommandText="SELECT FrameId,Id,SlotIndex,X,Y,Width,Height FROM FrameSlots ORDER BY FrameId,SlotIndex";using(var r=q.ExecuteReader())while(r.Read()){token.ThrowIfCancellationRequested();var frameId=Guid.Parse(r.GetString(0));List<FrameSlot> slots;if(!result.TryGetValue(frameId,out slots)){slots=new List<FrameSlot>();result[frameId]=slots;}slots.Add(new FrameSlot{Id=Guid.Parse(r.GetString(1)),Index=r.GetInt32(2),X=r.GetInt32(3),Y=r.GetInt32(4),Width=r.GetInt32(5),Height=r.GetInt32(6)});}}return result;}
  static IReadOnlyList<FrameSlot> Parse(string text){if(string.IsNullOrWhiteSpace(text))return new List<FrameSlot>();return text.Split(';').Select(x=>x.Split(',')).Select(x=>new FrameSlot{Id=Guid.NewGuid(),Index=int.Parse(x[0]),X=int.Parse(x[1]),Y=int.Parse(x[2]),Width=int.Parse(x[3]),Height=int.Parse(x[4])}).ToList();}
 }
}
