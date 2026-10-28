using System;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OpenTabletDriver.Plugin;

namespace RelativeArtist.ScreenCast
{
    /// <summary>
    /// Connects to a portal screen cast node and reads only the cursor metadata
    /// from it. The video data itself is never mapped or touched.
    /// </summary>
    public sealed unsafe class PipeWireCursorStream : IDisposable
    {
        private const string LibPipeWire = "libpipewire-0.3.so.0";

        // spa/utils/type.h (counted from SPA_TYPE_START = 0), spa/param/*.h, spa/buffer/meta.h
        private const uint SPA_TYPE_Id = 3;
        private const uint SPA_TYPE_Int = 4;
        private const uint SPA_TYPE_Object = 15;
        private const uint SPA_TYPE_Choice = 19;
        private const uint SPA_TYPE_OBJECT_ParamMeta = 0x40005;
        private const uint SPA_PARAM_Format = 4;
        private const uint SPA_PARAM_Meta = 6;
        private const uint SPA_PARAM_META_type = 1;
        private const uint SPA_PARAM_META_size = 2;
        private const uint SPA_CHOICE_Range = 1;
        private const uint SPA_FORMAT_VIDEO_modifier = 0x20002;
        private const uint SPA_FORMAT_VIDEO_size = 0x20003;
        private const uint SPA_META_Cursor = 5;
        private const uint PW_DIRECTION_INPUT = 0;
        private const uint PW_STREAM_FLAG_AUTOCONNECT = 1 << 0;

        private static bool resolverInstalled;

        private readonly Action<Vector2> onCursor;
        private readonly GCHandle self;
        private readonly IntPtr threadLoop;
        private readonly IntPtr context;
        private readonly IntPtr core;
        private readonly IntPtr stream;
        private readonly IntPtr listener;
        private readonly IntPtr events;
        private readonly IntPtr metaParam;
        private bool disposed;

        /// <summary>
        /// The negotiated stream size in buffer pixels, or zero until negotiated.
        /// </summary>
        public Vector2 StreamSize { get; private set; }

        /// <param name="fd">PipeWire remote from the portal; ownership passes to PipeWire.</param>
        /// <param name="onCursor">Called on the PipeWire thread with the cursor position in stream pixels.</param>
        public PipeWireCursorStream(int fd, uint nodeId, Action<Vector2> onCursor)
        {
            InstallResolver();
            this.onCursor = onCursor;
            self = GCHandle.Alloc(this);

            pw_init(IntPtr.Zero, IntPtr.Zero);
            threadLoop = pw_thread_loop_new("relartist-cursor", IntPtr.Zero);
            if (threadLoop == IntPtr.Zero)
                throw new InvalidOperationException("pw_thread_loop_new failed");
            if (pw_thread_loop_start(threadLoop) < 0)
                throw new InvalidOperationException("pw_thread_loop_start failed");

            pw_thread_loop_lock(threadLoop);
            try
            {
                context = pw_context_new(pw_thread_loop_get_loop(threadLoop), IntPtr.Zero, 0);
                if (context == IntPtr.Zero)
                    throw new InvalidOperationException("pw_context_new failed");

                core = pw_context_connect_fd(context, fd, IntPtr.Zero, 0);
                if (core == IntPtr.Zero)
                    throw new InvalidOperationException("pw_context_connect_fd failed");

                stream = pw_stream_new(core, "RelativeArtist cursor",
                    pw_properties_new_string("media.type=Video media.category=Capture media.role=Screen"));
                if (stream == IntPtr.Zero)
                    throw new InvalidOperationException("pw_stream_new failed");

                // struct pw_stream_events: uint32 version, then 11 callbacks.
                events = Marshal.AllocHGlobal(8 + 11 * IntPtr.Size);
                new Span<byte>((void*)events, 8 + 11 * IntPtr.Size).Clear();
                *(uint*)events = 2;
                var callbacks = (IntPtr*)(events + 8);
                callbacks[1] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, int, IntPtr, void>)&OnStateChanged;
                callbacks[4] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint, IntPtr, void>)&OnParamChanged;
                callbacks[7] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, void>)&OnProcess;

                metaParam = BuildCursorMetaParam();

                // struct spa_hook is six pointers.
                listener = Marshal.AllocHGlobal(6 * IntPtr.Size);
                new Span<byte>((void*)listener, 6 * IntPtr.Size).Clear();
                pw_stream_add_listener(stream, listener, events, GCHandle.ToIntPtr(self));

                // No format params: the stream takes the compositor's preferred format,
                // which is DMA-BUF where available, so frames are never copied back to the
                // CPU. Offering our own modifier list made KWin fall back to shared memory
                // whenever its chosen modifier wasn't on it.
                if (pw_stream_connect(stream, PW_DIRECTION_INPUT, nodeId, PW_STREAM_FLAG_AUTOCONNECT, IntPtr.Zero, 0) < 0)
                    throw new InvalidOperationException("pw_stream_connect failed");
            }
            finally
            {
                pw_thread_loop_unlock(threadLoop);
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnStateChanged(IntPtr data, int oldState, int state, IntPtr error)
        {
            // enum pw_stream_state: ERROR = -1, UNCONNECTED, CONNECTING, PAUSED, STREAMING
            var message = error != IntPtr.Zero ? Marshal.PtrToStringUTF8(error) : null;
            if (state < 0)
                Log.Write("RelativeArtist", $"Screen cast stream error: {message}", LogLevel.Error);
            else
                Log.Debug("RelativeArtist", $"Screen cast stream state {oldState} -> {state}");
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnParamChanged(IntPtr data, uint id, IntPtr param)
        {
            if (id != SPA_PARAM_Format || param == IntPtr.Zero)
                return;

            var instance = (PipeWireCursorStream)GCHandle.FromIntPtr(data).Target!;
            if (FindProp(param, SPA_FORMAT_VIDEO_size) is { } size)
                instance.StreamSize = new Vector2(((uint*)size)[0], ((uint*)size)[1]);

            var dmabuf = FindProp(param, SPA_FORMAT_VIDEO_modifier) != null;
            Log.Debug("RelativeArtist", $"Screen cast negotiated {instance.StreamSize.X}x{instance.StreamSize.Y} using {(dmabuf ? "DMA-BUF" : "shared memory")}");

            // Metadata is only attached when both ends ask for it.
            var paramList = instance.metaParam;
            pw_stream_update_params(instance.stream, (IntPtr)(&paramList), 1);
        }

        /// <summary>
        /// Builds a SPA_PARAM_Meta object pod asking for cursor metadata. The size is a
        /// range so it matches whatever cursor bitmap size the compositor allocates;
        /// the position is all that's read.
        /// </summary>
        private static IntPtr BuildCursorMetaParam()
        {
            // sizeof(spa_meta_cursor) + sizeof(spa_meta_bitmap) + w * h * 4
            static int CursorMetaSize(int side) => 28 + 20 + side * side * 4;

            var words = new uint[]
            {
                80, SPA_TYPE_Object,
                SPA_TYPE_OBJECT_ParamMeta, SPA_PARAM_Meta,
                SPA_PARAM_META_type, 0, 4, SPA_TYPE_Id, SPA_META_Cursor, 0,
                SPA_PARAM_META_size, 0, 28, SPA_TYPE_Choice,
                SPA_CHOICE_Range, 0, 4, SPA_TYPE_Int,
                (uint)CursorMetaSize(256), (uint)CursorMetaSize(1), (uint)CursorMetaSize(1024), 0
            };
            var pod = Marshal.AllocHGlobal(words.Length * 4);
            Marshal.Copy((int[])(object)words, 0, pod, words.Length);
            return pod;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnProcess(IntPtr data)
        {
            var instance = (PipeWireCursorStream)GCHandle.FromIntPtr(data).Target!;
            Vector2? latest = null;

            IntPtr buffer;
            while ((buffer = pw_stream_dequeue_buffer(instance.stream)) != IntPtr.Zero)
            {
                // struct pw_buffer { struct spa_buffer *buffer; ... }
                var spaBuffer = *(IntPtr*)buffer;
                var nMetas = *(uint*)spaBuffer;
                var metas = *(IntPtr*)(spaBuffer + 8);
                for (var i = 0; i < nMetas; i++)
                {
                    // struct spa_meta { uint32 type; uint32 size; void *data; }
                    var meta = metas + i * 16;
                    if (*(uint*)meta != SPA_META_Cursor || *(uint*)(meta + 4) < 28)
                        continue;

                    // struct spa_meta_cursor { uint32 id; uint32 flags; struct spa_point position; ... }
                    var cursor = *(IntPtr*)(meta + 8);
                    if (*(uint*)cursor != 0)
                        latest = new Vector2(*(int*)(cursor + 8), *(int*)(cursor + 12));
                }
                pw_stream_queue_buffer(instance.stream, buffer);
            }

            if (latest is { } position)
                instance.onCursor(position);
        }

        /// <summary>
        /// Returns a pointer to the body of a property's value in a SPA object pod,
        /// looking through a Choice to its first (default) value.
        /// </summary>
        private static IntPtr? FindProp(IntPtr pod, uint key)
        {
            var size = *(uint*)pod;
            if (*(uint*)(pod + 4) != SPA_TYPE_Object)
                return null;

            // Object header (8) + object body type/id (8), then props.
            var offset = 16u;
            while (offset + 16 <= size + 8)
            {
                var prop = pod + (int)offset;
                var valueSize = *(uint*)(prop + 8);
                var valueType = *(uint*)(prop + 12);
                if (*(uint*)prop == key)
                {
                    var body = prop + 16;
                    // Choice body: uint32 type, uint32 flags, then child pod header and values.
                    return valueType == SPA_TYPE_Choice ? body + 16 : body;
                }
                offset += 16 + ((valueSize + 7) & ~7u);
            }
            return null;
        }

        private static void InstallResolver()
        {
            // Resolve libpipewire from the system ourselves rather than relying on the
            // plugin load context's native library probing.
            if (resolverInstalled)
                return;
            NativeLibrary.SetDllImportResolver(typeof(PipeWireCursorStream).Assembly, (name, assembly, path) =>
                name == LibPipeWire ? NativeLibrary.Load(LibPipeWire) : IntPtr.Zero);
            resolverInstalled = true;
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;

            pw_thread_loop_lock(threadLoop);
            if (stream != IntPtr.Zero)
                pw_stream_destroy(stream);
            if (core != IntPtr.Zero)
                pw_core_disconnect(core);
            pw_thread_loop_unlock(threadLoop);

            pw_thread_loop_stop(threadLoop);
            if (context != IntPtr.Zero)
                pw_context_destroy(context);
            pw_thread_loop_destroy(threadLoop);

            Marshal.FreeHGlobal(listener);
            Marshal.FreeHGlobal(events);
            Marshal.FreeHGlobal(metaParam);
            self.Free();
        }

        [DllImport(LibPipeWire)] private static extern void pw_init(IntPtr argc, IntPtr argv);
        [DllImport(LibPipeWire)] private static extern IntPtr pw_thread_loop_new(string name, IntPtr props);
        [DllImport(LibPipeWire)] private static extern IntPtr pw_thread_loop_get_loop(IntPtr loop);
        [DllImport(LibPipeWire)] private static extern int pw_thread_loop_start(IntPtr loop);
        [DllImport(LibPipeWire)] private static extern void pw_thread_loop_stop(IntPtr loop);
        [DllImport(LibPipeWire)] private static extern void pw_thread_loop_lock(IntPtr loop);
        [DllImport(LibPipeWire)] private static extern void pw_thread_loop_unlock(IntPtr loop);
        [DllImport(LibPipeWire)] private static extern void pw_thread_loop_destroy(IntPtr loop);
        [DllImport(LibPipeWire)] private static extern IntPtr pw_context_new(IntPtr loop, IntPtr props, UIntPtr userDataSize);
        [DllImport(LibPipeWire)] private static extern void pw_context_destroy(IntPtr context);
        [DllImport(LibPipeWire)] private static extern IntPtr pw_context_connect_fd(IntPtr context, int fd, IntPtr props, UIntPtr userDataSize);
        [DllImport(LibPipeWire)] private static extern int pw_core_disconnect(IntPtr core);
        [DllImport(LibPipeWire)] private static extern IntPtr pw_properties_new_string(string args);
        [DllImport(LibPipeWire)] private static extern IntPtr pw_stream_new(IntPtr core, string name, IntPtr props);
        [DllImport(LibPipeWire)] private static extern void pw_stream_destroy(IntPtr stream);
        [DllImport(LibPipeWire)] private static extern void pw_stream_add_listener(IntPtr stream, IntPtr listener, IntPtr events, IntPtr data);
        [DllImport(LibPipeWire)] private static extern int pw_stream_connect(IntPtr stream, uint direction, uint targetId, uint flags, IntPtr parameters, uint nParams);
        [DllImport(LibPipeWire)] private static extern int pw_stream_update_params(IntPtr stream, IntPtr parameters, uint nParams);
        [DllImport(LibPipeWire)] private static extern IntPtr pw_stream_dequeue_buffer(IntPtr stream);
        [DllImport(LibPipeWire)] private static extern int pw_stream_queue_buffer(IntPtr stream, IntPtr buffer);
    }
}
