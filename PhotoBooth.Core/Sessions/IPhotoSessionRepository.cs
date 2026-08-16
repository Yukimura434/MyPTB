using System.Collections.Generic;

namespace PhotoBooth.Core.Sessions
{
    public interface IPhotoSessionRepository
    {
        void Save(PhotoSessionRecord session);
        IReadOnlyList<PhotoSessionRecord> GetAll();
    }
}
