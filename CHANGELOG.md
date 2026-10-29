# Changelog

## 0.1.13 (2026-10-29)

- **Sharing**: other people on your network can work in your projects live from their own ProJict. Turn it on under **Sharing** (the new button below the sidebar), create an invite with a role and the groups they'll see, and give them the code. They connect with your address and the code, and you let them in, deny or block them from a bar that appears. Their changes show on your board as they make them, and yours on theirs; when two people change different things at once, both changes are kept.
- Roles: **Viewer**, **Commenter**, **Reporter** (adds items and comments, for testers or clients) and **Editor** (changes everything except a project's name, group, status and folder). Your ProJict checks every change against the role, and signs comments and history with the person's name. Change roles, block or remove people at any time.
- Connections are encrypted, and ProJict remembers each host's fingerprint and refuses to connect if it changes. Badges show who has which project open.
- **GitHub issues**: a project's Details tab can import its repository's open issues as items of a GITHUB category (labels bug and enhancement become BUG and ADD). Each item shows its issue number, and pull requests that mention the issue ("Fixes #12") show next to it, coloured by state. A click opens them on GitHub.
- Issues stay in sync when ProJict starts or with **Sync now**: an issue closed or reopened on GitHub closes or reopens its item, and a new title renames it unless you changed the text. An item you delete isn't imported again.
- Optionally, finishing an item closes its issue on GitHub and reopening it reopens the issue (needs the GitHub CLI sign-in from Settings › GitHub).
- Search, the board filter and Copy prompt know the issue numbers, and AI agents see them too.
- A category's name shows its number of open items in brackets, like "Done (7)" does for finished ones.
- **Red dots** for what waits for you: on the discussion chip of an item or category with comments you haven't read, on a group heading whose discussion has new comments, on a folded category or folded Done list with an unread comment or an agent's work to review inside, and next to the project in the sidebar. Opening the discussion clears them. Comments from before this version count as read.

- **Feed** (under Dashboard): a timeline of everything that happened across your projects. It shows items added and finished, AI agents' check-outs and resolutions, reviews, priority and tag changes, comments, releases, notes and new projects. Filter by project, what happened, who did it and period, or search. A click opens the item, discussion, release or note.
- Pictures open in ProJict's own viewer, where you can mark them up: pen, highlighter, arrows, rectangles and ellipses in six colours. **Save copy** adds the marked-up picture as a new attachment; the original is never changed.
- Projects show badges for the AI agents working in them, in the sidebar and on dashboard cards.
- Items have a discussion button next to the ⋯ when you point at them.
- Paste a screenshot or copied files into a category's **Add item** box (or drop files on it) to add an item with them attached.
- ProJict also checks for updates every 4 hours while it's open.
- Settings › AI agents & comments has setup for OpenAI Codex, OpenCode and other MCP clients as well as Claude Code, folded away under "Connect an AI agent".

## 0.1.11 (2026-10-29)

- Fixed: right-clicking an item's text showed only Cut / Copy / Paste instead of the item's menu (priority, tag, discussion and more). Right-click anywhere on an item now opens its menu, which still has Cut / Copy / Paste.
- A ⋯ button appears on the right of an item when you point at it, for the same menu.

## 0.1.10 (2026-10-29)

- Priorities: urgent, high, medium or low. Set one from an item's right-click menu, its priority chip, or with Alt+1 to Alt+4 (Alt+0 removes it).
- The board's new Priority button sorts open items by priority and shows only items of some priority. Sorting only changes what you see; exports keep your own order.
- AI agents can read, set, filter and sort by priority, and Copy prompt mentions it.
- Fixed: the menu behind a category's "…" button showed boxes instead of text.

## 0.1.9 (2026-10-29)

- AI agents such as Claude Code can work in ProJict (over MCP). Copy the command from Settings › AI agents & comments and run it once. Agents find the project for the folder they work in, check items out, complete them with a resolution and screenshots, add items, attach files, write notes and draft release notes. They never delete anything, and Ctrl+Z undoes their changes.
- An item an agent checked out can't be changed by another agent unless it takes it over, which is written to the item's history. Right-click › Release check-out frees an item an agent left behind.
- Everything is signed: item history, notes and releases show who did them, you or which agent. Agents can add a label to their name so parallel sessions can be told apart.
- Discussions with threaded replies on items, categories and groups, for you and AI agents. Open one from an item's right-click menu or its comment chip, the speech bubble on a category, or right-click › Discussion on a group in the sidebar.
- Under each item, a chip for everyone who worked on it; click one to show only what they worked on.
- Items an agent finished show a "review" chip until you accept them. The dashboard lists everything waiting for review and recent agent activity, and the state filter has "Needs review".
- Notes can have attached files.
- Your name for comments and history is set in Settings (it defaults to your Windows user name).
- Settings scroll on short screens, and the board's toolbar shows only icons when it's narrow.

## 0.1.8 (2026-10-29)

- Attach files and images to items: drag files onto an item, paste a screenshot or copied files with Ctrl+V, or right-click › Attach files…. Images show as thumbnails under the item, and a click opens the file.
- Copy prompt lists the paths of attached files, so an AI assistant can open them.

## 0.1.7 (2026-10-29)

- Right-click an item's checkbox to mark it as to do, in progress or done.

## 0.1.6 (2026-10-29)

- New state filter next to the tag filter on the board: show only items that are to do, in progress or done.

## 0.1.5 (2026-10-28)

- Checks for a new version every time it starts (before, at most once a day).
- Settings › Updates has its own Install update button when a new version is found.
- Hidden projects are now hidden in both the sidebar and the dashboard, from either eye button or the right-click menu.
- GitHub releases: enter your GitHub account in Settings › GitHub (optionally with the GitHub CLI sign-in for private repositories). Projects are linked to repositories with the same name, dashboard cards show the latest release, and the Released filter lists projects with a published release.

## 0.1.4 (2026-10-28)

- Your own project groups next to Personal and Work: add them with the new folder button under the sidebar, rename or delete them by right-clicking their heading. Deleting a group moves its projects to Personal.
- Right-click a project › Move to group, including straight into a new group.
- New groups show in the sidebar even while they're empty.

## 0.1.3 (2026-10-28)

- Right-click a project in the sidebar or on the dashboard to hide it.
- Right-click a project and choose "Copy categories from" to reuse another project's categories (without its items). Ctrl+Z undoes it.
- "Collapse all" / "Expand all" button above a project's categories.

## 0.1.2 (2026-10-28)

- Filter box and show/hide list for projects in the sidebar and on the dashboard.
- Collapsible project groups in the sidebar.

## 0.1.1 (2026-10-28)

First public release.

- Projects with categories. Finished items move to the category's DONE list.
- Keyboard-friendly lists with sub-items, tags (BUG, ADD, …), an in-progress state, drag and drop, and undo.
- TODO and DONE shown below each other or side by side, with a text and tag filter.
- Import of existing TODO / DONE text files, with a preview first. The original files are never changed.
- Export to TODO / DONE text files, optionally kept up to date automatically.
- Dashboard with progress, open bugs, stale projects and recently finished work.
- Inbox for ideas, and quick capture from anywhere with a global hotkey.
- Copy items as a prompt for AI coding assistants.
- Open a project's folder, VS Code, a terminal or its GitHub page.
- Patch notes from everything finished since the last release.
- Search across all projects, and per-project notes.
- Light and dark theme, and automatic backups.
- Installer, with a check for new versions at startup.
