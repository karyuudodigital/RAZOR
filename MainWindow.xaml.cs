// =====================================================================================
// MainWindow.xaml.cs
// 
// Main application window for TESLOR, a WPF utility for
// managing plugin load orders and conflict checking for Bethesda games (Morrowind,
// Oblivion, Skyrim, Fallout series, etc.).
//
// This file contains the MainWindow class, which serves as the primary UI controller.
// It handles user interactions, manages the Games and Plugin data models, coordinates
// asynchronous operations (plugin loading, conflict checking), and persists user
// configuration. The MainWindow orchestrates the overall flow of the application,
// including initialization, folder selection, plugin management, and UI state updates.
//
// Program Flow Summary:
// - On startup, the window loads configuration from disk (or creates a new one).
// - It attempts to auto-detect game and config folders for each supported game.
// - The UI is initialized, and the user can select a game, set folders, and load plugins.
// - Plugin loading and conflict checking are performed asynchronously using Tasks to
//   keep the UI responsive. Progress is reported via a progress bar and label.
// - Users can reorder plugins, enable/disable them, and save their load order.
// - Asynchronous operations (plugin loading, conflict checking) are wrapped in Tasks
//   and use IProgress<T> to report progress to the UI thread, ensuring a smooth user
//   experience without blocking the interface.
// - The application uses JSON serialization for configuration persistence, and
//   interacts with the Windows Registry to auto-detect game installations.
//
// MainWindow is the central hub, connecting the UI, data models (Games, Game, Plugin),
// and system resources (filesystem, registry) to provide a seamless load order
// management experience.
//
// =====================================================================================

using Microsoft.Win32;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization.Json;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using Resx = SimpleLoadOrderOrganizer.Resources.Resources;


namespace SimpleLoadOrderOrganizer
{
    /// <summary>
    /// MainWindow is the primary WPF window for TESLOR (SimpleLoadOrderOrganizer).
    /// 
    /// Responsibilities:
    /// - Manages the main user interface for plugin load order organization.
    /// - Handles user input for selecting games, folders, and plugin files.
    /// - Coordinates loading and saving of configuration and plugin data.
    /// - Performs plugin loading and conflict checking asynchronously to keep the UI responsive.
    /// - Interacts with the Games and Plugin data models to reflect and persist user changes.
    /// - Provides feedback and error handling for file, folder, and registry operations.
    /// 
    /// Integration:
    /// - Uses the Games class to represent all supported games and their state.
    /// - Uses the Plugin class to represent individual plugins and their properties.
    /// - Interacts with the Windows Registry to auto-detect game installations.
    /// - Serializes configuration to JSON for persistence.
    /// - Updates the UI based on the current state and user actions.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public partial class MainWindow : System.Windows.Window
    {

        #region FIELDS, PROPERTIES, AND CONSTRUCTOR

        private Games games = new(); // Holds all supported games and their state
        private readonly DataContractJsonSerializer serializer = new(typeof(Games)); // For JSON serialization of config
        int index; // Currently selected game index
        bool conflictCheckLock = false; //prevents conflictCheck from running on combobox change


        public MainWindow(){  InitializeComponent();  }

        #endregion

        #region LOADING AND INITIALIZATION

        //ON WINDOW LOADED
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {

            //checks if config exists, if it doesn't, create it, if it does, deserialize it into class  
            #region CONFIG CHECK

            //if config file exist, read it
            if (System.IO.File.Exists("cfg.json"))
            {
                using var fs = File.OpenRead("cfg.json");

                //try to read it, if it fails, display a message box and recreate config file
                try
                {
                    games = serializer.ReadObject(fs) as Games ?? new Games();
                    index = games!.GameID;
                    fs.Close();
                }
                catch (Exception)
                {
                    System.Windows.Forms.MessageBox.Show($"{Resx.fileError}", $"{Resx.error}");
                    fs?.Close();
                    System.IO.File.Delete("cfg.json");
                    games = new Games();
                    index = games.GameID;
                    SaveConfig();
                }
            }
            //if config doesn't exist, create one
            else
            {
                games = new Games();
                index = games.GameID;
                SaveConfig();
            }

            game.SelectedIndex = index;

            #endregion


            //adds selectionChanged handler, this is becuase selectionChanged is called before the above code when set in XAML, and the handler will then try to access objects that are null 
            game.SelectionChanged += Game_SelectionChanged;


            //searches for folder/plugin.txt paths for each game 
            #region FOLDER SEARCH

            foreach (Game game in games.gamesList)
            {
                //searches for game directories
                #region GAME FOLDER

                //if game directory is unknown, interegate the registry entry and find the directory 
                if (string.IsNullOrEmpty(game.GameFolder))
                {
                    using var key = Registry.LocalMachine.OpenSubKey(game.RegKey!, writable: false);
                    if (key != null)
                    {
                        if (key.GetValue("installed path") is string path &&
                            !string.IsNullOrWhiteSpace(path) &&
                            Directory.Exists(path))
                        {
                            game.GameFolder = path;
                        }
                    }
                }


                #endregion


                //searches for plugin config directories
                #region CONFIG FILE

                //if game directory is unknown, search for it in the default directory
                //..^4 == "length - 4"
                if (game.ConfigFolder == "")
                {

                    //morrowind uses Morrowind.ini in it's game folder to store active plugins so we have to look in the game folder rather than appdata
                    if (game.Name == "The Elder Scrolls III: Morrowind" && System.IO.File.Exists(game.GameFolder + game.DefaultConfigFolder))
                    {
                        game.ConfigFolder = game.GameFolder + game.DefaultConfigFolder;
                        File.Copy(game.ConfigFolder, game.ConfigFolder[..^4] + "_backup.txt", true);
                    }
                    else if ((game.Name != "The Elder Scrolls III: Morrowind" && System.IO.File.Exists(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + game.DefaultConfigFolder)))
                    {
                        game.ConfigFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + game.DefaultConfigFolder;
                        File.Copy(game.ConfigFolder, game.ConfigFolder[..^4] + "_backup.txt", true);
                    }


                }
                else if (System.IO.File.Exists(game.ConfigFolder))
                {
                    File.Copy(game.ConfigFolder, game.ConfigFolder[..^4] + "_backup.txt", true);
                }

                #endregion

            }

            #endregion



            //if both folders exist,remove notice text and start loading plugins
            if (File.Exists(games.gamesList[game.SelectedIndex].ConfigFolder) && Directory.Exists(games.gamesList[game.SelectedIndex].GameFolder))
            {
                //loads plugins and sets DataContext
                LoadPlugins();
            }
            //if not make sure the notice text is visible
            else
            {
                editMasters.IsEnabled = false;
                conflictCheckBox.IsEnabled = false;
                warningLabel.Visibility = Visibility.Visible;
                progressLabel.Content = "©-2025-KARYUUDO-DIGITAL";
            }

        }


        //LOADS PLUGINS
        void LoadPlugins()
        {
            DataContext = null;
            warningLabel.Visibility = Visibility.Hidden;
            SetUIEnabled(false, $"{Resx.loadingPlugins}"); 
            _ = LoadPluginsAsync();
        }


        //SAVES CONFIG TO WORKING DIRECTORY
        public void SaveConfig()
        {
            //Tries to save config by serializing the class into JSON, displays messagebox is there is an exception
            try
            {
                using var fs = File.Create("cfg.json");
                serializer.WriteObject(fs, games);
            }
            catch (Exception) { System.Windows.Forms.MessageBox.Show($"{Resx.saveError}", $"{Resx.error}"); }

        }

        #endregion

        #region ASYNC OPERATIONS

        //GENERAL ASYNC OPERATION RUNNER
        private async Task RunAsyncOperation(Func<IProgress<double>, Task> operation, Action onCompleted, string progressMsg)
        {
            SetUIEnabled(false, progressMsg);
            var progress = new Progress<double>(p => loadingBar.Value = p);
            await operation(progress);
            onCompleted();
        }

        // Async wrapper for plugin loading
        private async Task LoadPluginsAsync(){await RunAsyncOperation(progress => Task.Run(() => games.gamesList[index].LoadPlugins(progress)), BackgroundWorker_OnCompleted, $"{Resx.loadingPlugins}");} 


        // Async wrapper for conflict check
        private async Task CheckConflictsAsync() { await RunAsyncOperation(progress => Task.Run(() => games.gamesList[index].OverlapCheck(progress)),BackgroundWorker_OnCompletedConflict, $"{Resx.checkingForConflicts}");} 


        //WHEN COMBOBOX INDEX CHANGES
        public async void Game_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            conflictCheckLock = true;
            RefreshDataContext(game.SelectedIndex);
            games.GameID = game.SelectedIndex;


            //IF LOAD ORDER IS NULL AND GAME IS VALID, LOAD PLUGINS
            if (games.gamesList[game.SelectedIndex].LoadOrder == null &&
                IsValidGame(game.SelectedIndex))
            {
                index = game.SelectedIndex;
                LoadPlugins();
            }
            //IF GAME IS VALID, SET DATACONTEXT
            else if (IsValidGame(game.SelectedIndex))
            {
                DataContext = games.gamesList[game.SelectedIndex];
                index = game.SelectedIndex;
                editMasters.IsEnabled = true;
                conflictCheckBox.IsEnabled = true;
                warningLabel.Visibility = Visibility.Hidden;
            }
            //IF GAME IS NOT VALID, DISABLE UI ELEMENTS AND SHOW WARNING
            else
            {
                index = game.SelectedIndex;
                DataContext = games.gamesList[game.SelectedIndex];
                editMasters.IsEnabled = false;
                conflictCheckBox.IsEnabled = false;
                warningLabel.Visibility = Visibility.Visible;
                progressLabel.Content = "©-2025-KARYUUDO-DIGITAL";
            }
            conflictCheckLock = false;
        }


        //WHEN CHECK FOR CONFLICTS IS CHECKED
        private async void ConflictCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (!conflictCheckLock)
            {
                foreach (Plugin p in games.gamesList[game.SelectedIndex].LoadOrder)
                    p.Conflicts = null;

                await CheckConflictsAsync();
            }
        }

        #endregion

        #region ASYNC COMPLETION HANDLERS

        //WHEN LOADING PLUGINS FINISHES
        private void BackgroundWorker_OnCompleted()
        {
            //Either starts conflict check or enables UI

            conflictCheckLock = true;
            RefreshDataContext(game.SelectedIndex);

            loadingBar.Value = 0;
            loadingBar.Visibility = Visibility.Hidden;

            if (games.gamesList[game.SelectedIndex].ConflictCheck)
            {
                progressLabel.Content = $"{Resx.checkingForConflicts}";
                progressLabel.Visibility = Visibility.Visible;
                loadingBar.Visibility = Visibility.Visible;
                _ = CheckConflictsAsync();
            }
            else
            {
                SetUIEnabled(true);
            }
        }



        //WHEN CHECKING FOR PLUGIN CONFLICTS FINISHES
        private void BackgroundWorker_OnCompletedConflict()
        {
            //Loads UI after conflict check
            loadingBar.Value = 0;
            warningLabel.Visibility = Visibility.Hidden;
            loadingBar.Visibility = Visibility.Hidden;
            conflictCheckLock = true;

            RefreshDataContext(game.SelectedIndex);
            SetUIEnabled(true);
        }

        #endregion

        #region UI EVENT HANDLERS

        //WHEN LOAD ORDER CHANGED
        private void PluginsBox_Drop(object sender, System.Windows.DragEventArgs e) { games.gamesList[game.SelectedIndex].WasChanged = true; }// when order is changed

        private void CheckBox_Checked(object sender, RoutedEventArgs e) { games.gamesList[game.SelectedIndex].WasChanged = true; }// when plugin is enabled

        private void CheckBox_Unchecked(object sender, RoutedEventArgs e) { games.gamesList[game.SelectedIndex].WasChanged = true; } // when plugin is disabled

        //GAME FOLDER BUTTON
        private void GameFolderBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            string expectedFile = game.SelectedIndex == 0 ? "Data Files" : "Data";

            // Use helper method
            if (TrySelectFolder(out string? selectedPath, expectedFile, false))
            {

                // Set the game folder path
                gameFolderBox.Text = selectedPath!;
                games.gamesList[game.SelectedIndex].GameFolder = selectedPath!;

                // If the game folder is already valid
                if (File.Exists(games.gamesList[game.SelectedIndex].ConfigFolder))
                {
                    LoadPlugins();
                }
                else
                {
                    System.Windows.Forms.MessageBox.Show($"{Resx.pluginConfigError}", $"{Resx.error}");
                }
            }

        }



        //PLUGIN CONFIG FOLDER BUTTTON
        private void PluginsTextBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            string expectedFile = games.GameID != 0 ? "plugins.txt" : "morrowind.ini";

            if (TrySelectFolder(out string? selectedPath, expectedFile, true))
            {
                // Set the config file path
                pluginsTextBox.Text = Path.Combine(selectedPath!, expectedFile);
                games.gamesList[game.SelectedIndex].ConfigFolder = Path.Combine(selectedPath!, expectedFile);

                // If the game folder is already valid
                if (Directory.Exists(games.gamesList[game.SelectedIndex].GameFolder))
                {
                    LoadPlugins();
                }
                else
                {
                    System.Windows.Forms.MessageBox.Show($"{Resx.gameDirectoryError}", $"{Resx.error}");
                }
            }
        }



        //SAVE BUTTON
        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            System.IO.File.Delete("cfg.json");
            SaveConfig();

            MessageBoxResult dialogResult = System.Windows.MessageBox.Show($"{Resx.saveDialogue}", $"{Resx.save}", MessageBoxButton.YesNo);
            if (dialogResult == MessageBoxResult.Yes)
            {
                //if a games load order was changed, saves the loadorder
                foreach (Game g in games.gamesList) { if (g.WasChanged && g.LoadOrder != null) { g.WritePlugins(); } }
            }
            else if (dialogResult == MessageBoxResult.No)
            {
                //if a games load order was changed, saves the loadorder
                if (games.gamesList[game.SelectedIndex].WasChanged) { games.gamesList[game.SelectedIndex].WritePlugins(); }
            }
        }

        //PLAY BUTTON
        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {

           

            //if valid executable path, start the game
            String path = Path.Combine(games.gamesList[game.SelectedIndex].GameFolder!, games.gamesList[game.SelectedIndex].ExePath!);


            if (File.Exists(path))
            {


                //launch exe
                static void launch(string path)
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = path,
                        UseShellExecute = true,  // ensures it runs independently
                        WorkingDirectory = Path.GetDirectoryName(path)
                    };
                    try
                    {
                        Process.Start(startInfo);
                    }
                    catch (Exception ex)
                    {
                        System.Windows.Forms.MessageBox.Show($"{Resx.failedToLaunchError} {Resx.error}: {ex.Message}", $"{Resx.error}");
                    }

                }


                if (File.Exists(Path.Combine(games.gamesList[game.SelectedIndex].GameFolder!, games.gamesList[game.SelectedIndex].ScriptExtender!)))
                {

                    //if script extender found in gamefolder, prompt user to select it

                    MessageBoxResult dialogResult = System.Windows.MessageBox.Show(string.Format(Resx.scriptExtenderPrompt, games.gamesList[game.SelectedIndex].Name), Resx.scriptExtenderDetected, MessageBoxButton.YesNo);
                    if (dialogResult == MessageBoxResult.Yes)
                    {
                        //update exe name to script extender
                        games.gamesList[game.SelectedIndex].ExePath = games.gamesList[game.SelectedIndex].ScriptExtender;
                        SaveConfig();
                        launch(Path.Combine(games.gamesList[game.SelectedIndex].GameFolder!, games.gamesList[game.SelectedIndex].ExePath!));
                    }
                    else if (dialogResult == MessageBoxResult.No)
                    {
                        launch(path);
                    }
                }
                else
                {
                    launch(path);

                }

                
            }
            //else show error message
            else
            {
                System.Windows.Forms.MessageBox.Show(
                   string.Format(Resx.exeNotFound, games.gamesList[game.SelectedIndex].Name),
                    $"{Resx.error}");
            }








        }

        #endregion

        #region HELPER METHODS

        //ENABLE/DISABLE UI ELEMENTS DURING ASYNC OPERATIONS
        private void SetUIEnabled(bool enabled, string progressMsg = "")
        {
            var controls = new System.Windows.Controls.Control[]


            {
        game, saveButton, gameFolderBox, pluginsTextBox,
        editMasters, conflictCheckBox, playButton
            };

            foreach (var control in controls)
            {
                control.IsEnabled = enabled;
            }

            loadingBar.Visibility = enabled ? Visibility.Hidden : Visibility.Visible;
            progressLabel.Content = enabled ? "©-2025-KARYUUDO-DIGITAL" : progressMsg;
        }



        //CHECKS IF GAME FOLDERS ARE VALID
        private bool IsValidGame(int i) =>
            File.Exists(games.gamesList[i].ConfigFolder) &&
            Directory.Exists(games.gamesList[i].GameFolder);


        //REFRESHES DATACONTEXT
        private void RefreshDataContext(int i)
        {
            DataContext = null;
            DataContext = games.gamesList[i];
        }


        //FOLDER/FILE SELECTION DIALOG
        private static bool TrySelectFolder(out string? path, string expectedFile, bool isFile)
        {
            path = null;

            if (isFile)
            {
                // File selection dialog
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    FileName = "Config file",
                    DefaultExt = ".txt",
                    Filter = "Text documents (*.txt;*.ini;*.cfg)|*.txt;*.ini;*.cfg"
                };

                bool? result = dialog.ShowDialog();
                if (result != true)
                    return false;

                if (Path.GetFileName(dialog.FileName).Equals(expectedFile, StringComparison.OrdinalIgnoreCase))
                {
                    path = Path.GetDirectoryName(dialog.FileName) + Path.DirectorySeparatorChar;
                    return true;
                }

                System.Windows.Forms.MessageBox.Show(
                    string.Format(Resx.correctFileNotFound, expectedFile),
                    $"{Resx.error}");
                return false;
            }
            else
            {
                // Folder selection dialog
                using var dialog = new FolderBrowserDialog { InitialDirectory = "C:\\" };
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                    return false;

                var fullPath = Path.Combine(dialog.SelectedPath, expectedFile);
                if (File.Exists(fullPath) || Directory.Exists(fullPath))
                {
                    path = dialog.SelectedPath + Path.DirectorySeparatorChar;
                    return true;
                }

                System.Windows.Forms.MessageBox.Show(
                    string.Format(Resx.couldNotFindDirectory, expectedFile),
                    $"{Resx.error}");
                return false;
            }
        }





        #endregion


        #region WINDOW CONTROL

        //RENABLES WINDOW DRAG
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            DragMove(); // allows dragging
        }


        //WINDOW CONTROL BUTTONS
        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void Maximize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = (WindowState == WindowState.Maximized)
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }





        #endregion

    }

}