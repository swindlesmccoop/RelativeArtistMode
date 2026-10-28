using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using OpenTabletDriver.Plugin;
using OpenTabletDriver.Plugin.Attributes;
using OpenTabletDriver.Plugin.DependencyInjection;
using OpenTabletDriver.Plugin.Output;
using OpenTabletDriver.Plugin.Platform.Display;
using OpenTabletDriver.Plugin.Platform.Pointer;
using OpenTabletDriver.Plugin.Tablet;
using RelativeArtist.ScreenCast;

namespace RelativeArtist
{
    /// <summary>
    /// Relative positioning with pressure on Linux. Motion is computed exactly
    /// like the stock Relative Mode, but the accumulated position is written to
    /// OTD's absolute virtual artist tablet, so libinput sees a real tablet tool
    /// (with pressure, tilt and eraser) instead of a mouse.
    /// </summary>
    [PluginName("Relative Artist Mode"), SupportedPlatform(PluginPlatform.Linux)]
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
    public class RelativeArtistMode : RelativeOutputMode
    {
        private RelativeArtistPointer? pointer;
        private RelativeArtistSettings settings = new RelativeArtistSettings();
        private bool settingsApplied;
        private MouseTracker? mouseTracker;
        private ScreenCastCursorSync? screenCastSync;

        [Resolved]
        public IPressureHandler? VirtualTablet { get; set; }

        [Resolved]
        public IVirtualScreen? VirtualScreen { get; set; }

        public override IRelativePointer Pointer
        {
            set => throw new NotSupportedException();
            get => pointer ??= new RelativeArtistPointer(
                VirtualTablet ?? throw new InvalidOperationException("Virtual artist tablet is unavailable"),
                VirtualScreen ?? throw new InvalidOperationException("Virtual screen is unavailable"))
            {
                KeepInProximity = settings.KeepInProximity
            };
        }

        public override void Read(IDeviceReport deviceReport)
        {
            // OTD hands the mode its filters after constructing it, so the settings
            // filter can only be picked up once reports start arriving.
            if (!settingsApplied)
                ApplySettings();

            base.Read(deviceReport);
        }

        private void ApplySettings()
        {
            settingsApplied = true;
            settings = Elements?.OfType<RelativeArtistSettings>().FirstOrDefault() ?? settings;

            var target = (RelativeArtistPointer)Pointer;
            target.KeepInProximity = settings.KeepInProximity;

            if (settings.CursorSync == RelativeArtistSettings.CursorSyncMouse)
                mouseTracker = new MouseTracker(delta => target.MoveBy(delta * settings.MouseSpeed));
            else if (settings.CursorSync == RelativeArtistSettings.CursorSyncScreenCast)
                screenCastSync = new ScreenCastCursorSync(target.SyncToCursor);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                mouseTracker?.Dispose();
                mouseTracker = null;
                screenCastSync?.Dispose();
                screenCastSync = null;
            }
            base.Dispose(disposing);
        }
    }
}
