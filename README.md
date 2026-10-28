# ProJict

Project to-do lists for Windows. Each project has categories, and when you finish an item it moves to that category's **DONE** list. You still get a clear list of what's left and a record of what's done.

## Download

Get **`ProJict-Setup-x.y.z.exe`** from [Releases](https://github.com/preseznik/projict/releases/latest) and run it.

- Windows 10 or 11 (64-bit).
- Installs for your user account only, so it needs no administrator rights.
- ProJict checks for new versions when it starts and can install them for you (**Settings → Updates**).

## Getting started

1. On first start, choose the folder where ProJict keeps your projects.
2. Create a project with **+** at the bottom of the sidebar, or bring in existing text lists with **Import** (see below).
3. Type into **Add item…** under a category and press Enter. Check an item off and it moves to the category's DONE list.

## Keyboard

| Keys | Action |
| --- | --- |
| Enter | new item below |
| Shift+Enter | new line inside an item |
| Tab / Shift+Tab | make it a sub-item / move it back out |
| Ctrl+Enter | mark done / not done |
| Ctrl+I | mark in progress |
| Alt+↑ / Alt+↓ | move the item up or down |
| Ctrl+Shift+Del | delete the item |
| Ctrl+Z / Ctrl+Y | undo / redo |
| Ctrl+F | filter the current project |
| Ctrl+K | search all projects |
| Ctrl+Space | select items for **Copy prompt** |

Start an item with `BUG:`, `ADD:`, `ISSUE:` or `CHANGE:` to tag it. Drag items by their handle to reorder them or move them to another category.

## Text files

ProJict reads and writes simple text lists like this:

```
HUD
- an open item
	- a sub-item
-+ an item in progress
+ a finished item
```

- **Import** reads `TODO-<name>.txt`, `DONE-<name>.txt`, `WORK-<name>.txt` and similar files from a folder. It shows a preview first and never changes the files. The original text stays available in each project's **Archive** tab.
- **Export** writes `TODO-<name>.txt` and `DONE-<name>.txt`. Under **Settings → TXT mirror** ProJict can keep a folder of these files up to date automatically. It never overwrites files it didn't create without asking.

## More

- **Finding projects**: type in **Filter projects** above the sidebar list or on the dashboard to narrow it down. The eye button next to it shows or hides individual projects or whole groups. The sidebar and the dashboard each remember their own choice. Click a group heading (Personal, Work, …) to collapse it.
- **Dashboard**: progress, open bugs and stale projects across all projects, plus everything finished in the last week.
- **Inbox and quick capture**: press **Ctrl+Alt+J** anywhere (changeable in Settings) to jot down an item or idea. Turn an idea into a project from its right-click menu.
- **Copy prompt**: copies selected items as a ready-made prompt for AI coding assistants. The prompt includes the project's folder, GitHub link and your own standing instructions (set in **Details**).
- **Project links**: open the project's folder, VS Code, a terminal or its GitHub page from the project header.
- **Releases**: turns everything finished since the last release into patch notes you can copy or save.
- **Notes**: free-text notes per project.

## Your data

Projects are saved as JSON files in the folder you chose, with automatic backups in its `backups` folder. Uninstalling ProJict leaves your data where it is.

## Building from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and [Inno Setup 6](https://jrsoftware.org/isinfo.php). Then run:

```
.\build.ps1
```

The installer is written to `artifacts\`.
