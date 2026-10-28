using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using OpenTabletDriver.Plugin;
using Tmds.DBus.Protocol;

namespace RelativeArtist.ScreenCast
{
    /// <summary>
    /// A monitor stream handed out by the ScreenCast portal.
    /// </summary>
    public sealed record PortalStream(uint NodeId, Vector2 LogicalPosition, Vector2 LogicalSize);

    /// <summary>
    /// Minimal client for org.freedesktop.portal.ScreenCast: asks the user once for
    /// a monitor stream with cursor metadata, then reuses the restore token so later
    /// sessions start without a prompt.
    /// </summary>
    public sealed class PortalScreenCast : IAsyncDisposable
    {
        private const string PortalService = "org.freedesktop.portal.Desktop";
        private const string PortalPath = "/org/freedesktop/portal/desktop";
        private const string ScreenCastInterface = "org.freedesktop.portal.ScreenCast";
        private const uint SourceTypeMonitor = 1;
        private const uint CursorModeMetadata = 4;
        private const uint PersistUntilRevoked = 2;

        // Matches the opentabletdriver.desktop file OTD installs.
        private const string AppId = "opentabletdriver";

        private readonly DBusConnection connection;
        private string? sessionHandle;
        private int tokenCounter;

        private PortalScreenCast(DBusConnection connection)
        {
            this.connection = connection;
        }

        /// <summary>
        /// The token to pass next time to skip the selection dialog.
        /// </summary>
        public string? RestoreToken { get; private set; }

        public static async Task<PortalScreenCast> ConnectAsync()
        {
            var address = DBusAddress.Session ?? throw new InvalidOperationException("No D-Bus session bus");
            var connection = new DBusConnection(address);
            await connection.ConnectAsync();
            var portal = new PortalScreenCast(connection);
            await portal.RegisterAppAsync();
            return portal;
        }

        /// <summary>
        /// Tells the portal which app this is, so its dialog names OpenTabletDriver
        /// instead of an unidentified application. It has to come before any other
        /// portal call on the connection. Older portals lack the interface, which is
        /// harmless.
        /// </summary>
        private async Task RegisterAppAsync()
        {
            try
            {
                await connection.CallMethodAsync(CreateRegister(AppId));
            }
            catch (DBusErrorReplyException ex)
            {
                Log.Debug("RelativeArtist", $"Portal app registration unavailable: {ex.Message}");
            }
        }

        public async Task<PortalStream> StartAsync(string? restoreToken)
        {
            var created = await RequestAsync("CreateSession", token => new Dictionary<string, VariantValue>
            {
                ["handle_token"] = token,
                ["session_handle_token"] = NextToken()
            }, null, null);
            sessionHandle = created["session_handle"].GetString();

            var selectOptions = new Dictionary<string, VariantValue>
            {
                ["types"] = VariantValue.UInt32(SourceTypeMonitor),
                ["multiple"] = VariantValue.Bool(false),
                ["cursor_mode"] = VariantValue.UInt32(CursorModeMetadata),
                ["persist_mode"] = VariantValue.UInt32(PersistUntilRevoked)
            };
            if (!string.IsNullOrEmpty(restoreToken))
                selectOptions["restore_token"] = restoreToken;
            await RequestAsync("SelectSources", token =>
            {
                selectOptions["handle_token"] = token;
                return selectOptions;
            }, sessionHandle, null);

            var started = await RequestAsync("Start", token => new Dictionary<string, VariantValue>
            {
                ["handle_token"] = token
            }, sessionHandle, string.Empty);

            if (started.TryGetValue("restore_token", out var newToken))
                RestoreToken = newToken.GetString();

            var streams = started["streams"];
            if (streams.Count == 0)
                throw new InvalidOperationException("The portal returned no streams");

            var stream = streams.GetItem(0);
            var properties = stream.GetItem(1).GetDictionary<string, VariantValue>();
            var position = properties.TryGetValue("position", out var p)
                ? new Vector2(p.GetItem(0).GetInt32(), p.GetItem(1).GetInt32())
                : Vector2.Zero;
            var size = properties.TryGetValue("size", out var s)
                ? new Vector2(s.GetItem(0).GetInt32(), s.GetItem(1).GetInt32())
                : Vector2.Zero;

            return new PortalStream(stream.GetItem(0).GetUInt32(), position, size);
        }

        /// <summary>
        /// Opens the PipeWire remote for this session. The caller owns the descriptor.
        /// </summary>
        public async Task<SafeFileHandle> OpenPipeWireRemoteAsync()
        {
            var message = CreateCall("OpenPipeWireRemote",
                sessionHandle ?? throw new InvalidOperationException("No portal session"), null,
                new Dictionary<string, VariantValue>());
            return await connection.CallMethodAsync(message,
                (m, _) => m.GetBodyReader().ReadHandle<SafeFileHandle>());
        }

        /// <summary>
        /// Calls a portal method that answers through a Request object, waits for its
        /// Response signal and returns the results.
        /// </summary>
        private async Task<Dictionary<string, VariantValue>> RequestAsync(string method,
            Func<string, Dictionary<string, VariantValue>> options, string? session, string? parentWindow)
        {
            var token = NextToken();
            var sender = (connection.UniqueName ?? throw new InvalidOperationException("Not connected"))
                .TrimStart(':').Replace('.', '_');
            var requestPath = $"{PortalPath}/request/{sender}/{token}";

            // Subscribe before calling so a fast response can't be missed.
            var response = new TaskCompletionSource<(uint Code, Dictionary<string, VariantValue> Results)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var watch = await connection.WatchSignalAsync(null, requestPath, "org.freedesktop.portal.Request", "Response",
                (m, _) =>
                {
                    var reader = m.GetBodyReader();
                    return (reader.ReadUInt32(), reader.ReadDictionaryOfStringToVariantValue());
                },
                (Notification<(uint, Dictionary<string, VariantValue>)> notification) =>
                {
                    if (notification.HasValue)
                        response.TrySetResult(notification.Value);
                    else if (notification.IsCompletion && notification.Exception != null)
                        response.TrySetException(notification.Exception);
                },
                ObserverFlags.None, emitOnCapturedContext: false);

            await connection.CallMethodAsync(CreateCall(method, session, parentWindow, options(token)));

            var (code, results) = await response.Task;
            return code switch
            {
                0 => results,
                1 => throw new OperationCanceledException($"Screen cast {method} was cancelled"),
                _ => throw new InvalidOperationException($"Screen cast {method} failed (response {code})")
            };
        }

        // MessageWriter is a ref struct, so messages are built outside the async methods.
        private MessageBuffer CreateCall(string method, string? session, string? parentWindow,
            Dictionary<string, VariantValue> options)
        {
            var signature = (session != null ? "o" : "") + (parentWindow != null ? "s" : "") + "a{sv}";
            using var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(PortalService, PortalPath, ScreenCastInterface, method, signature);
            if (session != null)
                writer.WriteObjectPath(session);
            if (parentWindow != null)
                writer.WriteString(parentWindow);
            writer.WriteDictionary(options);
            return writer.CreateMessage();
        }

        private MessageBuffer CreateRegister(string appId)
        {
            using var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(PortalService, PortalPath, "org.freedesktop.host.portal.Registry", "Register", "sa{sv}");
            writer.WriteString(appId);
            writer.WriteDictionary(new Dictionary<string, VariantValue>());
            return writer.CreateMessage();
        }

        private MessageBuffer CreateSessionClose(string session)
        {
            using var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(PortalService, session, "org.freedesktop.portal.Session", "Close");
            return writer.CreateMessage();
        }

        private string NextToken() => $"relartist{Environment.ProcessId}_{++tokenCounter}";

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (sessionHandle != null)
                    await connection.CallMethodAsync(CreateSessionClose(sessionHandle));
            }
            catch (Exception)
            {
                // The session is gone with the connection anyway.
            }
            finally
            {
                connection.Dispose();
            }
        }
    }
}
