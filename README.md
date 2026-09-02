# DingoGameObjectsCMSEditorServer

Runtime-capable, local authoring server for a mounted DingoCMS library. The
server edits `Application.persistentDataPath/assets` through staged `JObject`
changesets. The external module folders are the only authored content source:
the server does not use `AssetDatabase`, build Unity `.asset` files, or import
content back into the project. It never reloads the active gameplay catalog
after a commit.

## Library and package contract

Each direct child of the mounted `assets` root is an independently versioned
module package. A package contains canonical asset JSON, physical resources,
and a derived `manifest.json`. Distribution or installation places the package
directly at `Application.persistentDataPath/assets/<module>`. In particular, a
clean game installation receives its required `base` package externally at
`Application.persistentDataPath/assets/base`; there is no Unity-project fallback
or editor export step.

New assets and modules are authored directly in this mounted library through
MCP. The current Web UI browses and edits existing assets; its future runtime
UI may expose the same generic create/clone operations. `manifest.json` is
always regenerated from the staged module state and cannot be edited as an
ordinary resource.

## Explicit start modes

The SnakeAndMice composition root starts the server only when explicitly
requested. Alongside gameplay, use:

```text
--dingo-cms-editor-server
```

To author a clean or incomplete library without constructing a gameplay
catalog, use both flags:

```text
--dingo-cms-editor-server --dingo-cms-editor-authoring-only
```

Authoring-only mode starts before gameplay initialization, works with an empty
`AppData/assets` directory, and does not create a gameplay content snapshot.
The authoring-only flag by itself does not enable the server.

Directories directly under the assets root whose names begin with `.` are
operational directories, not DingoCMS modules. They are omitted from runtime
module discovery, EditorServer listings, and module counts. This allows the
assets root itself to contain a Git `.git` directory without exposing it as an
invalid module.

## Unity Editor window

Open `Window/DingoCMS/Editor Server` to manage the authoring server without
entering Play Mode or constructing a gameplay catalog. The MCP listener always
runs in an authoring-only Windows Player outside the Unity process. Compilation,
domain reload, Play Mode and Unity restarts therefore do not close or rebind the
MCP port. Unity is only the launcher and status client, matching the process
boundary used by UnityMCP.

The launcher records a project-scoped PID, process creation time, executable
path, port, project id, source-build fingerprint and random per-launch instance
token in `Library/DingoCmsEditorServer`. A host is reported as running only
after both the Windows executable identity and an authenticated `/health`
response match that record. Stop kills only that exact verified process. An
uncertain identity remains visibly retained and blocks a second daemon.
`Keep detached server running` restarts a confirmed crashed host with backoff;
the backoff and stable-health window survive domain reload. It does not bounce a
healthy host during compilation or reload.

`Build host` writes a companion fingerprint manifest next to the Player. The
fingerprint covers Player-side DingoCMS source. If that source changes, the
window marks the still-owned host as requiring a rebuild instead of silently
accepting an old binary or entering a restart loop. Stop that host, build once,
and start the fresh Player. The old in-Unity `HttpListener` mode is no longer
startable.

The `Configure` buttons are also explicit. They update only this project's
`.codex/config.toml` and `.mcp.json`, preserve unrelated entries and store a
backup below `Library/DingoCmsEditorServer/ConfigBackups`. The reusable bearer
token is referenced through `DINGO_CMS_EDITOR_TOKEN`; it is never written into
either project config. The window does not modify the user's global Codex or
Claude Code configuration.

Optional arguments:

```text
--dingo-cms-editor-port=17844
--dingo-cms-editor-token=<at-least-24-characters>
```

`DINGO_CMS_EDITOR_TOKEN` is used when the token argument is absent. If neither
is supplied, the server creates an internal random bearer token; configure an
explicit token to connect MCP clients. Unity logs only a separate one-time Web
bootstrap URL, never the reusable MCP bearer. The public
`DingoCmsEditorServerRuntime.Start` API is the integration point for a future
runtime UI.

The listener is loopback-only:

```text
MCP: http://127.0.0.1:17844/mcp
Web: http://127.0.0.1:17844/
```

## Codex

The Unity window writes this block to the project-local `.codex/config.toml`
only after `Configure` is clicked. For manual setup, use:

```toml
[mcp_servers.dingo_cms]
url = "http://127.0.0.1:17844/mcp"
bearer_token_env_var = "DINGO_CMS_EDITOR_TOKEN"
```

## Claude Code

The Unity window writes the following project-local `.mcp.json` entry only
after `Configure` is clicked. For manual setup, use:

```json
{
  "mcpServers": {
    "dingo-cms": {
      "type": "http",
      "url": "http://127.0.0.1:17844/mcp",
      "headers": {
        "Authorization": "Bearer ${DINGO_CMS_EDITOR_TOKEN}"
      }
    }
  }
}
```

Do not commit the token.

## Authoring workflow

To rewrite one document, `asset_save` is the whole flow: it stages, validates
and publishes in a single call, and discards its staging copy if any step
fails. Pass the `documentSha256` returned by `asset_get` and a save over a
document that changed underneath is rejected with `content_conflict` instead of
overwriting it. Saving a document identical to the one on disk is reported as
`changed: false` and does not publish a new module revision.

To upload one file of any type - a sprite, an audio clip, a lookup table - use
`resource_put`. It publishes through the same single-call transaction. Send the
bytes inline as `base64`, or pass `sourcePath` and the server reads that file
from the editor host itself and copies it into the module; the same `sourcePath`
is available on the `putResource` changeset operation. With `sourcePath` the
target `relativePath` may be omitted to keep the source file name at the module
root, or end with `/` to keep that name inside the given folder. `expectedSha256`
guards an overwrite the way `documentSha256` guards a document, with an empty
string requiring the file to be new, and re-uploading identical bytes is
reported as `changed: false`. `resource_list` shows the module's files with
their size and `sha256`; asset documents and the derived manifest are not listed
there.

For anything wider - creating, cloning, deleting, several files or assets at
once - drive the changeset directly:

1. Read `cms_status`, `schema_list` and `asset_search`.
2. Start a changeset with the module's current `contentHash`.
3. Apply generic asset/resource operations.
4. Inspect `changeset_preview`.
5. Run `changeset_validate`.
6. Commit explicitly, or abort.

## Web editor

The Web UI is the single-document path and exposes no changeset controls.
Select an asset on the left, edit it on the right, then press `Ctrl+S` or
`Save`. `Reload` discards local edits and re-reads the document from disk, and
leaving a document with unsaved edits asks first.

The document is browsed as folders. Objects and arrays are folders, scalars are
files, and the address bar is the asset's JSON Pointer, so `Components › 1`
really is `/Components/1`. An array element is captioned with its authored
`$type` rather than its index, because that is the name an author looks for.

| Gesture | Effect |
| --- | --- |
| Double-click, `Enter` | Open a folder, or focus a file's value |
| `Backspace`, `Up` | Leave the folder |
| Arrow keys | Move the selection across the grid |
| `New` | Insert an entry of the chosen type; object properties open for renaming |
| `F2`, `Rename` | Rename a property in place, keeping its position in the file |
| `Duplicate` | Copy an entry directly after the original |
| `Delete` | Remove the selected entry |
| `Ctrl+Z` / `Ctrl+Y` | Undo and redo, in both views, without moving the user |
| `JSON` | Switch to the raw text of the same document, and back |

Deletes are not confirmed - undo covers them. Switching to folders from the raw
text requires the text to parse; an invalid document stays in text view.

Changesets are staged outside the mounted `assets` root. Commit rechecks the
live hash before a recoverable directory swap, so two clients cannot silently
overwrite each other. The server derives `manifest.json`; clients do not edit
it directly. One process owns an OS-level lease for the library; connect Codex,
Claude Code and the Web UI to that same daemon instead of starting competing
servers.

Authored fields remain canonical JSON documents. The server discovers allowed
root/component `$type` aliases, but deliberately does not create a second
reflection-driven field model.

Component arrays use wider cards in the folder view. A one-level child
component set is shown as a separate `Child N` card with its stable ordinal and
component chips. Collider-shape cards summarize only the fields relevant to
their selected shape. The client also surfaces local composition errors such as
a missing shape, combined solid and trigger roles, invalid dimensions, an empty
child set, duplicate child components, or recursive child composition. Child
cards report these errors without requiring the child to be opened first.
These hints do not replace server schema/materialization validation and do not
rewrite the canonical JSON.
