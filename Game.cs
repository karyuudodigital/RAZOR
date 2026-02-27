// =====================================================================================
// Game.cs
//
// Purpose:
// This file contains two closely related types used by the application:
// - `Game` (partial): the per-game model that encapsulates a game's folders, configuration,
//   load-order and the logic to load plugins, write the active plugin list, and detect conflicts.
// - `Games`: a small container and factory that initializes the supported games and stores
//   the last active game index (persisted root object for application configuration).
//
// - `Game.LoadPlugins(IProgress<double>)`:
//   1. Enumerates plugin files (extensions .esm, .esp, .esl) in the game's Data folder.
//   2. For each file, it calls the native parser (via `Plugin`) to get metadata and constructs
//      runtime `Plugin` objects (filename, masters, override counts). The native handle is
//      released as soon as the managed fields are copied.
//   3. Reads the game's active-plugins config file (plugins.txt or Morrowind.ini) and sets
//      `IsActive` accordingly.
//   4. Reports progress to the UI via the provided `IProgress<double>` instance.
//   5. Updates the UI-bound `ObservableCollection<Plugin>` on the UI thread using
//      `App.Current.Dispatcher.Invoke` (ensures collection changes are marshalled to the UI thread).
//   6. Reorders core files and other plugins according to per-game rules (core files first,
//      optional date-sorting for older games).
//
// - `Game.WritePlugins()` writes the game's plugin list back to its configuration format.
//   The method handles per-game differences (Morrowind's GameFile entries, Skyrim/FO4's star
//   prefix for enabled plugins, and the "date-modification" ordering technique for older games).
//
// - `Game.OverlapCheck(IProgress<double>)`:
//   - Performs an O(n^2) comparison across the load order using `Native.DoesOverlap` to detect
//     conflicts between plugin pairs.
//   - Accumulates conflict pairs and applies the results in one batch on the UI thread to avoid
//     cross-thread property updates.
//   - Reports progress periodically to avoid flooding the progress UI.
//
// - `MainWindow` uses `Games` (collection) to populate the UI game selector and persists that
//   configuration to `cfg.json`. When a user selects a game, `Game.LoadPlugins` is invoked (on a
//   background task) and the UI is updated once loading completes.
// - `Plugin` objects returned by `LoadPlugins` are displayed in the UI; changes to `IsActive` or
//   reorder operations cause `WasChanged` to be set and are persisted by `Game.WritePlugins`.
//
// - Threading and UI safety: heavy I/O and native parsing run off the UI thread. Any writes to
//   `ObservableCollection` or UI-bound properties are done via `Dispatcher.Invoke` to ensure they
//   execute on the UI thread and avoid concurrency exceptions.
// - Performance: `OverlapCheck` is quadratic in the number of plugins. It uses native `DoesOverlap`
//   for the actual overlap test; consider profiling `DoesOverlap` if the conflict-check step is slow.
// - Serialization: `Game` is a data contract used when serializing the `Games` container to JSON.
//
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;


namespace RAZOR
{

    /// <summary>
    /// Represents a single game's configuration and runtime state:
    /// - Folder locations (game data folder and configuration file)
    /// - Information about how to read and write the game's active plugin list
    /// - The observable `LoadOrder` that the UI binds to
    /// - Methods for loading plugins, writing plugin files, and detecting conflicts
    /// 
    /// Important behaviour:
    /// - `LoadPlugins` performs file system scanning and native parsing off the UI thread
    ///   and then marshals the populated `ObservableCollection<Plugin>` to the UI thread.
    /// - Uses `IProgress<double>` to report progress while loading or checking conflicts.
    /// - Implements property notifications via `INotifyPropertyChanged` for UI binding.
    /// </summary>
    [DataContract]
    internal partial class Game
    {

        #region PROPERTIES AND FIELDS

        [DataMember(Name = "Game Folder")]
        public string? GameFolder { get; set; }

        [DataMember(Name = "Config Folder")]
        public string? ConfigFolder { get; set; }

        [DataMember(Name = "Exe Path")]
        public string? ExePath { get; set; }

        [DataMember(Name = "Script Extender")]
        public string? ScriptExtender { get; set; }

        [DataMember(Name = "Default Config Folder")]
        public string? DefaultConfigFolder { get; set; }

        [DataMember(Name = "Name")]
        public string? Name { get; set; }

        [DataMember(Name = "Registry Key")]
        public string? RegKey { get; set; }

        [DataMember(Name = "ID")]
        public Games.GameIDs Id { get; set; }

        [DataMember(Name = "Load Plugins")]
        public bool LoadOnStart { get; set; }


        private bool _conflictCheck;
        [DataMember(Name = "Check for Conflcits")]
        public bool ConflictCheck
        {
            get => _conflictCheck;
            set => SetField(ref _conflictCheck, value);
        }

        private bool _editMaster;
        [DataMember(Name = "Edit Master")]
        public bool EditMaster
        {
            get => _editMaster;
            set => SetField(ref _editMaster, value);
        }


       

        // INotifyPropertyChanged 
        public event PropertyChangedEventHandler? PropertyChanged;

        // UI-bound collection
        public ObservableCollection<Plugin> LoadOrder { get; set; } = [];

        // these are files that will always be loaded regardless of whether they are checked in the launcher/in the config
        [DataMember(Name = "Mandatory Files")]
        public List<string> MandatoryFiles { get; set; } = [];

        // indicates if any changes were made to the load order or active plugins
        [DefaultValue(false)]
        public bool WasChanged { get; set; }


        #endregion

        #region INotifyPropertyChanged IMPLEMENTATION

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            this.WasChanged = true;
            return true;
        }

        #endregion

        #region REGEXES

        //regexes for checking for anniversary edition plugins

        [GeneratedRegex(@"^cc[a-zA-Z]{6}\d{3}")]
        private static partial Regex CreationClubCheck1();
        [GeneratedRegex(@"^cc[a-zA-Z]{5}\d{4}")]
        private static partial Regex CreationClubCheck2();

        #endregion

        #region PLUGIN OPERATIONS

        //LOADS PLUGINS
        public void LoadPlugins(IProgress<double> progress)
        {

            #region LOAD ALL PLUGINS FROM DATA FOLDER
            this.LoadOrder = [];// clear existing

            string directory = ""; //hold's plugins directory for a given game


            //Morrowind uses "Data Files" as it's folder
            if (this.Name == "The Elder Scrolls III: Morrowind") { directory = this.GameFolder + "\\Data Files"; }
            //for every other game, just use the Data folder
            else { directory = this.GameFolder + "Data"; }





            DirectoryInfo info = new(directory);
            //gets all plugin files
            var filesList = from fullFilename in info.GetFiles().OrderBy(s => s.LastWriteTime).Where(s => s.Name.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || s.Name.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) || s.Name.EndsWith(".esl", StringComparison.OrdinalIgnoreCase))
                            select fullFilename;



            //reads config file

            IEnumerable<string> lines = [];
            if (!string.IsNullOrEmpty(this.ConfigFolder))
            {
                lines = File.ReadLines(this.ConfigFolder);
            }
            Plugin tempPlugin;
            List<Plugin> tempPlugins = [];

            //add a checkbox for each plugin
            foreach (FileInfo file in filesList)
            {


                //creates plugin
                tempPlugin = new Plugin(file.FullName, (int)this.Id);

                //if invalid plugin, skip
                if (tempPlugin.invalid == true) { continue; }

                tempPlugin.MastersString = String.Join("\n", [.. tempPlugin.Masters!]);
                tempPlugin.DateModified = file.LastWriteTime;
                tempPlugin.FilePath = file.FullName;

                try
                {
                    //checks all files that are required for loading
                    if (this.MandatoryFiles.Contains(file.Name) || (file.Name.Contains("_ResourcePack") && this.Id == Games.GameIDs.SkyrimSE) || CreationClubCheck1().IsMatch(file.Name) || CreationClubCheck2().IsMatch(file.Name)) { tempPlugin.IsActive = true; tempPlugin.IsReq = true;}

                    //Morrowind use's Morrowind.ini instead of plugins.txt so we have to acocunt for that
                    if (this.Id == Games.GameIDs.Morrowind)
                    {
                        string tempLine = "";
                        //for every line
                        foreach (var line in lines)
                        {
                            //gets rid of plugin prefix
                            if (line.Contains("GameFile")) { tempLine = line[(line.LastIndexOf('=') + 1)..]; }

                            //sets checkbox to checked if plugin is active (ie: if plugin name was found in plugins.txt/Morrowind.ini)
                            if (file.Name == tempLine) { tempPlugin.IsActive = true; }

                        }
                    }
                    else if (this.Id == Games.GameIDs.SkyrimSE || this.Id == Games.GameIDs.Fallout4)
                    {
                        string tempLine = "";

                        //for every line
                        foreach (var line in lines)
                        {
                            //gets rid of plugin prefix
                            if (line[0] == '*') { tempLine = line[(line.IndexOf('*') + 1)..]; }
                            else { tempLine = line; }

                            if (file.Name == tempLine)
                            {
                                tempPlugin.FoundInLoadOrder = true;
                                //sets checkbox to checked if plugin is active (ie: if plugin name was found in plugins.txt/Morrowind.ini)
                                if (line[0] == '*') { tempPlugin.IsActive = true; }

                            }

                        }

                        

                    }
                    else
                    {
                        //sets checkbox to checked if plugin is active (ie: if plugin name was found in plugins.txt/Morrowind.ini)
                        foreach (var line in lines) { if (file.Name == line) { tempPlugin.IsActive = true; } }

                    }
                }
                catch (Exception)
                {
                    System.Windows.Forms.MessageBox.Show("Error opening plugin, this may be because the file is open in another program, or that his program wasn't ran as administrator...", "Error");
                }


           
                tempPlugins.Add(tempPlugin);

                var fileCount = filesList.Count();


                progress?.Report((double)tempPlugins.Count / fileCount);
                tempPlugin.Dispose();


            }


            // Then, once:
            App.Current.Dispatcher.Invoke(() => {
                LoadOrder = new ObservableCollection<Plugin>(tempPlugins);
            });


            //reorders
            if (this.Id == Games.GameIDs.SkyrimSE || this.Id == Games.GameIDs.Fallout4 || this.Id == Games.GameIDs.Skyrim)
            {
                //for every line
                foreach (var line in lines)
                {

                    string tempLine = "";
                    foreach (FileInfo file in filesList)
                    {

                        //reinserts plugins into loadorder lists
                        tempLine = line[(line.IndexOf('*') + 1)..];
                        if (((file.Name == tempLine) && line[0] == '*') || (line == file.Name))
                        {


                            App.Current.Dispatcher.Invoke((Action)delegate // <--- HERE
                            {
                                tempPlugin = LoadOrder.FirstOrDefault(x => x.PluginFilename == file.Name)!;
                                if (tempPlugin != null)
                                {
                                    LoadOrder.Remove(tempPlugin);
                                    LoadOrder.Insert(0, tempPlugin);
                                }

                            });

                        }



                    }

                }
            }
            App.Current.Dispatcher.Invoke(() =>
            {
                static string[] GetCoreOrder(Games.GameIDs id) => id switch
                {
                    Games.GameIDs.Skyrim => ["Skyrim.esm", "Update.esm"],
                    Games.GameIDs.SkyrimSE => ["Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm"],
                    Games.GameIDs.Fallout4 => [ "Fallout4.esm", "DLCRobot.esm", "DLCworkshop01.esm", "DLCCoast.esm",
                                           "DLCworkshop02.esm", "DLCworkshop03.esm", "DLCNukaWorld.esm" ],
                    Games.GameIDs.Fallout3 => ["Fallout3.esm"],
                    Games.GameIDs.FalloutNV => ["FalloutNV.esm"],
                    Games.GameIDs.Morrowind => ["Morrowind.esm"],
                    Games.GameIDs.Oblivion => ["Oblivion.esm"],
                    _ => []
                };

                var coreOrder = GetCoreOrder(Id);
                if (coreOrder.Length == 0)
                    return;

                // Split list into “core” and “non-core” parts
                var corePlugins = LoadOrder
                    .Where(p => coreOrder.Contains(p.PluginFilename!, StringComparer.OrdinalIgnoreCase))
                    .OrderBy(p => Array.IndexOf(coreOrder, p.PluginFilename!))
                    .ToList();

                var otherPlugins = LoadOrder
                    .Where(p => !coreOrder.Contains(p.PluginFilename!, StringComparer.OrdinalIgnoreCase)).OrderBy(p => p.FoundInLoadOrder).OrderBy(p => p.IsReq).OrderBy(p => p.IsMaster).Reverse()
                    .ToList();

                // For certain games, sort *only* other plugins by DateModified (the old behavior)
                if (Id is Games.GameIDs.Morrowind or Games.GameIDs.Oblivion or Games.GameIDs.Fallout3 or Games.GameIDs.FalloutNV)
                {
                    otherPlugins = [.. otherPlugins
                        .OrderBy(p => p.DateModified)
                        .ThenBy(p => !p.IsMaster)];
                }

                // Combine the sorted core files with the original (or date-sorted) others
                var newOrder = new ObservableCollection<Plugin>(corePlugins.Concat(otherPlugins));

                LoadOrder = newOrder;
            });


            #endregion



        }

        //WRITES PLUGINS
        public void WritePlugins()
        {



            //sets release date
            var dateModified = this.Id switch
            {
                Games.GameIDs.Morrowind => new DateOnly(2002, 5, 1),
                Games.GameIDs.Oblivion => new DateOnly(2006, 3, 20),
                Games.GameIDs.Fallout3 => new DateOnly(2008, 10, 28),
                Games.GameIDs.FalloutNV => new DateOnly(2010, 10, 19),
                _ => new DateOnly(2011, 11, 11),
            };

            //declared string that will hold config text
            string fileString = "";


            if (this.Id == Games.GameIDs.Morrowind)
            {
                //Opens morrowind config file
                try
                {
                    {
                        //copies every line except game files
                        IEnumerable<string> lines = [];
                        if (!string.IsNullOrEmpty(this.ConfigFolder))
                        {
                            lines = File.ReadLines(this.ConfigFolder);
                        }

                        //for every line
                        foreach (var line in lines)
                        {
                            //gets rid of plugin prefix
                            if (!line.Contains("GameFile") && !line.Contains("[Game Files]")) { fileString += line + Environment.NewLine; }

                        }
                        fileString += "[Game Files]" + Environment.NewLine;
                    }
                }
                catch (Exception)
                {
                    System.Windows.Forms.MessageBox.Show("Error opening Morrowind.ini, this may be because the file is open in another program, or that his program wasn't ran as administrator...", "Error");
                }
            }

            //iterates through loadorder
            int counter = 0; //used for morrowind
            foreach (Plugin p in this.LoadOrder)
            {


                if (this.Id == Games.GameIDs.Morrowind || this.Id == Games.GameIDs.Oblivion || this.Id == Games.GameIDs.Fallout3 || this.Id == Games.GameIDs.FalloutNV)
                {


                    // Sets date modified (old game's loadorder is determined by the plugins last write time)
                    dateModified = dateModified.AddDays(1);
                    File.SetLastWriteTime(p.FilePath!, dateModified.ToDateTime(TimeOnly.MinValue));




                    // write name if active, nothing inactive (handle morrowind)
                    if (p.IsActive && this.Id != Games.GameIDs.Morrowind) { fileString += p.PluginFilename + Environment.NewLine; }
                    else if (p.IsActive && this.Id == Games.GameIDs.Morrowind)
                    {
                        fileString += "GameFile" + counter + "=" + p.PluginFilename + Environment.NewLine;
                        counter++;
                    }

                }
                else if (this.Id == Games.GameIDs.SkyrimSE || this.Id == Games.GameIDs.Fallout4)
                {

                    // write down * then name if active, just name if inactive
                    if (p.IsActive) { fileString += "*" + p.PluginFilename + Environment.NewLine; }
                    else { fileString += p.PluginFilename + Environment.NewLine; }

                }
                else
                {

                    // write name if active, nothing inactive
                    if (p.IsActive) { fileString += p.PluginFilename + Environment.NewLine; }

                }


            }


            //Saves loadorder
            try { File.WriteAllText(this.ConfigFolder!, fileString); }
            catch (Exception)
            {
                System.Windows.Forms.MessageBox.Show("Error saving plugins.txt or Morrowind.ini, this may be because the file is open in another program, or that his program wasn't ran as administrator...", "Error");
            }

        }

        //CHECKS FOR MOD CONFLICTS
        public void OverlapCheck(IProgress<double> progress)
        {
            var loadOrder = LoadOrder;
            int total = loadOrder.Count;
            int totalComparisons = (total * (total - 1)) / 2;
            int counter = 0;

            // Use a HashSet for O(1) lookup
            HashSet<string> checkedPlugins = [];

            // Cache base files to skip
            HashSet<string> basePlugins = new(StringComparer.OrdinalIgnoreCase)
            {
                "Skyrim.esm", "Update.esm",
                "Morrowind.esm",
                "Oblivion.esm",
                "FalloutNV.esm", "Fallout3.esm", "Fallout4.esm"
            };

            // Store all conflict results locally first
            var conflictPairs = new List<(Plugin, Plugin)>();

            for (int i = 0; i < total; i++)
            {
                var p1 = loadOrder[i];
                if (basePlugins.Contains(p1.PluginFilename!))
                    continue;

                for (int j = i + 1; j < total; j++) 
                {
                    var p2 = loadOrder[j];
                    if (basePlugins.Contains(p2.PluginFilename!))
                        continue;

                    if (Native.DoesOverlap((int)Id, p1.FilePath!, p2.FilePath!))
                    {
                        conflictPairs.Add((p1, p2));
                    }

                    counter++;
                    if (counter % 10 == 0) // don’t flood the progress bar
                        progress?.Report((double)counter / totalComparisons);
                }

                checkedPlugins.Add(p1.PluginFilename!);
            }

            // Now apply conflict results on UI thread in one go
            if (conflictPairs.Count > 0)
            {
                App.Current.Dispatcher.Invoke(() =>
                {
                    foreach (var (p1, p2) in conflictPairs)
                    {
                        if (!string.IsNullOrEmpty(p1.Conflicts))
                            p1.Conflicts += "\n";
                        if (!string.IsNullOrEmpty(p2.Conflicts))
                            p2.Conflicts += "\n";

                        p1.Conflicts += p2.PluginFilename;
                        p2.Conflicts += p1.PluginFilename;
                    }
                });
            }

            progress?.Report(1.0);
        }


        #endregion
        
    }


    /// <summary>
    /// Container for all supported games. This is the root object serialized to `cfg.json`.
    /// - `gamesList` holds the per-game `Game` objects the UI enumerates.
    /// - `GameID` persists the last selected game index across sessions.
    /// The constructor initializes the known games with default registry keys, config paths and
    /// mandatory/core files for each supported title.
    /// </summary>
    [DataContract]
    internal class Games
    {

        internal enum GameIDs
        {
            Morrowind = 0,
            Oblivion = 1,
            Skyrim = 2,
            SkyrimSE = 3,
            Fallout3 = 4,
            FalloutNV = 5,
            Fallout4 = 6
        }



        [DataMember(Name = "Games")]
        public ObservableCollection<Game> gamesList = [];

        [DataMember(Name = "Last Active Game")]
        public int GameID { get; set; }

        public Games()
        {

            gamesList.Add(new Game { Name = "The Elder Scrolls III: Morrowind", ConfigFolder = "", GameFolder = "", ScriptExtender = "", RegKey = "SOFTWARE\\WOW6432Node\\Bethesda Softworks\\Morrowind", DefaultConfigFolder = "\\Morrowind.ini", Id = GameIDs.Morrowind, EditMaster = false, ConflictCheck = false, MandatoryFiles = ["Morrowind.esm"], ExePath = "Morrowind.exe" }); 
            gamesList.Add(new Game { Name = "The Elder Scrolls IV: Oblivion", ConfigFolder = "", GameFolder = "", ScriptExtender = "obse_loader.exe", RegKey = "SOFTWARE\\WOW6432Node\\Bethesda Softworks\\Oblivion", DefaultConfigFolder = "\\AppData\\Local\\Oblivion\\Plugins.txt", Id = GameIDs.Oblivion, EditMaster = false, ConflictCheck = false, MandatoryFiles = ["Oblivion.esm"], ExePath = "Oblivion.exe" });
            gamesList.Add(new Game { Name = "The Elder Scrolls V: Skyrim", ConfigFolder = "", GameFolder = "", ScriptExtender = "skse_loader.exe", RegKey = "SOFTWARE\\WOW6432Node\\Bethesda Softworks\\Skyrim", DefaultConfigFolder = "\\AppData\\Local\\Skyrim\\plugins.txt", Id = GameIDs.Skyrim, EditMaster = false, ConflictCheck = false, MandatoryFiles = ["Skyrim.esm", "Update.esm"], ExePath = "TESV.exe" });
            gamesList.Add(new Game { Name = "The Elder Scrolls V: Skyrim – Special Edition", ConfigFolder = "", GameFolder = "", ScriptExtender = "skse64_loader.exe", RegKey = "SOFTWARE\\WOW6432Node\\Bethesda Softworks\\Skyrim Special Edition", DefaultConfigFolder = "\\AppData\\Local\\Skyrim Special Edition\\Plugins.txt", Id = GameIDs.SkyrimSE, EditMaster = false, ConflictCheck = false, MandatoryFiles = ["Skyrim.esm", "Update.esm", "Dawnguard.esm", "Dragonborn.esm", "HearthFires.esm"], ExePath = "SkyrimSE.exe" });
            gamesList.Add(new Game { Name = "Fallout 3", ConfigFolder = "", GameFolder = "", ScriptExtender = "fose_loader.exe", RegKey = "SOFTWARE\\WOW6432Node\\Bethesda Softworks\\Fallout3", DefaultConfigFolder = "\\AppData\\Local\\Fallout3\\plugins.txt", Id = GameIDs.Fallout3, EditMaster = false, ConflictCheck = false, MandatoryFiles = ["Fallout3.esm"], ExePath = "Fallout3.exe" });
            gamesList.Add(new Game { Name = "Fallout: New Vegas", ConfigFolder = "", GameFolder = "", ScriptExtender = "nvse_loader.exe", RegKey = "SOFTWARE\\WOW6432Node\\Bethesda Softworks\\falloutnv", DefaultConfigFolder = "\\AppData\\Local\\FalloutNV\\plugins.txt", Id = GameIDs.FalloutNV, EditMaster = false, ConflictCheck = false, MandatoryFiles = ["FalloutNV.esm"], ExePath = "FalloutNV.exe" });
            gamesList.Add(new Game { Name = "Fallout 4", ConfigFolder = "", GameFolder = "", ScriptExtender = "f4se_loader.exe", RegKey = "SOFTWARE\\WOW6432Node\\Bethesda Softworks\\Fallout4", DefaultConfigFolder = "\\AppData\\Local\\Fallout4\\Plugins.txt", Id = GameIDs.Fallout4, EditMaster = false, ConflictCheck = false, MandatoryFiles = ["Fallout4.esm", "DLCRobot.esm", "DLCworkshop01.esm", "DLCCoast.esm", "DLCworkshop02.esm", "DLCworkshop03.esm", "DLCNukaWorld.esm"], ExePath = "Fallout4.exe" });
            GameID = (int)GameIDs.SkyrimSE;

        }
    }
}