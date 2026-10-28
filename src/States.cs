// Savestates kept in states.txt next to the tool, one per line:
//   name <TAB> area <TAB> x <TAB> y <TAB> z <TAB> save file (in the saves folder, may be empty) <TAB> facing (degrees, optional)
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace HP5Practice
{
    public sealed class SaveState
    {
        public string Name;
        public string Area;
        public float X, Y, Z;
        public float Yaw = float.NaN;  // Harry's facing in degrees (NaN = keep the current facing)
        public string SaveFile;  // copy of the game's save taken with the state ("" = position only)

        public string PositionText
        {
            get
            {
                if (!HasPosition) return "(save spawn point)";
                string p = string.Format(CultureInfo.InvariantCulture, "{0:F2}, {1:F2}, {2:F2}", X, Y, Z);
                return float.IsNaN(Yaw) ? p : p + string.Format(CultureInfo.InvariantCulture, "  ({0:F0}°)", Yaw);
            }
        }

        public string SaveFilePath { get { return string.IsNullOrEmpty(SaveFile) ? null : Path.IsPathRooted(SaveFile) ? SaveFile : Path.Combine(StateStore.SavesDir, SaveFile); } }

        // States without a position (imported saves, level select) only reload the save.
        public bool HasPosition { get { return !float.IsNaN(X); } }
    }

    public static class StateStore
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        const int KeepBackups = 10;

        static string BaseDir { get { return AppDomain.CurrentDomain.BaseDirectory; } }
        public static string FilePath { get { return Path.Combine(BaseDir, "states.txt"); } }
        public static string SavesDir { get { return Path.Combine(BaseDir, "saves"); } }
        // Bundled chapter/level saves
        public static string PresetsDir { get { return Path.Combine(BaseDir, "presets"); } }
        // Slot 1 as it was before each full load (the newest KeepBackups are kept).
        public static string BackupsDir { get { return Path.Combine(BaseDir, "backups"); } }

        public static List<SaveState> LoadPresets()
        {
            var list = new List<SaveState>();
            if (!Directory.Exists(PresetsDir)) return list;
            string[] files = Directory.GetFiles(PresetsDir, "*.HPOOTP");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string f in files)
                list.Add(new SaveState { Name = Path.GetFileNameWithoutExtension(f), Area = "", X = float.NaN, Y = float.NaN, Z = float.NaN, SaveFile = f });
            return list;
        }

        public static string ImportSave(string path)
        {
            byte[] b = ReadSave(path, 1);
            Directory.CreateDirectory(SavesDir);
            string name = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", Inv) + "-import.HPOOTP";
            File.WriteAllBytes(Path.Combine(SavesDir, name), b);
            return name;
        }

        public static bool LooksLikeSave(byte[] b)
        {
            return b != null && b.Length >= 8 && b[0] == 0x32 && b[1] == 0x30 && b[2] == 0x43 && b[3] == 0x4D && BitConverter.ToInt32(b, 4) == b.Length;
        }

        public static bool LooksLikeSave(string path)
        {
            try { return new FileInfo(path).Length < 1000000 && LooksLikeSave(File.ReadAllBytes(path)); }
            catch { return false; }
        }

        static byte[] ReadSave(string path, int tries)
        {
            for (int t = 1; ; t++)
            {
                try
                {
                    if (new FileInfo(path).Length > 1000000) throw new InvalidDataException(Path.GetFileName(path) + " is too large for an HP5 save.");
                    byte[] b = File.ReadAllBytes(path);
                    if (LooksLikeSave(b)) return b;
                    if (t >= tries) throw new InvalidDataException(Path.GetFileName(path) + " isn't a complete HP5 save.");
                }
                catch (IOException) { if (t >= tries) throw; }
                Thread.Sleep(250);
            }
        }

        static void WriteWithRetry(string path, byte[] b)
        {
            for (int t = 1; ; t++)
            {
                try { File.WriteAllBytes(path, b); return; }
                catch (IOException) { if (t >= 8) throw; Thread.Sleep(250); }
            }
        }

        public static string OriginalBackup { get { return Path.Combine(SavesDir, "original-save-backup.HPOOTP"); } }

        public static List<SaveState> Load()
        {
            var list = new List<SaveState>();
            if (!File.Exists(FilePath)) return list;
            foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
            {
                string[] f = line.Split('\t');
                if (f.Length < 5) continue;
                float x, y, z;
                if (!float.TryParse(f[2], NumberStyles.Float, Inv, out x) || !float.TryParse(f[3], NumberStyles.Float, Inv, out y)
                    || !float.TryParse(f[4], NumberStyles.Float, Inv, out z)) continue;
                float yaw;
                if (f.Length < 7 || !float.TryParse(f[6], NumberStyles.Float, Inv, out yaw)) yaw = float.NaN;
                list.Add(new SaveState { Name = f[0], Area = f[1], X = x, Y = y, Z = z, SaveFile = f.Length > 5 ? f[5] : "", Yaw = yaw });
            }
            return list;
        }

        public static void Save(List<SaveState> list)
        {
            var sb = new StringBuilder();
            foreach (SaveState s in list)
                sb.AppendLine(string.Join("\t", new[] { Clean(s.Name), Clean(s.Area), s.X.ToString("R", Inv), s.Y.ToString("R", Inv), s.Z.ToString("R", Inv), Clean(s.SaveFile), s.Yaw.ToString("R", Inv) }));
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null); else File.Move(tmp, FilePath);
        }

        public static string SnapshotGameSave()
        {
            if (!File.Exists(Game.SaveFilePath)) return "";
            byte[] b = ReadSave(Game.SaveFilePath, 6);
            Directory.CreateDirectory(SavesDir);
            string name = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", Inv) + ".HPOOTP";
            File.WriteAllBytes(Path.Combine(SavesDir, name), b);
            return name;
        }

        public static void InstallSave(SaveState s)
        {
            byte[] b = ReadSave(s.SaveFilePath, 1);
            BackupSlot(b);
            WriteWithRetry(Game.SaveFilePath, b);
        }

        public static bool SaveInstalled(SaveState s)
        {
            try { return SameBytes(File.ReadAllBytes(Game.SaveFilePath), File.ReadAllBytes(s.SaveFilePath)); }
            catch { return false; }
        }


        static void BackupSlot(byte[] installing)
        {
            if (!File.Exists(Game.SaveFilePath)) return;
            byte[] cur;
            try { cur = File.ReadAllBytes(Game.SaveFilePath); } catch (IOException) { return; }
            if (!LooksLikeSave(cur) || SameBytes(cur, installing)) return;
            Directory.CreateDirectory(SavesDir);
            if (!File.Exists(OriginalBackup)) File.WriteAllBytes(OriginalBackup, cur);

            Directory.CreateDirectory(BackupsDir);
            string[] old = Directory.GetFiles(BackupsDir, "slot1-*.HPOOTP");
            Array.Sort(old, StringComparer.Ordinal);
            if (old.Length > 0)
            {
                try { if (SameBytes(cur, File.ReadAllBytes(old[old.Length - 1]))) return; } catch (IOException) { }
            }
            File.WriteAllBytes(Path.Combine(BackupsDir, "slot1-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", Inv) + ".HPOOTP"), cur);
            for (int k = 0; k < old.Length + 1 - KeepBackups; k++) try { File.Delete(old[k]); } catch { }
        }

        static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int k = 0; k < a.Length; k++) if (a[k] != b[k]) return false;
            return true;
        }

        static string Clean(string s) { return (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '); }
    }
}
