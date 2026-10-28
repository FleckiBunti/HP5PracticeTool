// Reloads slot 1 from inside the running game by taking the same route as its menus:
//   pause -> Main menu (quit) -> Continue adventure -> slot 1 -> Continue -> area loads.
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace HP5Practice
{
    public sealed class MenuDriver
    {
        readonly Game game;
        readonly int pid;  // the game process this load started with
        public Action<string> Progress = delegate { };

        public MenuDriver(Game game) { this.game = game; pid = game.ProcessId; }

        sealed class AbortException : Exception { public AbortException(string m) : base(m) { } }

        public string ReloadSlot1(Action installSave, Func<bool> saveInstalled)
        {
            try { return Run(installSave, saveInstalled); }
            catch (AbortException ex) { return ex.Message; }
        }

        string Run(Action installSave, Func<bool> saveInstalled)
        {
            if (!FocusGame()) Progress("Click the game window to start the load.");
            string screen = game.FrontEndState;
            if (screen == "9.0_Game")
            {
                Progress("Pausing...");
                if (!game.QueuePause() || !WaitState("10.0_Paused", 3000)) return "The game didn't pause (state " + game.FrontEndState + ").";
                screen = game.FrontEndState;
            }
            if (screen == "10.0_Paused")
            {
                Progress("Going to the main menu...");
                if (!Settle(() => game.PauseIdle)) return "The pause menu didn't get ready.";
                if (!game.QuitToMainMenu() || !WaitState("2.0_Main_Menu", 15000)) return Abort("Didn't reach the main menu (state " + game.FrontEndState + ").");
            }
            if (game.FrontEndState != "2.0_Main_Menu") return "Full load starts from gameplay, the pause menu or the main menu (state " + game.FrontEndState + ").";

            installSave();
            if (!Settle(() => game.MainMenuIdle)) return Abort("The main menu didn't get ready.");
            if (!game.OpenSaveList() || !WaitState("4.0_Load_Game", 10000)) return Abort("The save list didn't open (state " + game.FrontEndState + ").");

            Progress("Loading the save...");
            if (!Settle(() => game.SaveListIdle)) return Abort("The save list didn't get ready.");
            if (!saveInstalled()) installSave();  // the game autosaved over it after reaching the menu
            if (!game.LoadSlot1() || !WaitState("Load_Game_Options", 10000)) return Abort("The save didn't load (state " + game.FrontEndState + ").");

            Progress("Loading the area...");
            if (!Settle(() => game.OptionsIdle)) return Abort("The options page didn't get ready.");
            if (!game.ContinueFromOptions() || !WaitState("Level_Load", 10000)) return Abort("The area didn't start loading (state " + game.FrontEndState + ").");
            if (!WaitLevelLoad(90000)) return "The area didn't finish loading (state " + game.FrontEndState + ").";
            for (int i = 0; i < 100 && game.HarryPosition() == null; i++) { CheckGame(); Thread.Sleep(100); }
            Thread.Sleep(1500);  // let the area settle before teleporting
            return null;
        }

        string Abort(string why) { return why + " Stopped here; finish from the game's menus by hand."; }

        void CheckGame()
        {
            if (game.ProcessId != pid || !game.Attached) throw new AbortException("The game closed during the load.");
        }

        bool WaitState(string state, int ms) { return WaitFor(() => game.FrontEndState == state, ms); }

        bool Settle(Func<bool> idle)
        {
            if (!WaitFor(idle, 15000)) return false;
            WaitFor(() => !game.MoviePlaying, 5000);
            WaitFor(() => false, SettleMs);
            return idle();
        }

        const int SettleMs = 400;

        bool WaitFor(Func<bool> ready, int ms, Action tick = null)
        {
            var active = new Stopwatch(); var stalled = Stopwatch.StartNew(); var clock = Stopwatch.StartNew();
            uint frames = game.FrameCount; long frameAt = 0;
            string told = null;
            while (active.ElapsedMilliseconds < ms)
            {
                if (ready()) return true;
                CheckGame();
                if (tick != null) tick();
                uint f = game.FrameCount;
                if (f != frames) { frames = f; frameAt = clock.ElapsedMilliseconds; }
                bool front = Native.ForegroundPid() == pid, running = clock.ElapsedMilliseconds - frameAt < 500;
                string why = !front ? "Paused: click the game window to continue the load." : !running ? "Waiting for the game to respond..." : null;
                if (why != told) { Progress(why ?? "Loading..."); told = why; }
                if (why == null) { active.Start(); stalled.Reset(); stalled.Start(); }
                else
                {
                    active.Stop();
                    if (stalled.ElapsedMilliseconds > 120000) throw new AbortException(Abort(front ? "The game didn't respond for 2 minutes." : "The game window wasn't in front for 2 minutes."));
                }
                Thread.Sleep(20);
            }
            return ready();
        }

        bool WaitLevelLoad(int ms)
        {
            return WaitFor(() => game.FrontEndState == "9.0_Game", ms, () => game.SkipLoadingMovie());
        }

        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

        bool FocusGame()
        {
            if (Native.ForegroundPid() == pid) return true;
            IntPtr h = game.MainWindow;
            if (h == IntPtr.Zero) return false;
            keybd_event(0x12, 0, 0, IntPtr.Zero); keybd_event(0x12, 0, 2, IntPtr.Zero);  // Alt tap lifts the focus lock
            ShowWindow(h, 9);
            SetForegroundWindow(h);
            Thread.Sleep(300);
            return Native.ForegroundPid() == pid;
        }
    }
}
