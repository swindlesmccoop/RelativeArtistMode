using System;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using OpenTabletDriver.Plugin;

namespace RelativeArtist.ScreenCast
{
    /// <summary>
    /// Reports the compositor's real cursor position by screen casting a monitor
    /// through xdg-desktop-portal with cursor metadata. The user picks the monitor
    /// once; the portal's restore token is kept so later sessions start silently.
    /// </summary>
    public sealed class ScreenCastCursorSync : IDisposable
    {
        private static readonly string TokenPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OpenTabletDriver", "RelativeArtist", "screencast-restore-token");

        private readonly Action<Vector2> onCursor;
        private readonly object sync = new object();
        private PortalScreenCast? portal;
        private PipeWireCursorStream? stream;
        private bool disposed;

        /// <param name="onCursor">Called with the cursor position in logical desktop coordinates.</param>
        public ScreenCastCursorSync(Action<Vector2> onCursor)
        {
            this.onCursor = onCursor;
            _ = StartAsync();
        }

        private async Task StartAsync()
        {
            try
            {
                var screenCast = await PortalScreenCast.ConnectAsync();
                lock (sync)
                {
                    if (disposed)
                    {
                        _ = screenCast.DisposeAsync();
                        return;
                    }
                    portal = screenCast;
                }

                var source = await screenCast.StartAsync(ReadToken());
                if (screenCast.RestoreToken != null)
                    WriteToken(screenCast.RestoreToken);

                var fd = await screenCast.OpenPipeWireRemoteAsync();
                lock (sync)
                {
                    if (disposed)
                    {
                        fd.Dispose();
                        return;
                    }

                    stream = new PipeWireCursorStream((int)fd.DangerousGetHandle(), source.NodeId,
                        pixels => onCursor(ToLogical(source, pixels)));
                    // PipeWire owns and closes the descriptor now.
                    fd.SetHandleAsInvalid();
                }
                Log.Write("RelativeArtist", $"Syncing to the cursor through screen cast node {source.NodeId}");
            }
            catch (OperationCanceledException)
            {
                Log.Write("RelativeArtist", "Screen cast was declined; cursor sync is off", LogLevel.Warning);
            }
            catch (Exception ex)
            {
                Log.Write("RelativeArtist", $"Screen cast cursor sync failed: {ex.Message}", LogLevel.Error);
                Log.Exception(ex);
            }
        }

        /// <summary>
        /// Stream positions are in buffer pixels relative to the monitor; the portal
        /// reports the monitor's place and size in logical coordinates.
        /// </summary>
        private Vector2 ToLogical(PortalStream source, Vector2 pixels)
        {
            var streamSize = stream?.StreamSize ?? Vector2.Zero;
            var scale = streamSize.X > 0 && source.LogicalSize.X > 0
                ? source.LogicalSize / streamSize
                : Vector2.One;
            return source.LogicalPosition + pixels * scale;
        }

        private static string? ReadToken()
        {
            try
            {
                return File.Exists(TokenPath) ? File.ReadAllText(TokenPath).Trim() : null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        private static void WriteToken(string token)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(TokenPath)!);
                File.WriteAllText(TokenPath, token);
            }
            catch (IOException ex)
            {
                Log.Write("RelativeArtist", $"Could not save the screen cast restore token: {ex.Message}", LogLevel.Warning);
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed)
                    return;
                disposed = true;

                stream?.Dispose();
                stream = null;
                if (portal != null)
                    _ = portal.DisposeAsync().AsTask();
                portal = null;
            }
        }
    }
}
