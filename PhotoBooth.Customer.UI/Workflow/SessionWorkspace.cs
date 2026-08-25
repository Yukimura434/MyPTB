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
            var files = (session.CapturedFiles ?? new string[0]).ToList();
            var ids = (session.CapturedImageIds ?? new string[0]).ToList();
            var keptFiles = new List<string>();
            var keptIds = new List<string>();
            for (var i = 0; i < files.Count; i++)
            {
                string replacement;
                if (promoted.TryGetValue(files[i], out replacement)) keptFiles.Add(replacement);
                else if (!Contains(session, files[i])) keptFiles.Add(files[i]);
                else continue;
                if (i < ids.Count) keptIds.Add(ids[i]);
            }
            session.CapturedFiles = keptFiles;
            session.CapturedImageIds = keptIds;
        }

        private static string EnsureTrailingSeparator(string path) =>
            path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? path
                : path + Path.DirectorySeparatorChar;
    }
}
