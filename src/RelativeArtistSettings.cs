using System;
using OpenTabletDriver.Plugin;
using OpenTabletDriver.Plugin.Attributes;
using OpenTabletDriver.Plugin.Output;
using OpenTabletDriver.Plugin.Tablet;

namespace RelativeArtist
{
    /// <summary>
    /// Holds Relative Artist Mode's own settings. OTD's GUI only shows the stock
    /// Relative Mode editor for output modes, so these live on a filter instead,
    /// which the Filters tab does show. It sits at <see cref="PipelinePosition.None"/>,
    /// so OTD never puts it in the report path; the mode reads it from its element list.
    /// </summary>
    [PluginName("Relative Artist Mode Settings"), SupportedPlatform(PluginPlatform.Linux)]
    public class RelativeArtistSettings : IPositionedPipelineElement<IDeviceReport>
    {
        public const string CursorSyncNone = "None";
        public const string CursorSyncMouse = "Mouse tracking";
        public const string CursorSyncScreenCast = "Screen cast";

        public static string[] CursorSyncModes => new[] { CursorSyncNone, CursorSyncMouse, CursorSyncScreenCast };

        [BooleanProperty("Keep cursor visible when lifted",
            "Keep the pen reported as in range after it leaves the tablet, so the cursor and brush outline stay on screen.")]
        [DefaultPropertyValue(true)]
        public bool KeepInProximity { set; get; } = true;

        [Property("Cursor sync"), PropertyValidated(nameof(CursorSyncModes)), DefaultPropertyValue(CursorSyncNone)]
        [ToolTip("How the pen continues from where the mouse left the cursor.\n" +
            "Mouse tracking: reads mice from /dev/input; only stays in sync with pointer acceleration disabled (flat profile).\n" +
            "Screen cast: your desktop will show a screen sharing request when this is enabled. The plugin never looks at or " +
            "saves what's on your screen; screen sharing is the only standard way to read the true cursor position, and that " +
            "position is the only thing it uses. A screen sharing indicator stays visible while it is active.")]
        public string CursorSync { set; get; } = CursorSyncNone;

        [Property("Mouse speed multiplier"), DefaultPropertyValue(1f)]
        [ToolTip("Mouse tracking only: scale applied to mouse motion. With libinput's flat profile this is 1 + the pointer speed setting.")]
        public float MouseSpeed { set; get; } = 1f;

        public PipelinePosition Position => PipelinePosition.None;

        public event Action<IDeviceReport>? Emit;

        public void Consume(IDeviceReport value) => Emit?.Invoke(value);
    }
}
