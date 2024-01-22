using JetBrains.Annotations;

namespace osu.Framework.Input.Handlers.Mouse
{
    [PublicAPI]
    public class GazePointData
    {
        public float X { get; set; }
        public float Y { get; set; }
        public bool Valid { get; set; }
        public string Timestamp { get; set; } = default!;

        public long TimestampNum => long.Parse(Timestamp);
    }
}
