using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using JetBrains.Annotations;
using Newtonsoft.Json;
using osu.Framework.Platform;
using osuTK;
using osuTK.Input;

namespace osu.Framework.Input.Handlers.Mouse
{
    [PublicAPI]
    public class UdpGazePointDataHandler
    {
        public static UdpGazePointDataHandler Instance = new UdpGazePointDataHandler(new IPEndPoint(IPAddress.Loopback, 8052));

        public event Action<Vector2>? AbsolutePositionChanged;
        public event Action<MouseButton>? DragStarted;
        public event Action<MouseButton>? DragEnded;

        private readonly UdpClient server;

        private const uint min_blink_time = 150; //ms
        private const uint max_blink_time = 600; //ms
        private const uint max_time_between_blinks = 400;
        private const uint frames_after_blinking = 16; // 10 - for menu is ok, but for gameplay it is too much
        private const uint frozen_frames_after_blinking = 5;
        private const uint averaged_frames_after_blinking = frames_after_blinking - frozen_frames_after_blinking; // 10 - for menu is ok, but for gameplay it is too much
        private const double old_post_coef_start = 0.80;
        private const double new_pos_coef_start = 1 - old_post_coef_start;
        private const double coef_delta = old_post_coef_start / averaged_frames_after_blinking;

        private Rectangle bounds;

        private long lastTimestamp;

        private bool hasBlinkedOnce;
        private bool hasBlinkedTwice;
        private long lastBlinkTimestamp;

        private int framesAfterBlinkingCounter;
        private Vector2 oldPosition = new Vector2(0, 0);
        private Vector2 preBlinkPosition = new Vector2(0, 0);
        private bool isAfterBlink;

        private FileStream fout;

        public UdpGazePointDataHandler(IPEndPoint endpoint)
        {
            server = new UdpClient(endpoint);
            fout = new FileStream("./osu-eye-tracker-debug.log", FileMode.OpenOrCreate);
        }

        public void Initialize(GameHost gameHost)
        {
            bounds = gameHost.Window.PrimaryDisplay.Bounds;
            fout.Write(Encoding.ASCII.GetBytes($"Set up bounds: {bounds}\n"));
        }

        public void Receive()
        {
            while (true)
            {
                var sender = new IPEndPoint(IPAddress.Any, 0);
                byte[] data = server.Receive(ref sender);
                string stringData = Encoding.ASCII.GetString(data);
                var decodedData = JsonConvert.DeserializeObject<GazePointData>(stringData);

                if (decodedData == null || !decodedData.Valid)
                {
                    fout.Write(Encoding.ASCII.GetBytes("Skipping invalid data.\n"));
                    continue;
                }

                if (decodedData.TimestampNum < lastTimestamp)
                {
                    fout.Write(Encoding.ASCII.GetBytes($"Skipping too old data: {decodedData.TimestampNum} < {lastTimestamp}.\n"));
                    continue;
                }

                long dt = decodedData.TimestampNum - lastTimestamp;
                var measuredPosition = new Vector2(decodedData.X * bounds.Width + bounds.Left, decodedData.Y * bounds.Height + bounds.Top);

                if (dt > min_blink_time && dt < max_blink_time)
                {
                    if (hasBlinkedTwice)
                    {
                        // second blink happened - start draging
                        DragEnded?.Invoke(MouseButton.Left);
                        hasBlinkedTwice = false;
                    }
                    else if (hasBlinkedOnce)
                    {
                        // start click&drag
                        DragStarted?.Invoke(MouseButton.Left);
                        hasBlinkedTwice = true;
                        hasBlinkedOnce = false;
                    }
                    else
                    {
                        // click down
                        DragStarted?.Invoke(MouseButton.Left);
                        hasBlinkedOnce = true;
                        lastBlinkTimestamp = decodedData.TimestampNum;
                    }

                    isAfterBlink = true;
                    preBlinkPosition = oldPosition;
                    framesAfterBlinkingCounter = 0;
                }
                // blink did not happen
                else if (hasBlinkedOnce && decodedData.TimestampNum - lastBlinkTimestamp >= max_time_between_blinks)
                {
                    DragEnded?.Invoke(MouseButton.Left);
                    hasBlinkedOnce = false;
                }

                lastTimestamp = decodedData.TimestampNum;

                if (isAfterBlink && framesAfterBlinkingCounter < frames_after_blinking)
                {
                    fout.Write(Encoding.ASCII.GetBytes($"Waiting for blink: {decodedData.TimestampNum} - {lastBlinkTimestamp} >= {frames_after_blinking}.\n"));

                    if (framesAfterBlinkingCounter > frozen_frames_after_blinking)
                    {
                        long averagedFramesAfterBlinkingCounter = framesAfterBlinkingCounter - frozen_frames_after_blinking;
                        double newPosCoef = new_pos_coef_start + averagedFramesAfterBlinkingCounter * coef_delta;
                        double oldPostCoef = old_post_coef_start - averagedFramesAfterBlinkingCounter * coef_delta;

                        oldPosition = new Vector2(
                            (float)(newPosCoef * measuredPosition.X + oldPostCoef * preBlinkPosition.X),
                            (float)(newPosCoef * measuredPosition.Y + oldPostCoef * preBlinkPosition.Y)
                        );
                    }

                    framesAfterBlinkingCounter++;
                }
                else
                {
                    isAfterBlink = false;
                    oldPosition = measuredPosition;
                }

                fout.Write(Encoding.ASCII.GetBytes($"Position: {oldPosition}\n"));
                AbsolutePositionChanged?.Invoke(oldPosition);
            }
        }
    }
}
