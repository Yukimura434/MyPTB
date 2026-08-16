using System.Collections.Generic;
using System.Linq;
using PhotoBooth.Core.Sessions;

namespace PhotoBooth.Database
{
    public sealed class InMemoryPhotoSessionRepository : IPhotoSessionRepository
    {
        private readonly List<PhotoSessionRecord> _sessions = new List<PhotoSessionRecord>();

        public void Save(PhotoSessionRecord session) => _sessions.Add(session);

        public IReadOnlyList<PhotoSessionRecord> GetAll() => _sessions.ToList();
    }
}
