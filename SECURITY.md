# Security

**English** · [Español](SECURITY.es.md)

## Reporting a problem

Use [a private security advisory](https://github.com/ElKiro1311/Atrio/security/advisories/new) instead of a public issue.

## Risks worth knowing

**Add-ons run with your permissions.** An Atrio or Playnite add-on is code that runs as you. Only install those whose author you trust. Atrio warns you if you have any of the ones in `atrio/addons/blocked.txt`.

**Store sessions are kept in Atrio's folder.** They are in `ExtensionsData`. If you share or upload your folder, remove it first.

**The program isn't signed.** Download it only from this repository's releases page. Updates come from that same page, and Atrio checks each package's hash before installing it.

**OptiScaler Client is somebody else's program.** It's only downloaded if you ask for it on a game's page, from its GitHub page, and Atrio checks the SHA-256 hash that GitHub gives before opening it. A package you downloaded yourself is checked too, and isn't used unless it's a published one; a copy you unpack by hand into Atrio's folder is used as it is. It runs with your permissions and changes files of the games you install it in. Atrio drives it with a module of its own through a pipe that only your user can open. Before changing a game it saves what it's about to touch in OptiScaler Client's data folder (`Atrio` subfolder) and deletes it when it's done: if something fails or is cut short, the game goes back to how it was. Don't use it in online games.

**Inherited libraries.** Atrio keeps two old Playnite libraries because add-ons and the library format depend on them:

| Library | Advisory | Why it stays | Scope |
|---|---|---|---|
| LiteDB 4.1.4 | Deserialization of untrusted data (critical, fixed in 5.0.13) | Version 5 uses another file format: it would break Playnite libraries and add-ons | It only opens the local library. Whoever can alter it already has access to your account |
| AngleSharp 0.9.9 | Wrong encoding when generating HTML (moderate) | Newer versions change the API that add-ons use | It's used to read pages, not to publish HTML |

Newtonsoft.Json is up to date (13.0.3), with a redirect for the add-ons built against version 10.
