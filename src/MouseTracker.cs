using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;
using OpenTabletDriver.Plugin;

namespace RelativeArtist
{
    /// <summary>
    /// Watches every relative pointing device under /dev/input and reports its
    /// motion, so the pen's position can follow the cursor when a mouse moves it.
    /// The devices are only read, never grabbed.
    /// </summary>
    public sealed class MouseTracker : IDisposable
    {
        private const string InputClassPath = "/sys/class/input";
        private const int InputEventSize = 24; // struct input_event on 64-bit
        private const ushort EV_REL = 0x02;
        private const ushort REL_X = 0x00;
        private const ushort REL_Y = 0x01;
        private static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(2);

        private readonly Action<Vector2> onMove;
        private readonly HashSet<string> watched = new HashSet<string>();
        private readonly Timer rescanTimer;
        private volatile bool disposed;

        public MouseTracker(Action<Vector2> onMove)
        {
            this.onMove = onMove;
            rescanTimer = new Timer(_ => Rescan(), null, TimeSpan.Zero, RescanInterval);
        }

        private void Rescan()
        {
            if (disposed || !Directory.Exists(InputClassPath))
                return;

            foreach (var dir in Directory.EnumerateDirectories(InputClassPath, "event*"))
            {
                var node = Path.GetFileName(dir);
                lock (watched)
                {
                    if (watched.Contains(node) || !IsMouse(dir))
                        continue;
                    watched.Add(node);
                }

                var thread = new Thread(() => ReadDevice(node))
                {
                    IsBackground = true,
                    Name = $"RelativeArtist mouse {node}"
                };
                thread.Start();
            }
        }

        private static bool IsMouse(string sysDir)
        {
            try
            {
                // Skip OTD's own virtual devices, including its relative mode mouse.
                var name = File.ReadAllText(Path.Combine(sysDir, "device", "name")).Trim();
                if (name.StartsWith("OpenTabletDriver", StringComparison.Ordinal))
                    return false;

                // Skip what libinput won't use as a pointer, e.g. a tablet's own boot
                // mouse interface, since its motion never reaches the cursor.
                var udev = ReadUdevProperties(sysDir);
                if (udev.Contains("ID_INPUT_TABLET=1") || udev.Contains("LIBINPUT_IGNORE_DEVICE=1"))
                    return false;

                // The bitmask is printed as space separated words, lowest bits last.
                var words = File.ReadAllText(Path.Combine(sysDir, "device", "capabilities", "rel")).Trim().Split(' ');
                var low = Convert.ToUInt64(words[^1], 16);
                return (low & (1 << REL_X)) != 0 && (low & (1 << REL_Y)) != 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static HashSet<string> ReadUdevProperties(string sysDir)
        {
            var properties = new HashSet<string>();
            var dev = File.ReadAllText(Path.Combine(sysDir, "dev")).Trim();
            var db = "/run/udev/data/c" + dev;
            if (File.Exists(db))
            {
                foreach (var line in File.ReadLines(db))
                {
                    if (line.StartsWith("E:", StringComparison.Ordinal))
                        properties.Add(line.Substring(2));
                }
            }
            return properties;
        }

        private void ReadDevice(string node)
        {
            try
            {
                using var stream = new FileStream("/dev/input/" + node, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1);
                Log.Debug("RelativeArtist", $"Tracking mouse motion from /dev/input/{node}");

                var buffer = new byte[InputEventSize];
                while (!disposed)
                {
                    int read = 0;
                    while (read < InputEventSize)
                    {
                        int n = stream.Read(buffer, read, InputEventSize - read);
                        if (n == 0)
                            return;
                        read += n;
                    }

                    if (disposed)
                        return;

                    var type = BitConverter.ToUInt16(buffer, 16);
                    var code = BitConverter.ToUInt16(buffer, 18);
                    var value = BitConverter.ToInt32(buffer, 20);
                    if (type != EV_REL)
                        continue;

                    if (code == REL_X)
                        onMove(new Vector2(value, 0));
                    else if (code == REL_Y)
                        onMove(new Vector2(0, value));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Device unplugged or not readable; a later rescan picks it up again if it comes back.
            }
            finally
            {
                lock (watched)
                    watched.Remove(node);
            }
        }

        public void Dispose()
        {
            // Reader threads blocked in read() exit on their next event.
            disposed = true;
            rescanTimer.Dispose();
        }
    }
}
