// Main window: status, savestate list, buttons and hotkeys
//   F5 save state, F6 quick load (position only), F7 fly mode, F8 full load (save file + reload + position).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace HP5Practice
{
    public sealed class MainForm : Form
    {
        const uint VK_F5 = 0x74, VK_F6 = 0x75, VK_F7 = 0x76, VK_F8 = 0x77;

        readonly Game game = new Game();
        readonly FlyMode fly;
        readonly bool[] vkDown = new bool[256];  // fly keys currently held
        readonly List<SaveState> states;
        readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer { Interval = 250 };
        volatile bool busy;  // a load or teleport is running on a worker thread
        volatile int gamePid;  // for the keyboard hook thread
        readonly int ownPid = System.Diagnostics.Process.GetCurrentProcess().Id;

        readonly Label status = new Label { AutoSize = false, Dock = DockStyle.Top, Height = 64, Padding = new Padding(6) };
        readonly ListView list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, LabelEdit = true };
        readonly ListView presetList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
        readonly TabControl tabs = new TabControl { Dock = DockStyle.Fill };
        readonly Label message = new Label { AutoSize = false, Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(6), ForeColor = Color.DarkRed };
        AttachResult lastAttach = AttachResult.NotRunning;

        public MainForm()
        {
            Text = "HP5 Practice Tool";
            ClientSize = new Size(640, 480);
            MinimumSize = new Size(460, 320);
            Font = new Font("Segoe UI", 9f);

            list.Columns.Add("Name", 200);
            list.Columns.Add("Area", 140);
            list.Columns.Add("Position", 160);
            list.Columns.Add("Save", 60);
            list.AfterLabelEdit += OnRenamed;
            list.DoubleClick += delegate { FullLoad(); };
            presetList.Columns.Add("Level / chapter start", 420);
            presetList.DoubleClick += delegate { LoadPreset(); };

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(4) };
            buttons.Controls.Add(MakeButton("Save (F5)", delegate { SaveNew(); }));
            buttons.Controls.Add(MakeButton("Shallow load (F6)", delegate { QuickLoad(); }));
            buttons.Controls.Add(MakeButton("Fly (F7)", delegate { ToggleFly(); }));
            buttons.Controls.Add(MakeButton("Full load (F8)", delegate { FullLoad(); }));
            buttons.Controls.Add(MakeButton("Rename", delegate { if (list.SelectedItems.Count > 0) list.SelectedItems[0].BeginEdit(); }));
            buttons.Controls.Add(MakeButton("Delete", delegate { DeleteSelected(); }));
            buttons.Controls.Add(MakeButton("Import save...", delegate { ImportSave(); }));

            var stateTab = new TabPage("Savestates");
            stateTab.Controls.Add(list);
            stateTab.Controls.Add(buttons);
            var presetButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(4) };
            presetButtons.Controls.Add(MakeButton("Load level (F8)", delegate { LoadPreset(); }));
            presetButtons.Controls.Add(MakeButton("Open presets folder", delegate { OpenPresetsFolder(); }));
            presetButtons.Controls.Add(MakeButton("Refresh", delegate { RefreshPresets(); }));
            var presetTab = new TabPage("Level select");
            presetTab.Controls.Add(presetList);
            presetTab.Controls.Add(presetButtons);
            tabs.TabPages.Add(stateTab);
            tabs.TabPages.Add(presetTab);

            var help = new Label
            {
                Dock = DockStyle.Bottom, Height = 90, Padding = new Padding(6), ForeColor = Color.DimGray,
                Text = "F5: save a state (Harry's position + the game's last autosave).\n" +
                       "F6: quick load - only moves Harry back, in the same area.  F8: full load - reloads the save " +
                       ", then moves Harry. Might play cutscenes\n" +
                       "F7: fly mode - WASD move, Space up, Z down, Shift faster; F7 again drops Harry.\n" +
                       "F8 on the Level select tab loads the selected level (Harry starts at the save's spawn)."
            };

            Controls.Add(tabs);
            Controls.Add(status);
            Controls.Add(help);
            Controls.Add(message);

            fly = new FlyMode(game, vk => vkDown[vk & 0xFF]);
            fly.Ended = why => OnUi(delegate { Notify(why); });

            states = StateStore.Load();
            RefreshList();
            RefreshPresets();
            poll.Tick += delegate { Poll(); };
            poll.Start();
            Poll();
        }

        static Button MakeButton(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, Height = 28 };
            b.Click += click;
            return b;
        }

        // Runs an action on the UI thread from any thread; does nothing once the window is gone.
        void OnUi(Action a)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); }
            catch (InvalidOperationException) { }  // closing
        }

        // ---- hotkeys ----

        Thread hookThread;
        uint hookThreadId;
        Native.LowLevelKeyboardProc hookProc;  // kept in a field so the GC doesn't collect it
        IntPtr hook;
        readonly HashSet<uint> keysDown = new HashSet<uint>();  // hook thread only

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            var ready = new ManualResetEvent(false);
            hookThread = new Thread(delegate ()
            {
                hookThreadId = Native.GetCurrentThreadId();
                hookProc = KeyboardHook;
                hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, hookProc, Native.GetModuleHandle(null), 0);
                ready.Set();
                if (hook == IntPtr.Zero) return;
                Native.MSG msg;
                while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0) { }
                Native.UnhookWindowsHookEx(hook);
            }) { IsBackground = true, Name = "keyboard hook" };
            hookThread.Start();
            ready.WaitOne();
            if (hook == IntPtr.Zero) Notify("Couldn't install the keyboard hook, so the F-keys are off. The buttons still work.");
        }

        IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                uint vk = (uint)System.Runtime.InteropServices.Marshal.ReadInt32(lParam);
                int msg = (int)wParam;
                bool down = msg == 0x0100 || msg == 0x0104, up = msg == 0x0101 || msg == 0x0105;
                if (Array.IndexOf(FlyMode.Keys, vk) >= 0) vkDown[vk & 0xFF] = down || (!up && vkDown[vk & 0xFF]);
                if (vk == VK_F5 || vk == VK_F6 || vk == VK_F7 || vk == VK_F8)
                {
                    if (down && keysDown.Add(vk))
                    {
                        int fg = Native.ForegroundPid();
                        if (fg != 0 && (fg == gamePid || fg == ownPid))
                        {
                            if (vk == VK_F5) OnUi(SaveNew);
                            else if (vk == VK_F6) OnUi(QuickLoad);
                            else if (vk == VK_F7) OnUi(ToggleFly);
                            else OnUi(LoadFromActiveTab);
                        }
                    }
                    if (up) keysDown.Remove(vk);
                }
            }
            return Native.CallNextHookEx(hook, code, wParam, lParam);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (busy) { e.Cancel = true; Notify("A load is running; close the tool after it finishes."); return; }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (hookThreadId != 0) Native.PostThreadMessage(hookThreadId, 0x0012, IntPtr.Zero, IntPtr.Zero);  // WM_QUIT
            poll.Stop();
            fly.Stop();
            game.Dispose();
            base.OnFormClosed(e);
        }

        // ---- status ----

        void Poll()
        {
            if (fly.Active && !game.Attached) fly.Stop();  // game gone: stop the loop before the handle is closed
            AttachResult r = game.Attach();
            gamePid = r == AttachResult.Ok ? game.ProcessId : 0;
            if (r != lastAttach) { lastAttach = r; if (r == AttachResult.Ok && !busy) Notify(""); }
            switch (r)
            {
                case AttachResult.NotRunning: status.Text = "Game: not running.\nThe tool attaches automatically."; return;
                case AttachResult.WrongExe: status.Text = "Game: unsupported hp.exe.\nThis tool only works with the community exe (MD5 " + Game.ExpectedMd5 + ")."; return;
                case AttachResult.AccessDenied: status.Text = "Game: found, but the tool can't access it.\nRun the tool with the same rights as the game (e.g. both as administrator or both not)."; return;
                case AttachResult.HookFailed: status.Text = "Game: found, but the tool couldn't hook into it.\n" + game.LastError; return;
            }
            float[] p = game.HarryPosition();
            string pos = p == null ? "-" : string.Format(CultureInfo.InvariantCulture, "{0:F2}, {1:F2}, {2:F2}", p[0], p[1], p[2]);
            status.Text = string.Format("Game: attached (pid {0})\nArea: {1}    Screen: {2}\nHarry: {3}{4}",
                game.ProcessId, Empty(game.Area), Empty(game.FrontEndState), pos, fly.Active ? "    FLYING (F7 to stop)" : "");
        }

        static string Empty(string s) { return string.IsNullOrEmpty(s) ? "-" : s; }

        SaveState Selected()
        {
            if (states.Count == 0) { Notify("No savestates yet. Press F5 in the game to make one."); return null; }
            return list.SelectedItems.Count > 0 ? (SaveState)list.SelectedItems[0].Tag : states[states.Count - 1];
        }

        // ---- actions ----

        void SaveNew()
        {
            if (busy) return;
            if (!game.Attached) { Notify("The game isn't running."); return; }
            if (!game.InGameplay) { Notify("Savestates can only be made while playing (not in menus or loading)."); return; }
            float[] p = game.HarryPosition();
            if (p == null) { Notify("Harry's position is unavailable right now."); return; }
            float yaw = game.HarryYaw();
            string area = game.Area;
            int n = 1;
            foreach (SaveState s in states) if (s.Area == area) n++;
            string save, problem = null;
            try { save = StateStore.SnapshotGameSave(); }
            catch (Exception ex) { save = ""; problem = "Couldn't copy the game's save (" + ex.Message + "), so this state only has the position."; }
            if (save == "" && problem == null) problem = "The game has no save yet, so this state only has the position (quick load only).";
            var st = new SaveState { Name = area + " " + n, Area = area, X = p[0], Y = p[1], Z = p[2], Yaw = yaw, SaveFile = save };
            states.Add(st);
            if (!SaveStates()) return;
            RefreshList();
            list.Items[list.Items.Count - 1].Selected = true;
            if (problem != null) Notify("Saved \"" + st.Name + "\". " + problem); else Notify("Saved \"" + st.Name + "\".", false);
        }

        void QuickLoad()
        {
            if (busy) return;
            SaveState st = Selected(); if (st == null) return;
            if (!game.Attached) { Notify("The game isn't running."); return; }
            if (!st.HasPosition) { Notify("\"" + st.Name + "\" has no position (imported save); use full load (F8)."); return; }
            if (st.Area != game.Area) { Notify("\"" + st.Name + "\" is in " + st.Area + " but the game is in " + Empty(game.Area) + ". Use full load (F8)."); return; }
            fly.Stop();
            RunWorker(delegate
            {
                string err;
                bool ok = game.TeleportHarry(new[] { st.X, st.Y, st.Z }, st.Yaw, out err);
                return ok && err == null ? null : err;
            }, "Moved Harry to \"" + st.Name + "\".");
        }

        void ToggleFly()
        {
            if (busy) return;
            if (fly.Active) { fly.Stop(); Notify("Fly mode off.", false); return; }
            string err = fly.Start();
            if (err != null) Notify(err); else Notify("Fly mode on: WASD move, Space up, Z down, Shift faster. F7 to stop.", false);
        }

        // F8: the selected savestate, or the selected level when the Level select tab is open.
        void LoadFromActiveTab()
        {
            if (tabs.SelectedIndex == 1) LoadPreset(); else FullLoad();
        }

        void FullLoad()
        {
            SaveState st = Selected(); if (st != null) FullLoad(st);
        }

        void LoadPreset()
        {
            if (presetList.SelectedItems.Count == 0) { Notify(presetList.Items.Count == 0 ? "No level saves yet: put .HPOOTP files into the presets folder." : "Select a level first."); return; }
            FullLoad((SaveState)presetList.SelectedItems[0].Tag);
        }

        void FullLoad(SaveState st)
        {
            if (busy) return;
            if (!game.Attached) { Notify("The game isn't running."); return; }
            if (st.SaveFilePath == null || !File.Exists(st.SaveFilePath)) { Notify("\"" + st.Name + "\" has no save file; only quick load (F6) works for it."); return; }
            if (!StateStore.LooksLikeSave(st.SaveFilePath)) { Notify("\"" + st.Name + "\": " + Path.GetFileName(st.SaveFilePath) + " isn't a valid HP5 save."); return; }
            string screen = game.FrontEndState;
            if (screen != "9.0_Game" && screen != "10.0_Paused" && screen != "2.0_Main_Menu") { Notify("Full load starts from gameplay, the pause menu or the main menu (not from other menus or loading)."); return; }

            fly.Stop();
            Notify("Loading \"" + st.Name + "\"... don't press keys in the game.", false);
            RunWorker(delegate
            {
                var driver = new MenuDriver(game);
                driver.Progress = text => OnUi(delegate { Notify(text, false); });
                string result = driver.ReloadSlot1(delegate { StateStore.InstallSave(st); }, delegate { return StateStore.SaveInstalled(st); });
                if (result != null || !st.HasPosition) return result;
                if (game.Area != st.Area) return "Loaded the save, but the game is in " + Empty(game.Area) + " instead of " + st.Area + ", so Harry wasn't moved.";
                string err;
                bool ok = game.TeleportHarry(new[] { st.X, st.Y, st.Z }, st.Yaw, out err);
                if (ok) return err == null ? null : "Loaded the save. " + err;
                return "Loaded the save, but moving Harry failed: " + err;
            }, "Loaded \"" + st.Name + "\".");
        }

        // Runs work on a background thread with busy set; work returns null on success or a message.
        void RunWorker(Func<string> work, string success)
        {
            busy = true;
            var worker = new Thread(delegate ()
            {
                string result;
                try { result = work(); }
                catch (Exception ex) { result = "Failed: " + ex.Message; }
                OnUi(delegate
                {
                    busy = false;
                    if (result == null) Notify(success, false); else Notify(result);
                });
            });
            worker.IsBackground = true;
            worker.Start();
        }

        void ImportSave()
        {
            if (busy) return;
            using (var dlg = new OpenFileDialog { Title = "Import an HP5 save", Filter = "HP5 saves (HPOOTP*)|HPOOTP*;*.HPOOTP|All files|*.*" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (!StateStore.LooksLikeSave(dlg.FileName)) { Notify("That file isn't an HP5 save."); return; }
                string name;
                try { name = StateStore.ImportSave(dlg.FileName); }
                catch (Exception ex) { Notify("Couldn't import the save: " + ex.Message); return; }
                var st = new SaveState { Name = Path.GetFileName(dlg.FileName), Area = "", X = float.NaN, Y = float.NaN, Z = float.NaN, SaveFile = name };
                states.Add(st);
                if (!SaveStates()) return;
                RefreshList();
                Notify("Imported \"" + st.Name + "\". Use full load (F8) to play it.", false);
            }
        }

        // Writes states.txt; shows the problem instead of crashing if that fails (e.g. the file is locked).
        bool SaveStates()
        {
            try { StateStore.Save(states); return true; }
            catch (Exception ex) { Notify("Couldn't write states.txt: " + ex.Message); return false; }
        }

        void RefreshPresets()
        {
            presetList.BeginUpdate();
            presetList.Items.Clear();
            foreach (SaveState s in StateStore.LoadPresets()) presetList.Items.Add(new ListViewItem(s.Name) { Tag = s });
            presetList.EndUpdate();
        }

        void OpenPresetsFolder()
        {
            Directory.CreateDirectory(StateStore.PresetsDir);
            System.Diagnostics.Process.Start("explorer.exe", "\"" + StateStore.PresetsDir + "\"");
        }

        void DeleteSelected()
        {
            if (busy || list.SelectedItems.Count == 0) return;
            var st = (SaveState)list.SelectedItems[0].Tag;
            states.Remove(st);
            if (!SaveStates()) { states.Add(st); return; }
            if (st.SaveFilePath != null) try { File.Delete(st.SaveFilePath); } catch { }
            RefreshList();
        }

        void OnRenamed(object sender, LabelEditEventArgs e)
        {
            if (e.Label == null || e.Label.Trim() == "") { e.CancelEdit = true; return; }
            ((SaveState)list.Items[e.Item].Tag).Name = e.Label.Trim();
            SaveStates();
        }

        void RefreshList()
        {
            list.BeginUpdate();
            list.Items.Clear();
            foreach (SaveState s in states)
            {
                var item = new ListViewItem(new[] { s.Name, s.Area, s.PositionText, string.IsNullOrEmpty(s.SaveFile) ? "-" : "yes" }) { Tag = s };
                list.Items.Add(item);
            }
            list.EndUpdate();
        }

        void Notify(string text, bool problem = true)
        {
            message.ForeColor = problem ? Color.DarkRed : Color.DarkGreen;
            message.Text = text;
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
