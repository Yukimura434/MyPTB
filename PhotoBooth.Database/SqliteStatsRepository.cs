using System.Threading;using System.Threading.Tasks;using Microsoft.Data.Sqlite;using PhotoBooth.Core.Persistence;
namespace PhotoBooth.Database
{
 public sealed class SqliteStatsRepository:IStatsRepository
 {
  readonly SqliteDatabase db;public SqliteStatsRepository(SqliteDatabase db){this.db=db;}
  // CustomerSessions includes the reusable Base_session. A completed photo-booth
  // round is represented by Captures, independently of the camera that took it.
  public Task<long> CountSessionsAsync(CancellationToken t)=>Task.FromResult(Count("SELECT COUNT(*) FROM Captures"));
  // Count only the final original photos attached to a completed capture. This
  // excludes GIF/composite files and abandoned or replaced camera files.
  public Task<long> CountCapturedImagesAsync(CancellationToken t)=>Task.FromResult(Count("SELECT COUNT(*) FROM CapturePhotos WHERE PhotoType='Original'"));
  public Task<long> CountSuccessfulPrintsAsync(CancellationToken t)=>Task.FromResult(Count("SELECT COUNT(*) FROM PrintJobs WHERE Status='Success'"));
  long Count(string sql){using(var c=db.OpenConnection())using(var q=c.CreateCommand()){q.CommandText=sql;return (long)q.ExecuteScalar();}}
 }
}
