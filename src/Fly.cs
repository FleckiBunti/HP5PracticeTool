// Fly mode (noclip):
// WASD = move, Space = up, Z = down, Shift = faster
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace HP5Practice
{
    public sealed class FlyMode
    {
        public const float Speed = 6f, FastSpeed = 18f, VerticalSpeed = 4f, FastVerticalSpeed = 12f;

        readonly Game game;
        readonly Func<uint, bool> keyDown;  // virtual-key code
        Thread thread;
        Session session;

        sealed class Session { public volatile bool Run = true; }

        public FlyMode(Game game, Func<uint, bool> keyDown) { this.game = game; this.keyDown = keyDown; }

        public bool Active { get { Session s = session; return s != null && s.Run; } }

        public Action<string> Ended = delegate { };

        public string Start()
        {
            if (Active) return null;
            Stop();
            if (!game.Attached) return "The game isn't running.";
            if (!game.InGameplay) return "Fly mode only works while playing (not in menus or loading).";
            if (!game.PrepareFly()) return "Couldn't find Harry's physics body.";
            var s = new Session();
            session = s;
            thread = new Thread(() => Loop(s)) { IsBackground = true, Name = "fly" };
            thread.Start();
            return null;
        }

        public void Stop()
        {
            Session s = session;
            if (s != null) s.Run = false;
            Thread t = thread;
            if (t != null && t != Thread.CurrentThread) t.Join(3000);
            session = null;
            thread = null;
            game.ClearFly();
        }

        void Loop(Session s)
        {
            timeBeginPeriod(1);
            string why = null;
            try
            {
                uint ent = game.HarryNode;
                float[] m = game.HarryMatrix();
                if (m == null) { why = "Harry isn't available."; return; }
                float x = m[12], y = m[13], z = m[14];
                var clock = Stopwatch.StartNew();
                double last = 0, windowAt = -1;
                IntPtr window = IntPtr.Zero;
                while (s.Run)
                {
                    if (!game.Attached) { why = "The game closed."; return; }
                    if (game.HarryNode != ent) { why = "Fly mode ended: the area changed."; return; }
                    if (!game.InGameplay) { Thread.Sleep(50); last = clock.Elapsed.TotalSeconds; continue; }  // paused

                    double now = clock.Elapsed.TotalSeconds;
                    float dt = (float)Math.Min(0.1, now - last);
                    last = now;

                    m = game.HarryMatrix();
                    if (m == null) { Thread.Sleep(20); continue; }
                    if (now - windowAt > 1) { window = game.MainWindow; windowAt = now; }
                    if (Native.GetForegroundWindow() == window)
                    {
                        bool fast = keyDown(VK_LSHIFT) || keyDown(VK_RSHIFT);
                        if (keyDown(VK_W) || keyDown(VK_A) || keyDown(VK_S) || keyDown(VK_D))
                        {
                            float sp = (fast ? FastSpeed : Speed) * dt;
                            x += m[8] * sp;
                            z += m[10] * sp;
                        }
                        float vs = (fast ? FastVerticalSpeed : VerticalSpeed) * dt;
                        if (keyDown(VK_SPACE)) y += vs;
                        if (keyDown(VK_Z)) y -= vs;
                    }
                    m[12] = x; m[13] = y; m[14] = z; m[15] = 1;
                    if (!game.RunTeleportMatrix(m, 2, 1, 250)) Thread.Sleep(20);
                }
            }
            catch (Exception ex) { why = "Fly mode stopped: " + ex.Message; }
            finally
            {
                timeEndPeriod(1);
                bool ended = s.Run;
                s.Run = false;
                if (ended && why != null) Ended(why);
            }
        }

        const uint VK_W = 0x57, VK_A = 0x41, VK_S = 0x53, VK_D = 0x44, VK_Z = 0x5A, VK_SPACE = 0x20,
            VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1;
        public static readonly uint[] Keys = { VK_W, VK_A, VK_S, VK_D, VK_Z, VK_SPACE, VK_LSHIFT, VK_RSHIFT };

        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);
    }
}
