using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Customer.UI.Workflow
{
    internal static class SessionWorkspace
    {
        internal const string DirectoryName = "Session";

        internal static string GetPath(Session session)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.OutputDirectory))
                throw new InvalidOperationException("Session output directory is unavailable.");
            return Path.Combine(Path.GetFullPath(session.OutputDirectory), DirectoryName);
        }

        internal static void Prepare(Session session)
        {
            var path = GetPath(session);
            TryDelete(path);
            Directory.CreateDirectory(path);
        }

        internal static void Cleanup(Session session)
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

        internal static bool Contains(Session session, string file)
        {
            if (session == null || string.IsNullOrWhiteSpace(file)) return false;
            var root = EnsureTrailingSeparator(Path.GetFullPath(GetPath(session)));
            var path = Path.GetFullPath(file);
            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        internal static string Promote(Session session, string file)
        {
            if (!Contains(session, file)) return file;
            var destination = Path.Combine(Path.GetFullPath(session.OutputDirectory), Path.GetFileName(file));
            if (File.Exists(destination)) File.Delete(destination);
            File.Move(Path.GetFullPath(file), destination);
            return destination;
        }

        internal static void ReplaceWorkspaceFiles(Session session, IReadOnlyDictionary<string, string> promoted)
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
                kept.Add(new CapturedShot { Id=shot.Id, Sequence=shot.Sequence, PicturePath=picture, VideoPath=video, CapturedAtUtc=shot.CapturedAtUtc });
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
