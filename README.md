# TESLOR - The Elder Scrolls Load Order Reorganizer
![Screenshot 2023-09-05 015833](https://github.com/george0815/TESLOR/assets/20736715/de66b373-8234-4755-af42-9ab573101a54)




Extremely simple load order editor for mainline Bethesda games. Users can see their load order, reorder it, see mods' master files, see how many records mods overwrite, and check which mods conflict with each other. 

### Games supported:
Morrowind <br/>
Oblivion<br/>
Skyrim<br/>
Skyrim Special Edition<br/>
Fallout 3<br/>
Fallout: New Vegas<br/>
Fallout 4<br/>

# Usage
At first launch, the program will try to automatically find the install path and the config file which stores which plugins are active (Morrowind.ini for Morrowind, plugins.txt for everything else), if it doesn't automatically detect the directories then manually 
add them via the buttons near the top right of the window. Reorder plugins by dragging and dropping them in the desired order. Enable/disable plugins by checking or unchecking the checkbox respectively. "Override Records" means the amount of records a given plugin 
overrides, "Masters" is a list plugins that are needed for a given plugin to function.
### Checking for Conflicts
You can check which plugins overlap with eachother by clicking the "Check for conflicts" checkbox, upon completion the "Conflicts" column will display what plugins conflicts with 
eachother. This process usually takes about a minute or two so it is reccomened to leave it off if all you want to do is edit your load order. 
### Reordering Custom Masters
If you want to edit the load order of custom masters, check the "Edit masters" checkbox. 
 


# Installation
Just extract the zip file and launch the program.



## **IMPORTANT**
Some files have to be loaded no matter what, (e.g., Morrowind.esm, Update.esm, Skyrim.esm), therefore disabling these plugins will be ignored, even if the "Edit masters" option is enabled. Furthurmore, in the case of Skyrim Special Edition and Fallout 4, DLCs 
will always be loaded in a certain order, so changing the load order of DLCs will also be ignored. Finally Creation Club plugins will always be loaded AFTER DLCs but BEFORE custom masters.
 

## Acknowledgements

- [esplugin](https://github.com/Ortham/esplugin): Library for reading esm, esl, and esp files.
