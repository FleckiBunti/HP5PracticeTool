// Access to the running hp.exe
// installs in-game command hook
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace HP5Practice
{
    public enum AttachResult { NotRunning, WrongExe, AccessDenied, HookFailed, Ok }

    public sealed class Game : IDisposable
    {
        public const string ExpectedMd5 = "4C852B5E46484B5600C309B3867AFC9E";

        // Fixed addresses in hp.exe (image base 0x400000)
        const uint IatPeekMessageA = 0x007B73B4;   // import slot, called once per main-loop iteration
        const uint FnTeleportPlayer = 0x0048DFD0;  // void __cdecl TeleportPlayer(float matrix[16])
        const uint PtrHarryNode = 0x00C1FC14;      // -> Harry: scene node, position at +0x40 (0xC1F998 = party list entry 6, companion)
        const uint HarryNodeVtable = 0x008815A0;
        const uint PtrFrontEndState = 0x00BEF6BC;  // -> current front-end state object, name at +5
        const uint AreaName = 0x00E69F28;          // current area name
        // Level_Load state handler: step at +0xA0 (4 = loading the level, 5 = level loaded, waiting for the movie,
        // 6 = stop the movie if +0xB9 is set, 7 = proceed to the game), level-loaded flag at +0xB8.
        const uint LevelLoadHandler = 0x008BFDD0;
        // Front-end state machine: [0xE6B32C] = machine (61 transitions of 0x28 bytes at +0x894: event name inline,
        // from-state at +0x20, to-state at +0x24).
        const uint PtrStateMachine = 0x00E6B32C;
        const uint GameHandler = 0x008CE6B0;       // 9.0_Game: pending transition at +0xA0
        const uint PauseHandler = 0x00C2BFD0;      // 10.0_Paused: step +0xA0 (1 = idle, 2 = quit to the main menu)
        const uint PtrMovieManager = 0x00E6B2DC;   // menu/loading movies; [+4] = current player (0 = none)
        const uint MainMenuHandler = 0x00C50A00;   // 2.0_Main_Menu: step +0xA0 (1 = idle), pending transition +0xA4
        const uint LoadGameHandler = 0x00C2BB60;   // 4.0_Load_Game: step +0xA0 (1 = idle, 2 = load the slot)
        const uint LoadSlotIndex = 0x00C848C0;     // slot the save list loads (0 = slot 1)
        const uint OptionsHandler = 0x00C70A60;    // Load_Game_Options: step +0x9EBC (1 = idle, 12 = Continue)
        // Quitting to the main menu, as the pause menu does after "Yes": 0x47CEF0(0xC1E050), 0x508B50(eax =
        // [0xE6B2D8]), 0x509370().
        const int CaveQuitStub = 0x600;

        // Used by fly mode to move only Harry
        const uint FnPlaceBody = 0x004D0030;         // stdcall (component*, float matrix[16]*, int mode 0, int 1)
        const uint TypeInfoTable = 0x00C5D310;       // [entity+0x50] = type; info = [table + 4*type]


        const int CaveOrigPeek = 0x160, CaveMagic = 0x1F0, CaveCode = 0x200, CaveCodeMax = 0x200;
        // cmd = (sequence << 8) | command
        const int CaveCmd = 0x400, CaveAck = 0x404, CaveFlyComp = 0x408, CaveFlyPosComp = 0x40C, CaveFlyEnt = 0x410,
            CaveCurrent = 0x414, CaveSkipped = 0x418, CaveFrames = 0x41C, CaveMatrix = 0x420;
        // cmd 3 (generic call): target, eax/ecx/edx/esi/edi, 4 stack args, result; saved esp (all commands)
        const int CaveCallTarget = 0x480, CaveCallEax = 0x484, CaveCallArgs = 0x498, CaveCallResult = 0x4A8, CaveSavedEsp = 0x4AC;
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("HP5PT009");  // bump on changes
        static readonly byte[] MagicPrefix = Encoding.ASCII.GetBytes("HP5PT");

        readonly object sync = new object();
        Process proc;
        IntPtr handle;
        uint cave;
        uint seq;  // last command sequence number

        public int ProcessId { get { Process p = proc; return p == null ? 0 : p.Id; } }
        public string LastError { get; private set; }

        public IntPtr MainWindow
        {
            get
            {
                Process p = proc;
                if (p == null) return IntPtr.Zero;
                try { p.Refresh(); return p.MainWindowHandle; } catch { return IntPtr.Zero; }
            }
        }

        // The game keeps slot 1 in this single file
        public static string SaveFilePath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Electronic Arts\Harry Potter and the Order of the Phoenix\HPOOTP\HPOOTP");
            }
        }

        public bool Attached
        {
            get
            {
                Process p = proc;
                try { return p != null && cave != 0 && !p.HasExited; } catch { return false; }
            }
        }

        static string md5Path, md5Value;  // cache: hashing 5 MB on every poll is wasteful

        public AttachResult Attach()
        {
            lock (sync)
            {
                if (Attached)
                {
                    if (ReadU32(IatPeekMessageA) == cave) return AttachResult.Ok;
                    // Something put the import back: hook again.
                    try { InstallHook(); return AttachResult.Ok; }
                    catch (Exception ex) { LastError = ex.Message; Detach(); return AttachResult.HookFailed; }
                }
                Detach();
                Process[] list = Process.GetProcessesByName("hp");
                Process found = null;
                AttachResult result = AttachResult.NotRunning;
                foreach (Process p in list)
                {
                    if (found != null) { p.Dispose(); continue; }
                    string path;
                    try { path = p.MainModule.FileName; }
                    catch { result = AttachResult.AccessDenied; p.Dispose(); continue; }
                    if (!string.Equals(FileMd5(path), ExpectedMd5, StringComparison.OrdinalIgnoreCase))
                    {
                        if (result == AttachResult.NotRunning) result = AttachResult.WrongExe;
                        p.Dispose(); continue;
                    }
                    found = p;
                }
                if (found == null) return result;

                IntPtr h = Native.OpenProcess(Native.PROCESS_ALL_ACCESS, false, found.Id);
                if (h == IntPtr.Zero) { found.Dispose(); return AttachResult.AccessDenied; }
                proc = found; handle = h;
                try { InstallHook(); }
                catch (Exception ex) { LastError = ex.Message; Detach(); return AttachResult.HookFailed; }
                return AttachResult.Ok;
            }
        }

        static string FileMd5(string path)
        {
            if (path == md5Path) return md5Value;
            using (var md5 = MD5.Create())
            using (var f = File.OpenRead(path))
            {
                md5Value = BitConverter.ToString(md5.ComputeHash(f)).Replace("-", "");
                md5Path = path;
                return md5Value;
            }
        }

        void InstallHook()
        {
            uint current = ReadU32(IatPeekMessageA);
            if (current == 0) throw new InvalidOperationException("can't read the game's import table");
            if (IsOurCave(current)) { cave = current; seq = ReadU32(cave + CaveAck) >> 8; return; }

            while (IsCaveWithMagic(current, MagicPrefix)) current = ReadU32(current + CaveOrigPeek);

            uint c = cave;
            bool fresh = c == 0 || !IsOurCave(c);
            if (fresh)
            {
                c = (uint)Native.VirtualAllocEx(handle, IntPtr.Zero, 0x1000, Native.MEM_COMMIT | Native.MEM_RESERVE, Native.PAGE_EXECUTE_READWRITE);
                if (c == 0) throw new InvalidOperationException("VirtualAllocEx failed (error " + Marshal.GetLastWin32Error() + ")");
            }
            try
            {
                if (fresh)
                {
                    byte[] code = BuildCave(c);
                    var entry = new byte[5];  // jmp code
                    entry[0] = 0xE9; BitConverter.GetBytes(CaveCode - 5).CopyTo(entry, 1);
                    if (!WriteBytes(c + CaveCode, code) || !WriteBytes(c + CaveQuitStub, BuildQuitStub(c)) || !WriteBytes(c, entry) || !WriteBytes(c + CaveMagic, Magic))
                        throw new InvalidOperationException("can't write into the game's memory");
                }
                if (!WriteU32(c + CaveOrigPeek, current)) throw new InvalidOperationException("can't write into the game's memory");

                uint old;
                if (!Native.VirtualProtectEx(handle, (IntPtr)IatPeekMessageA, 4, Native.PAGE_READWRITE, out old))
                    throw new InvalidOperationException("VirtualProtectEx failed (error " + Marshal.GetLastWin32Error() + ")");
                WriteU32(IatPeekMessageA, c);
                Native.VirtualProtectEx(handle, (IntPtr)IatPeekMessageA, 4, old, out old);
                if (ReadU32(IatPeekMessageA) != c) throw new InvalidOperationException("can't redirect the game's PeekMessageA import");
            }
            catch
            {
                if (fresh) Native.VirtualFreeEx(handle, (IntPtr)c, 0, Native.MEM_RELEASE);
                throw;
            }
            cave = c;
            seq = ReadU32(cave + CaveAck) >> 8;
        }

        // Commands (low byte of cmd):
        //   1: TeleportPlayer(matrix) - Harry plus companions.
        //   2: Harry only (fly mode): PlaceBody(Harry's body component, matrix, 0, 1)
        //   3: generic call (test harness)
        static byte[] BuildCave(uint c)
        {
            var a = new Asm(c + CaveCode);
            a.B(0x9C);                                                   // pushfd
            a.B(0xFF, 0x05); a.D(c + CaveFrames);                        // inc dword [frames]
            a.B(0x60);                                                   // pushad
            a.B(0x89, 0x25); a.D(c + CaveSavedEsp);                      // mov [saved esp],esp
            a.B(0x31, 0xC0);                                             // xor eax,eax
            a.B(0x87, 0x05); a.D(c + CaveCmd);                           // xchg eax,[cmd]  (take it, atomically)
            a.B(0x85, 0xC0); a.Jcc(0x84, "done");                        // test eax,eax / je done
            a.B(0xA3); a.D(c + CaveCurrent);                             // mov [current],eax
            a.B(0xC7, 0x05); a.D(c + CaveSkipped); a.D(1);               // mov dword [skipped],1
            a.B(0x3C, 0x03); a.Jcc(0x84, "three");                       // cmp al,3 / je three
            // in gameplay? state name at [[0xBEF6BC]+5] == "9.0_Game"
            a.B(0x8B, 0x0D); a.D(PtrFrontEndState);                      // mov ecx,[state]
            a.B(0x85, 0xC9); a.Jcc(0x84, "finish");                      // test ecx,ecx / je finish
            a.B(0x81, 0x79, 0x05); a.D(Dword("9.0_")); a.Jcc(0x85, "finish"); // cmp dword [ecx+5],'9.0_' / jne
            a.B(0x81, 0x79, 0x09); a.D(Dword("Game")); a.Jcc(0x85, "finish"); // cmp dword [ecx+9],'Game' / jne
            a.B(0x3C, 0x01); a.Jcc(0x84, "one");                         // cmp al,1 / je one
            a.B(0x3C, 0x02); a.Jcc(0x84, "two");                         // cmp al,2 / je two
            a.Jmp("finish");

            a.Label("one");
            a.B(0xC7, 0x05); a.D(c + CaveSkipped); a.D(0);               // mov dword [skipped],0
            a.B(0x68); a.D(c + CaveMatrix);                              // push matrix
            a.Call(FnTeleportPlayer);                                    // cdecl (esp restored at finish)
            a.Jmp("finish");

            a.Label("two");
            a.B(0xA1); a.D(PtrHarryNode);                                // mov eax,[Harry]
            a.B(0x85, 0xC0); a.Jcc(0x84, "finish");                      // test eax,eax / je finish
            a.B(0x3B, 0x05); a.D(c + CaveFlyEnt); a.Jcc(0x85, "finish"); // cmp eax,[fly entity] / jne finish
            a.B(0xC7, 0x05); a.D(c + CaveSkipped); a.D(0);               // mov dword [skipped],0
            // position component (as the helper does): pos = matrix row 3, w = 1, clear flags, reset path
            a.B(0x8B, 0x35); a.D(c + CaveFlyPosComp);                    // mov esi,[position component]
            a.B(0x85, 0xF6); a.Jcc(0x84, "noPos");                       // test esi,esi / je
            a.B(0xA1); a.D(c + CaveMatrix + 0x30); a.B(0x89, 0x46, 0x10); // mov eax,[m.x]; mov [esi+10h],eax
            a.B(0xA1); a.D(c + CaveMatrix + 0x34); a.B(0x89, 0x46, 0x14); // mov eax,[m.y]; mov [esi+14h],eax
            a.B(0xA1); a.D(c + CaveMatrix + 0x38); a.B(0x89, 0x46, 0x18); // mov eax,[m.z]; mov [esi+18h],eax
            a.B(0xC7, 0x46, 0x1C); a.D(0x3F800000);                      // mov dword [esi+1Ch],1.0
            a.B(0x80, 0x66, 0x4C, 0xAF);                                 // and byte [esi+4Ch],0AFh
            a.B(0xC7, 0x46, 0x30); a.D(0xFFFFFFFF);                      // mov dword [esi+30h],-1
            a.B(0xC7, 0x46, 0x34); a.D(0x7FFF);                          // mov dword [esi+34h],7FFFh
            a.Label("noPos");
            a.B(0xA1); a.D(c + CaveFlyComp);                             // mov eax,[body component]
            a.B(0x85, 0xC0); a.Jcc(0x84, "finish");                      // test eax,eax / je
            a.B(0x6A, 0x01, 0x6A, 0x00);                                 // push 1; push 0 (mode)
            a.B(0x68); a.D(c + CaveMatrix);                              // push matrix
            a.B(0x50);                                                   // push eax
            a.Call(FnPlaceBody);                                         // stdcall
            a.Jmp("finish");

            a.Label("three");
            a.B(0xC7, 0x05); a.D(c + CaveSkipped); a.D(0);               // mov dword [skipped],0
            for (int k = 3; k >= 0; k--) { a.B(0xFF, 0x35); a.D(c + CaveCallArgs + 4 * (uint)k); } // push args[k]
            a.B(0xA1); a.D(c + CaveCallEax);                             // mov eax,[..]
            a.B(0x8B, 0x0D); a.D(c + CaveCallEax + 4);                   // mov ecx,[..]
            a.B(0x8B, 0x15); a.D(c + CaveCallEax + 8);                   // mov edx,[..]
            a.B(0x8B, 0x35); a.D(c + CaveCallEax + 12);                  // mov esi,[..]
            a.B(0x8B, 0x3D); a.D(c + CaveCallEax + 16);                  // mov edi,[..]
            a.B(0xFF, 0x15); a.D(c + CaveCallTarget);                    // call [target]
            a.B(0xA3); a.D(c + CaveCallResult);                          // mov [result],eax

            a.Label("finish");
            a.B(0x8B, 0x25); a.D(c + CaveSavedEsp);                      // mov esp,[saved esp]
            a.B(0xA1); a.D(c + CaveCurrent);                             // mov eax,[current]
            a.B(0xA3); a.D(c + CaveAck);                                 // mov [ack],eax
            a.Label("done");
            a.B(0x61);                                                   // popad
            a.B(0x9D);                                                   // popfd
            a.B(0xFF, 0x25); a.D(c + CaveOrigPeek);                      // jmp [original PeekMessageA]
            byte[] code = a.Bytes();
            if (code.Length > CaveCodeMax) throw new InvalidOperationException("cave code too long");
            return code;
        }

        static byte[] BuildQuitStub(uint c)
        {
            var a = new Asm(c + CaveQuitStub);
            a.B(0x68); a.D(0x00C1E050);                                  // push 0C1E050h
            a.Call(0x0047CEF0);
            a.B(0xA1); a.D(0x00E6B2D8);                                  // mov eax,[0E6B2D8h]
            a.Call(0x00508B50);
            a.Call(0x00509370);
            a.B(0xC3);                                                   // ret (cmd 3 restores esp)
            return a.Bytes();
        }

        static uint Dword(string four) { return BitConverter.ToUInt32(Encoding.ASCII.GetBytes(four), 0); }

        bool IsOurCave(uint addr) { return IsCaveWithMagic(addr, Magic); }

        bool IsCaveWithMagic(uint addr, byte[] magic)
        {
            if (addr < 0x10000) return false;
            byte[] m = ReadBytes(addr + CaveMagic, magic.Length);
            if (m == null) return false;
            for (int k = 0; k < magic.Length; k++) if (m[k] != magic[k]) return false;
            return true;
        }

        // Tiny x86 emitter: bytes plus rel32 jumps/calls with labels.
        sealed class Asm
        {
            readonly uint origin;
            readonly System.Collections.Generic.List<byte> b = new System.Collections.Generic.List<byte>();
            readonly System.Collections.Generic.Dictionary<string, int> labels = new System.Collections.Generic.Dictionary<string, int>();
            readonly System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<int, string>> fixups = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<int, string>>();
            public Asm(uint origin) { this.origin = origin; }
            public void B(params byte[] x) { b.AddRange(x); }
            public void D(uint v) { b.AddRange(BitConverter.GetBytes(v)); }
            public void Label(string name) { labels[name] = b.Count; }
            public void Jcc(byte cc, string label) { B(0x0F, cc); fixups.Add(new System.Collections.Generic.KeyValuePair<int, string>(b.Count, label)); D(0); }
            public void Jmp(string label) { B(0xE9); fixups.Add(new System.Collections.Generic.KeyValuePair<int, string>(b.Count, label)); D(0); }
            public void Call(uint target) { B(0xE8); D((uint)(target - (origin + (uint)b.Count + 4))); }
            public byte[] Bytes()
            {
                byte[] r = b.ToArray();
                foreach (var f in fixups) BitConverter.GetBytes(labels[f.Value] - (f.Key + 4)).CopyTo(r, f.Key);
                return r;
            }
        }

        public void RemoveHook()
        {
            lock (sync)
            {
                if (!Attached || ReadU32(IatPeekMessageA) != cave) return;
                uint orig = ReadU32(cave + CaveOrigPeek);
                if (orig == 0) return;
                uint old;
                Native.VirtualProtectEx(handle, (IntPtr)IatPeekMessageA, 4, Native.PAGE_READWRITE, out old);
                WriteU32(IatPeekMessageA, orig);
                Native.VirtualProtectEx(handle, (IntPtr)IatPeekMessageA, 4, old, out old);
            }
        }

        public void Detach()
        {
            lock (sync)
            {
                if (handle != IntPtr.Zero) Native.CloseHandle(handle);
                if (proc != null) proc.Dispose();
                handle = IntPtr.Zero; proc = null; cave = 0;
            }
        }

        public void Dispose() { RemoveHook(); Detach(); }

        // ---- game state ----

        public string FrontEndState
        {
            get
            {
                uint p = ReadU32(PtrFrontEndState);
                return p < 0x10000 ? "" : ReadCString(p + 5, 27);
            }
        }

        public bool InGameplay { get { return FrontEndState == "9.0_Game"; } }

        public string Area { get { return ReadCString(AreaName, 64); } }

        public bool SkipLoadingMovie()
        {
            if (FrontEndState != "Level_Load" || ReadU32(LevelLoadHandler + 0xA0) != 5) return false;
            WriteBytes(LevelLoadHandler + 0xB9, new byte[] { 1 });
            return WriteU32(LevelLoadHandler + 0xA0, 6);
        }

        // ---- menu actions without key presses (----

        uint FindTransition(string eventName)
        {
            uint machine = ReadU32(PtrStateMachine), current = ReadU32(PtrFrontEndState);
            if (machine < 0x10000 || current < 0x10000) return 0;
            for (uint k = 0; k < 61; k++)
            {
                uint t = machine + 0x894 + 0x28 * k;
                if (ReadU32(t + 0x20) == current && ReadCString(t, 0x20) == eventName) return t;
            }
            return 0;
        }

        public bool QueuePause()
        {
            if (!InGameplay || ReadU32(GameHandler + 0xA0) != 0) return false;
            uint t = FindTransition("Pause");
            return t != 0 && WriteU32(GameHandler + 0xA0, t);
        }

        public bool MoviePlaying { get { uint m = ReadU32(PtrMovieManager); return m >= 0x10000 && ReadU32(m + 4) != 0; } }

        public bool PauseIdle { get { return FrontEndState == "10.0_Paused" && ReadU32(PauseHandler + 0xA0) == 1; } }

        public bool QuitToMainMenu()
        {
            if (!PauseIdle || cave == 0) return false;
            return CallGame(cave + CaveQuitStub, null, null, 60000) != null;
        }

        public bool MainMenuIdle { get { return FrontEndState == "2.0_Main_Menu" && ReadU32(MainMenuHandler + 0xA0) == 1; } }

        public bool OpenSaveList()
        {
            if (!MainMenuIdle) return false;
            uint t = FindTransition("LoadGame");
            if (t == 0) return false;
            WriteU32(MainMenuHandler + 0xA4, t);
            return WriteU32(MainMenuHandler + 0xA0, 4);
        }

        public bool SaveListIdle { get { return FrontEndState == "4.0_Load_Game" && ReadU32(LoadGameHandler + 0xA0) == 1; } }

        public bool LoadSlot1()
        {
            if (!SaveListIdle) return false;
            WriteU32(LoadSlotIndex, 0);
            return WriteU32(LoadGameHandler + 0xA0, 2);
        }

        public bool OptionsIdle
        {
            get
            {
                if (FrontEndState != "Load_Game_Options" || ReadU32(OptionsHandler + 0x9EBC) != 1) return false;
                byte[] inGame = ReadBytes(OptionsHandler + 0x9EB8, 1);  // 0 = opened from the save list
                return inGame != null && inGame[0] == 0;
            }
        }

        public bool ContinueFromOptions()
        {
            return OptionsIdle && WriteU32(OptionsHandler + 0x9EBC, 12);
        }

        public float[] HarryPosition()
        {
            uint node = ReadU32(PtrHarryNode);
            if (node < 0x10000 || ReadU32(node) != HarryNodeVtable) return null;
            return ReadFloats(node + 0x40, 3);
        }

        public float HarryYaw()
        {
            float[] m = HarryMatrix();
            return m == null ? float.NaN : (float)(Math.Atan2(m[2], m[0]) * 180 / Math.PI);
        }

        public bool TeleportHarry(float[] pos, float yaw, out string error)
        {
            error = null;
            if (!Attached) { error = "Not attached to the game."; return false; }
            if (!InGameplay) { error = "Only possible while playing (not in menus or loading)."; return false; }
            if (float.IsNaN(yaw)) yaw = HarryYaw();
            if (float.IsNaN(yaw)) yaw = 0;
            float[] target = (float[])pos.Clone();
            float miss = 0;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (!RunTeleportMatrix(YawMatrix(yaw, target))) { error = "The game did not run the teleport (is it paused, minimized or loading?)."; return false; }
                Thread.Sleep(50);
                float[] now = HarryPosition();
                if (now == null) { error = "Harry's position is unavailable."; return false; }
                float dx = pos[0] - now[0], dy = pos[1] - now[1], dz = pos[2] - now[2];
                miss = (float)Math.Sqrt(dx * dx + dz * dz);
                if (miss < 0.03f) return true;
                if (miss > 0.5f) break;
                target[0] += dx; target[1] += dy; target[2] += dz;
            }
            if (miss >= 0.1f)
                error = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "Harry was moved, but the game put him {0:F2} m away from the saved spot (pushed out of a wall or object?).", miss);
            return true;
        }

        static float[] YawMatrix(float yaw, float[] p)
        {
            double a = yaw * Math.PI / 180;
            float s = (float)Math.Sin(a), c = (float)Math.Cos(a);
            return new float[] { c, 0, s, 0, 0, 1, 0, 0, -s, 0, c, 0, p[0], p[1], p[2], 1 };
        }
        public float[] HarryMatrix()
        {
            uint node = ReadU32(PtrHarryNode);
            if (node < 0x10000 || ReadU32(node) != HarryNodeVtable) return null;
            return ReadFloats(node + 0x10, 16);
        }

        public uint HarryNode { get { return ReadU32(PtrHarryNode); } }

        public uint FrameCount { get { return cave == 0 ? 0 : ReadU32(cave + CaveFrames); } }

        public bool PrepareFly() { return PrepareFly(PtrHarryNode); }

        public bool PrepareFly(uint entityPtr)
        {
            lock (sync)
            {
                if (cave == 0) return false;
                ClearFly();
                uint ent = ReadU32(entityPtr);
                if (ent < 0x10000 || ReadU32(ent) != HarryNodeVtable) return false;
                uint comp = Component(ent, 0x1C);
                if (comp == 0 || ReadU32(comp + 0xF4) == 0) return false;  // must have a physics body
                WriteU32(cave + CaveFlyPosComp, Component(ent, 0x52));
                WriteU32(cave + CaveFlyComp, comp);
                WriteU32(cave + CaveFlyEnt, ent);
                return true;
            }
        }

        public void ClearFly()
        {
            lock (sync)
            {
                if (cave == 0) return;
                WriteU32(cave + CaveFlyEnt, 0);
                WriteU32(cave + CaveFlyComp, 0);
                WriteU32(cave + CaveFlyPosComp, 0);
            }
        }

        uint Component(uint ent, uint infoField)
        {
            uint info = ReadU32(TypeInfoTable + 4 * ReadU32(ent + 0x50));
            if (info < 0x10000) return 0;
            byte[] idx = ReadBytes(info + infoField, 1);
            if (idx == null || idx[0] == 0xFF) return 0;
            return ent + ReadU32(ReadU32(info + 0x10) + 8u * idx[0]);
        }

        public bool RunTeleportMatrix(float[] m, int cmd = 1, int pollMs = 10, int timeoutMs = 1000)
        {
            var b = new byte[64];
            for (int k = 0; k < 16; k++) BitConverter.GetBytes(m[k]).CopyTo(b, 4 * k);
            lock (sync)
            {
                if (cave == 0) return false;
                WriteBytes(cave + CaveMatrix, b);
                return RunCommand(cmd, pollMs, timeoutMs);
            }
        }

        // regs = eax, ecx, edx, esi, edi;
        // args pushed so that args[0] is at [esp+4]. Returns eax, or null if the game didn't run it within timeoutMs.
        public uint? CallGame(uint target, uint[] regs, uint[] args, int timeoutMs = 3000)
        {
            lock (sync)
            {
                if (cave == 0) return null;
                WriteU32(cave + CaveCallTarget, target);
                for (int k = 0; k < 5; k++) WriteU32(cave + CaveCallEax + 4 * (uint)k, regs != null && k < regs.Length ? regs[k] : 0);
                for (int k = 0; k < 4; k++) WriteU32(cave + CaveCallArgs + 4 * (uint)k, args != null && k < args.Length ? args[k] : 0);
                if (!RunCommand(3, 5, timeoutMs)) return null;
                return ReadU32(cave + CaveCallResult);
            }
        }

        bool RunCommand(int cmd, int pollMs, int timeoutMs)
        {
            seq = (seq + 1) & 0xFFFFFF;
            if (seq == 0) seq = 1;
            uint value = (seq << 8) | (uint)cmd;
            if (!WriteU32(cave + CaveCmd, value)) return false;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                Thread.Sleep(pollMs);
                if (ReadU32(cave + CaveAck) == value) return ReadU32(cave + CaveSkipped) == 0;
            }
            WriteU32(cave + CaveCmd, 0);
            return false;
        }

        public void PokeBytes(uint addr, byte[] b) { WriteBytes(addr, b); }

        // Raw access for the test harness.
        public float[] PeekFloats(uint addr, int n) { return ReadFloats(addr, n); }
        public uint PeekU32(uint addr) { return ReadU32(addr); }
        public void PokeFloats(uint addr, float[] f)
        {
            var b = new byte[4 * f.Length];
            for (int k = 0; k < f.Length; k++) BitConverter.GetBytes(f[k]).CopyTo(b, 4 * k);
            WriteBytes(addr, b);
        }
        // ---- memory helpers ----

        byte[] ReadBytes(uint addr, int n)
        {
            IntPtr h = handle;
            if (n <= 0) return new byte[0];
            if (h == IntPtr.Zero) return null;
            var b = new byte[n]; IntPtr r;
            return Native.ReadProcessMemory(h, (IntPtr)addr, b, (IntPtr)n, out r) ? b : null;
        }
        uint ReadU32(uint addr) { byte[] b = ReadBytes(addr, 4); return b == null ? 0 : BitConverter.ToUInt32(b, 0); }
        float[] ReadFloats(uint addr, int n)
        {
            byte[] b = ReadBytes(addr, 4 * n); if (b == null) return null;
            var f = new float[n]; for (int k = 0; k < n; k++) f[k] = BitConverter.ToSingle(b, 4 * k); return f;
        }
        string ReadCString(uint addr, int max)
        {
            byte[] b = ReadBytes(addr, max); if (b == null) return "";
            int e = Array.IndexOf(b, (byte)0); if (e < 0) e = max;
            return Encoding.ASCII.GetString(b, 0, e);
        }
        bool WriteBytes(uint addr, byte[] b)
        {
            IntPtr h = handle, w;
            return h != IntPtr.Zero && Native.WriteProcessMemory(h, (IntPtr)addr, b, b.Length, out w);
        }
        bool WriteU32(uint addr, uint v) { return WriteBytes(addr, BitConverter.GetBytes(v)); }
    }

    static class Native
    {
        public const int PROCESS_ALL_ACCESS = 0x1F0FFF;
        public const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_RELEASE = 0x8000, PAGE_EXECUTE_READWRITE = 0x40, PAGE_READWRITE = 0x04;
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] public static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr n, out IntPtr read);
        [DllImport("kernel32.dll")] public static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int n, out IntPtr written);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr addr, uint size, uint type, uint protect);
        [DllImport("kernel32.dll")] public static extern bool VirtualFreeEx(IntPtr h, IntPtr addr, uint size, uint type);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool VirtualProtectEx(IntPtr h, IntPtr addr, uint size, uint protect, out uint old);
        public const int WH_KEYBOARD_LL = 13;
        public delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookEx(int id, LowLevelKeyboardProc proc, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int pid);
        [DllImport("user32.dll")] public static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
        [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

        // Process id of the window in front (0 if none).
        public static int ForegroundPid()
        {
            int pid;
            IntPtr w = GetForegroundWindow();
            if (w == IntPtr.Zero) return 0;
            GetWindowThreadProcessId(w, out pid);
            return pid;
        }
    }
}
