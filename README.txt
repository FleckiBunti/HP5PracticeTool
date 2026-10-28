HP5 Practice Tool
====================================

For Harry Potter and the Order of the Phoenix (PC), speedrun hp.exe only (MD5 4C852B5E46484B5600C309B3867AFC9E).

Use
---
1. Start the game and the tool.
2. While playing:
     F5  = save a state
     F6  = quick load: moves Harry back to the state's position and facing (same area only, instant)
     F7  = fly mode on/off
     F8  = full load: puts the state's save back, reloads it through the game's own menu steps, then moves Harry to the state's position
   With the "Level select" tab open, F8 only loads the selected level instead.
3. Double-click a state for a full load; "Rename" (or click the name twice) to rename it.
4. "Import save..." adds any HP5 save file as a state

Fly mode (noclip)
-----------------
F7 turns it on:
     W A S D   = move
     Space     = up
     Z         = down
     Shift     = faster
F7 again turns it off and drops Harry.

Saves
-------------
Full load replaces the game's slot 1 save. Before every full load the tool keeps a copy of what was in slot 1:
- saves\original-save-backup.HPOOTP: the save from before the tool first replaced it
- backups\slot1-<date>-<time>.HPOOTP: the last 10 saves that were replaced.

Build
-----
build.bat compiles src\*.cs with the C# compiler included in Windows (.NET Framework 4).

Verifying a download
--------------------
Every file in a release can be verified:
     gh attestation verify HP5PracticeTool.zip --repo FleckiBunti/HP5PracticeTool
