using System;
using System.Diagnostics;
using System.Numerics;
using OpenTabletDriver.Plugin.Platform.Display;
using OpenTabletDriver.Plugin.Platform.Pointer;

namespace RelativeArtist
{
    /// <summary>
    /// Turns relative deltas into an absolute position on the virtual screen and
    /// forwards everything else to the underlying absolute tablet.
    /// </summary>
    public class RelativeArtistPointer : IRelativePointer, IPenActionHandler, IPressureHandler, ITiltHandler,
        IEraserHandler, IHoverDistanceHandler, ISynchronousPointer
    {
        private readonly IPressureHandler tablet;
        private readonly IVirtualScreen screen;
        private readonly object sync = new object();
        private Vector2? position;
        private float pressure;
        private bool isEraser;
        private bool proximityTimerDisarmed;
        private Vector2? cursor;
        private long lastPenReport;

        /// <summary>
        /// A pause in pen reports longer than this means the pen left the tablet,
        /// so the next report continues from the real cursor if it is known.
        /// </summary>
        private static readonly TimeSpan CursorSyncGap = TimeSpan.FromMilliseconds(100);

        public RelativeArtistPointer(IPressureHandler tablet, IVirtualScreen screen)
        {
            if (tablet is not IAbsolutePointer)
                throw new ArgumentException("Virtual tablet is not an absolute pointer", nameof(tablet));

            this.tablet = tablet;
            this.screen = screen;
        }

        /// <summary>
        /// When set, lifting the pen out of range only releases the tip and
        /// buttons instead of sending the tool out of proximity, so the
        /// compositor keeps showing the cursor.
        /// </summary>
        public bool KeepInProximity { get; set; }

        public Vector2 Position => position ?? new Vector2(screen.Width / 2f, screen.Height / 2f);

        public void SetPosition(Vector2 delta)
        {
            var now = Stopwatch.GetTimestamp();
            lock (sync)
            {
                if (cursor is { } known && (lastPenReport == 0 || Stopwatch.GetElapsedTime(lastPenReport, now) > CursorSyncGap))
                    position = known;
                lastPenReport = now;
            }

            var pos = MoveBy(delta);
            ((IAbsolutePointer)tablet).SetPosition(pos);

            if (KeepInProximity && !proximityTimerDisarmed && pressure == 0 && !isEraser)
                DisarmProximityTimer(pos);
        }

        /// <summary>
        /// Moves the stored position without emitting anything. Used both for pen
        /// motion and for mouse motion picked up by <see cref="MouseTracker"/>,
        /// so the pen continues from wherever the cursor was left.
        /// </summary>
        public Vector2 MoveBy(Vector2 delta)
        {
            lock (sync)
            {
                var pos = Vector2.Clamp(Position + delta, Vector2.Zero, new Vector2(screen.Width - 1, screen.Height - 1));
                position = pos;
                return pos;
            }
        }

        /// <summary>
        /// Records where the compositor's cursor really is, as reported by
        /// <see cref="ScreenCast.ScreenCastCursorSync"/>. It is picked up the next
        /// time the pen comes back after a pause, so mouse motion in between is
        /// honoured exactly.
        /// </summary>
        public void SyncToCursor(Vector2 cursorPosition)
        {
            lock (sync)
                cursor = cursorPosition;
        }

        /// <summary>
        /// libinput forces a proximity out after 50 ms without events on any pen
        /// that has never sent BTN_TOOL_PEN 0 itself, which would hide the cursor
        /// whenever the pen is lifted. A single real proximity out unloads that
        /// timer for the lifetime of the device, so pulse the tool out and back in
        /// once while hovering.
        /// </summary>
        private void DisarmProximityTimer(Vector2 pos)
        {
            if (tablet is not IEraserHandler eraserHandler || tablet is not ISynchronousPointer synchronousPointer)
                return;

            synchronousPointer.Flush();
            // OTD's virtual tablet releases the current tool and syncs on a tool switch.
            eraserHandler.SetEraser(true);
            eraserHandler.SetEraser(false);
            ((IAbsolutePointer)tablet).SetPosition(pos);
            proximityTimerDisarmed = true;
        }

        public void SetPressure(float percentage)
        {
            pressure = percentage;
            tablet.SetPressure(percentage);
        }

        public void SetTilt(Vector2 tilt) => (tablet as ITiltHandler)?.SetTilt(tilt);

        public void SetEraser(bool isEraser)
        {
            this.isEraser = isEraser;
            (tablet as IEraserHandler)?.SetEraser(isEraser);
        }

        public void SetHoverDistance(uint distance) => (tablet as IHoverDistanceHandler)?.SetHoverDistance(distance);

        public void Activate(PenAction action) => (tablet as IPenActionHandler)?.Activate(action);

        public void Deactivate(PenAction action) => (tablet as IPenActionHandler)?.Deactivate(action);

        public void Reset()
        {
            if (!KeepInProximity)
            {
                (tablet as ISynchronousPointer)?.Reset();
                return;
            }

            tablet.SetPressure(0);
            if (tablet is IPenActionHandler penActionHandler)
            {
                penActionHandler.Deactivate(PenAction.BarrelButton1);
                penActionHandler.Deactivate(PenAction.BarrelButton2);
                penActionHandler.Deactivate(PenAction.BarrelButton3);
            }
        }

        public void Flush() => (tablet as ISynchronousPointer)?.Flush();
    }
}
