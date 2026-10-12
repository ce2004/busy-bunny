Busy Bunny

A to-do app for Windows, made for NVDA and braille displays first. Easy to start, hard to get overwhelmed by, forgiving when you fall behind.

This is an early version: the Today tab with sample tasks, so the main screen can be tried and adjusted before the rest is built.


Getting it

Go to the Releases page: github.com/ce2004/busy-bunny/releases
On an ARM laptop, like a Surface with a Snapdragon chip, download BusyBunny-arm64.exe.
On any other Windows laptop, download BusyBunny-x64.exe.
Put it somewhere of your own, like your Documents folder, and open it. Nothing else needs installing.
If Windows SmartScreen says it does not recognise the app, choose More info, then Run anyway.

To update: Help menu, Check for updates. Busy Bunny shows what is new in every version you are missing, then downloads the new one, restarts, and keeps all your tasks. It also checks quietly every 30 seconds and as soon as the internet comes back, and tells you once when there is a new version and how many versions behind you are. Options, Check for updates automatically turns that off.


Priority scales

Each list can have its own priority scale. Options, Priority scales: choose a list, then a scale from the library (Coffee, Space, Burner and more), a custom one with your own levels, or None. The Levels field shows the order. Tabs show the scale after the list's name, like Homework (Coffee).


The main screen

From the top:
The summary line, like: 3 of 8 done today, test Friday, next meeting at 2, a Hop to it day.
Today's progress and this week's progress, by effort.
The tabs: Today, Focus, Upcoming, then Homework, Academic, Meetings, Projects, Personal Immediate and Personal.
Under the tabs, two pick menus for the task you chose: Status and Move to. Arrow to what you want and press Enter. Nothing changes until you press Enter or leave the menu.

Each task reads as one line, most important first: tag, name, status, priority, due date. For example: PSY, chapter 4 reading, Today, Triple shot 1, due Thursday.


Keys

Alt opens the menus.
Control Tab and Control Shift Tab, or Control Page Down and Control Page Up: next and previous tab, straight to its tasks.
The tab row: Shift Tab from a tab's first list, then Left and Right Arrow move through the tabs.
Shift F1: view the changelog.
Control N: quick add a task.
Enter on a task: go to the Task field, to read it by character or by word with the arrows. Escape goes back to the list.
F2: edit the chosen task, its name or anything else.
Control D: mark the chosen task done.
Control 1 to Control 6: Today, This week, Next week, Done not submitted, Waiting on someone, Done.
Control Z: undo.
Delete: delete the chosen task. Control Z brings it back.
Control R: read my day.
Control Shift S: short mode on or off.
The Applications key or Shift F10 on a task: everything you can do to it.


Sounds

Sounds play on real instruments: a Steinway piano, bowed strings, plucked strings, harp, glockenspiel and marimba. Options, Sounds picks a sound for each event, with its own volume apart from NVDA's. You hear each sound as you arrow to it. Options, Sounds on turns them all off.


Your tasks

Busy Bunny saves after every change, in your AppData folder (Help, About Busy Bunny says exactly where). The first save each day also keeps a dated copy in the Backups folder next to it, the last 30 days. File, Open the backups folder shows them.


Building it yourself

You need the .NET 8 SDK. Run build.bat for an x64 copy in bin\x64, or build.bat full for both ARM64 and x64. BusyBunny.exe --selftest checks the sounds, dates, saving and the main window, and writes the result to busybunny-selftest.txt in your TEMP folder.
Pushing a tag like v0.2.0 makes GitHub build both copies and publish them as a release, with that version's lines from CHANGES.txt as the notes.
