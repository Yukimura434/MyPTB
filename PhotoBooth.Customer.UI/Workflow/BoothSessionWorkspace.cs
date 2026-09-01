using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Customer.UI.Workflow
{
    internal static class BoothSessionWorkspace
    {
        internal static string GetPath(BoothSession session)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.OutputDirectory))
                throw new InvalidOperationException("Booth-session output directory is unavailable.");
            if (!session.IsBoothSession)
                throw new InvalidOperationException("A customer workspace requires a BoothSession, not an Event.");
            return Path.Combine(Path.GetFullPath(session.OutputDirectory), "Work");
        }

        internal static void Prepare(BoothSession session)
        {
            var path = GetPath(session);
            TryDelete(path);
            Directory.CreateDirectory(path);
        }

        internal static void Cleanup(BoothSession session)
        {
            if (session == null) return;
            var path = GetPath(session);
            TryDelete(path);
        }

        static void TryDelete(string path)
        {
            if (!Directory.Exists(path)) return;
            try { Directory.Delete(path, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        internal static bool Contains(BoothSession session, string file)
        {
            if (session == null || string.IsNullOrWhiteSpace(file)) return false;
            var root = EnsureTrailingSeparator(Path.GetFullPath(GetPath(session)));
            var path = Path.GetFullPath(file);
            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        internal static string Promote(BoothSession session, string file)
        {
            return Promote(session,file,"Originals");
        }

        internal static string PromoteOriginal(BoothSession session,string file) => Promote(session,file,"Originals");
        internal static string PromoteFinal(BoothSession session,string file) => Promote(session,file,"Final");

        static string Promote(BoothSession session, string file,string area)
        {
            if (!Contains(session, file)) return file;
            var destinationDirectory=Path.Combine(Path.GetFullPath(session.OutputDirectory),area);
            Directory.CreateDirectory(destinationDirectory);
            var destination = Path.Combine(destinationDirectory, Path.GetFileName(file));
            if(string.Equals(Path.GetFullPath(file),Path.GetFullPath(destination),StringComparison.OrdinalIgnoreCase))return destination;
            if (File.Exists(destination)) File.Delete(destination);
            File.Move(Path.GetFullPath(file), destination);
            return destination;
        }

        internal static void ReplaceWorkspaceFiles(BoothSession session, IReadOnlyDictionary<string, string> promoted)
        {
            var kept = new List<CapturedShot>();
            foreach (var shot in session.CapturedShots ?? new CapturedShot[0])
            {
                string picture;
                if (promoted.TryGetValue(shot.PicturePath, out picture)) { }
                else if (!Contains(session, shot.PicturePath)) picture = shot.PicturePath;
                else continue;
                var video = shot.VideoPath;
                string videoReplacement;
                if (!string.IsNullOrWhiteSpace(video) && promoted.TryGetValue(video, out videoReplacement)) video = videoReplacement;
                else if (!string.IsNullOrWhiteSpace(video) && Contains(session, video)) video = null;
                kept.Add(new CapturedShot { Id=shot.Id, Sequence=shot.Sequence, PicturePath=picture, VideoPath=video, PictureAssetId=shot.PictureAssetId, VideoAssetId=shot.VideoAssetId, CapturedAtUtc=shot.CapturedAtUtc });
            }
            session.CapturedShots = kept;
            session.CapturedFiles = kept.Select(x=>x.PicturePath).ToList();
            session.CapturedVideoFiles = kept.Where(x=>x.HasVideo).Select(x=>x.VideoPath).ToList();
            session.CapturedImageIds = kept.Select(x=>x.Id).ToList();
        }

        private static string EnsureTrailingSeparator(string path) =>
            path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? path
                : path + Path.DirectorySeparatorChar;
    }
}
