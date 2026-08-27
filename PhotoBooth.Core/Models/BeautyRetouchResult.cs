namespace PhotoBooth.Core.Models
{
    public sealed class BeautyRetouchResult
    {
        public bool Applied { get; set; }
        public int FacesDetected { get; set; }
        public long ElapsedMilliseconds { get; set; }
    }
}
