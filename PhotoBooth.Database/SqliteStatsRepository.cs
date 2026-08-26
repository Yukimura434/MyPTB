using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;
namespace PhotoBooth.Database
{
 public sealed class SqliteStatsRepository:IStatsRepository
 {
  readonly SqliteDatabase db;public SqliteStatsRepository(SqliteDatabase db){this.db=db;}
  public Task<long> CountSessionsAsync(CancellationToken t)=>Task.FromResult(Count("SELECT COUNT(*) FROM Captures"));
  public Task<long> CountCapturedImagesAsync(CancellationToken t)=>Task.FromResult(Count("SELECT COUNT(*) FROM CapturePhotos WHERE PhotoType IN ('Picture','MotionPhoto','MotionPhotoComposite','Original')"));
  public Task<long> CountSuccessfulPrintsAsync(CancellationToken t)=>Task.FromResult(Count("SELECT COUNT(*) FROM PrintJobs WHERE Status='Success'"));
  public Task<DataStatisticsSnapshot> GetDataStatisticsAsync(CancellationToken token)
  {
   token.ThrowIfCancellationRequested();using(var c=db.OpenConnection()){var today=DateTime.Today.ToUniversalTime().ToString("O");return Task.FromResult(new DataStatisticsSnapshot{GeneratedAtUtc=DateTime.UtcNow,
    SessionCount=Scalar(c,"SELECT COUNT(*) FROM CustomerSessions WHERE SessionName IS NULL OR SessionName<>'Base_session'"),CaptureCount=Scalar(c,"SELECT COUNT(*) FROM Captures"),PictureCount=Scalar(c,"SELECT COUNT(*) FROM CapturePhotos WHERE PhotoType IN ('Picture','Original')"),MotionPhotoCount=Scalar(c,"SELECT COUNT(*) FROM CapturePhotos WHERE PhotoType IN ('MotionPhoto','MotionPhotoComposite')"),CompositeCount=Scalar(c,"SELECT COUNT(*) FROM CapturePhotos WHERE PhotoType='Composite'"),GifCount=Scalar(c,"SELECT COUNT(*) FROM CapturePhotos WHERE PhotoType='Gif'"),ShareArchiveCount=Scalar(c,"SELECT COUNT(*) FROM CapturePhotos WHERE PhotoType='ShareArchive'"),ReadyAssetCount=Scalar(c,"SELECT COUNT(*) FROM CapturePhotos WHERE AssetStatus='Ready'"),MissingAssetCount=Scalar(c,"SELECT COUNT(*) FROM CapturePhotos WHERE AssetStatus='Missing'"),SuccessfulPrintCount=Scalar(c,"SELECT COUNT(*) FROM PrintJobs WHERE Status='Success'"),FailedPrintCount=Scalar(c,"SELECT COUNT(*) FROM PrintJobs WHERE Status='Failed'"),PendingUploadCount=Scalar(c,"SELECT COUNT(*) FROM UploadQueue WHERE Status IN ('Pending','Uploading','RetryWaiting')"),UploadedCount=Scalar(c,"SELECT COUNT(*) FROM UploadQueue WHERE Status='Uploaded'"),FailedUploadCount=Scalar(c,"SELECT COUNT(*) FROM UploadQueue WHERE Status='PermanentFailure'"),TodayCaptureCount=Scalar(c,"SELECT COUNT(*) FROM Captures WHERE CreatedAtUtc>=$today",today),TodayPictureCount=Scalar(c,"SELECT COUNT(*) FROM CapturePhotos WHERE PhotoType IN ('Picture','MotionPhoto','MotionPhotoComposite','Original') AND CreatedAtUtc>=$today",today),TodayPrintCount=Scalar(c,"SELECT COUNT(*) FROM PrintJobs WHERE Status='Success' AND PrintedAtUtc>=$today",today),TotalAssetBytes=Scalar(c,"SELECT COALESCE(SUM(FileLength),0) FROM CapturePhotos"),RecentCaptures=LoadRecent(c,token)});}
  }
  static IReadOnlyList<RecentCaptureStatistics> LoadRecent(SqliteConnection c,CancellationToken token){var values=new List<RecentCaptureStatistics>();using(var q=c.CreateCommand()){q.CommandText=@"SELECT c.Id,c.SessionId,COALESCE(s.SessionName,'—'),c.CreatedAtUtc,c.Status,COUNT(a.Id),SUM(CASE WHEN a.PhotoType='MotionPhoto' THEN 1 ELSE 0 END),SUM(CASE WHEN a.PhotoType='Gif' THEN 1 ELSE 0 END),(SELECT COUNT(*) FROM PrintJobs p WHERE p.CaptureId=c.Id),SUM(CASE WHEN a.AssetStatus='Missing' THEN 1 ELSE 0 END),MAX(CASE WHEN a.PhotoType='ShareArchive' THEN 1 ELSE 0 END) FROM Captures c LEFT JOIN CustomerSessions s ON s.Id=c.SessionId LEFT JOIN CapturePhotos a ON a.CaptureId=c.Id GROUP BY c.Id,c.SessionId,s.SessionName,c.CreatedAtUtc,c.Status ORDER BY c.CreatedAtUtc DESC LIMIT 50";using(var r=q.ExecuteReader())while(r.Read()){token.ThrowIfCancellationRequested();values.Add(new RecentCaptureStatistics{CaptureId=r.GetString(0),SessionId=Guid.Parse(r.GetString(1)),SessionName=r.GetString(2),CreatedAtUtc=DateTime.Parse(r.GetString(3)).ToUniversalTime(),Status=r.GetString(4),AssetCount=r.GetInt32(5),MotionPhotoCount=r.IsDBNull(6)?0:r.GetInt32(6),GifCount=r.IsDBNull(7)?0:r.GetInt32(7),PrintCount=r.GetInt32(8),MissingAssetCount=r.IsDBNull(9)?0:r.GetInt32(9),HasShareArchive=!r.IsDBNull(10)&&r.GetInt32(10)>0});}}return values;}
  long Count(string sql){using(var c=db.OpenConnection())return Scalar(c,sql);}static long Scalar(SqliteConnection c,string sql,string today=null){using(var q=c.CreateCommand()){q.CommandText=sql;if(today!=null)q.Parameters.AddWithValue("$today",today);return Convert.ToInt64(q.ExecuteScalar());}}
 }
}
