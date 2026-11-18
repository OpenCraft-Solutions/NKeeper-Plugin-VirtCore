# NKeeper virt-core plugin

The official `virt-core` plugin for [NKeeper](https://github.com/OpenCraft-Solutions/NKeeper) 2.0: what the hypervisor plugins (KVM, later Proxmox) share. It lives in a repository of its own and is released on its own, so releasing it isn't a release of NKeeper (NKeeper's `docs/architecture.md` §9). Its design is §7.36 there.

## What it does

virt-core contributes nothing on its own. Each hypervisor plugin (`kvm`, say) depends on it, contributes its own session type, and brings its own way of reaching the hypervisor; virt-core runs the session and draws the tab. It needs SDK 1.8 (`>=1.8.0 <2.0.0`).

**For a hypervisor's backend**, its one assembly, `NKeeper.Plugins.Virtualization`, is exported, so every hypervisor plugin uses virt-core's copy. It is also published as a NuGet package of the same name, which they build against:
- `IGuestProvider`: connects to the hypervisor through core and gives an `IGuestConnection`: its guests, their power actions, and a guest's console as a stream;
- `GuestSessionHandler<TProvider>`: the hypervisor's session type's handler, registered with `services.AddGuestProvider<TProvider>()`.

**For a hypervisor's frontend**, it exports `GuestsView`, the guests tab, ported from 1.x's VM list. The hypervisor wraps it in its session view (`context.use('virt-core')`, SDK 1.8):
- the guests, with their state and power actions, asking first before those that cut a guest off; power is audited as `<plugin>.power`;
- a guest's VNC console in the same tab, with noVNC, its bytes carried by the session's own binary frames.

**Not yet:** SPICE consoles, the VM capacity report, guests' tasks, and linking a guest to its host in NKeeper's inventory.

The plugin's ID is `virt-core`, and it is built in: it is signed with NKeeper's official key and installed with NKeeper.

## Layout

```text
backend/        the shared assembly: the contract (Guests.cs), the session (GuestSession.cs), the plugin, nkeeper-plugin.json (the manifest), the project
frontend/       the guests tab and its VNC console (noVNC): a Vite library build to dist/index.js
tests/          the session's tests, on an in-memory hypervisor
.github/        CI and the release workflow
```

It builds against NKeeper's published SDK, never core's source:

| Package | Registry | Used for |
| --- | --- | --- |
| `NKeeper.Abstractions` | GitHub Packages (NuGet) | the backend SDK |
| `NKeeper.Plugin.Build` | GitHub Packages (NuGet) | builds, packs and signs the plugin |
| `@opencraft-solutions/nkeeper-sdk` | GitHub Packages (npm) | the frontend SDK, imported as `@nkeeper/sdk` |
| `@novnc/novnc` | npm | the VNC console, which the frontend bundles (MPL-2.0) |

## Building

You need the .NET 10 SDK (see `global.json`) and Node.js 24 (see `.nvmrc`).

GitHub Packages needs a token even for public packages. Make a GitHub personal access token that can read packages (`read:packages`) and give it to the build in the environment. It is never written to the repository:

```bash
export NUGET_USERNAME=<your GitHub user name>
export NUGET_AUTH_TOKEN=<token>
export NODE_AUTH_TOKEN=<token>
```

Then, from the repository root:

```bash
(cd frontend && npm ci && npm run lint && npm run typecheck)
dotnet restore NKeeper.Plugins.Virtualization.slnx
dotnet build NKeeper.Plugins.Virtualization.slnx --configuration Release
```

Building the plugin builds its frontend, packs `virt-core-<version>.nkp` into `artifacts/plugins/` and signs it with a development key, which the build creates under `.dev/keys/` on first use. Both are ignored by git. To load the package in a development NKeeper, point `Plugins:Directory` at `artifacts/plugins` and `Plugins:DevelopmentTrustDirectory` at `.dev/keys`, with a hypervisor plugin beside it.

Pinned versions: NuGet packages are locked in `packages.lock.json` (CI restores with `--locked-mode`), and npm packages in `frontend/package-lock.json`. Run `dotnet restore --force-evaluate` after changing a version.

## Testing

```bash
dotnet test NKeeper.Plugins.Virtualization.slnx --configuration Release
```

The tests run the session in process, on an in-memory hypervisor whose console is a pipe each way. They cover the guests list, power and its audit (and refusals), a console's bytes both ways and its end, and a guest without a console. Each hypervisor plugin tests its own commands.

## Continuous integration

`.github/workflows/ci.yml` runs on every push to `main` and every pull request: it restores from GitHub Packages with the workflow's token, lints and type-checks the frontend, checks formatting, builds, runs the tests and packs the plugin signed with a development key, which it keeps as a build artifact.

## Releasing

1. Raise `version` in `backend/nkeeper-plugin.json` (and keep `frontend/package.json` in step), and merge it to `main`.
2. Tag the merge commit `v<version>`, for example `v1.1.0`, and push the tag.
3. `.github/workflows/release.yml` runs:
   - it checks that the tag matches the manifest's version and is on `main`, then builds and tests;
   - in the protected `release` environment, which needs an owner's approval, it packs `virt-core-<version>.nkp` signed with the official key, and publishes a GitHub release with the package and its SHA-256;
   - beside it, once the build and tests pass, it publishes `NKeeper.Plugins.Virtualization` `<version>` to GitHub Packages, for the hypervisor plugins. That needs no key, so it doesn't wait for the approval.

The official key's private half, `NKEEPER_RELEASE_KEY` (a PEM), is a secret of the `release` environment. It is available only to the signing job, goes to memory-backed storage for the packing step alone, and is removed when that step ends. It is never in the repository or in a log.

NKeeper's installer picks the release up when its `deploy/plugins.lock` is updated with the new URL and SHA-256.

## License

MIT, as NKeeper. See [LICENSE](LICENSE).
