# Releases

Wubuntu ships as a portable ZIP for 64-bit Windows. `VERSION` is the single
source for the EXE version, generated manifest, archive name, and release tag.
Use three numeric components without leading zeros, each at most 65534.

## Prepare a release

1. Update `VERSION`: patch for fixes (`1.0.1`), minor for features (`1.1.0`),
   major for incompatible changes (`2.0.0`). The first stable release is `1.0.0`.
2. Commit the changes and merge them into `main`. Wait for CI to pass.
3. From the intended release commit on an up-to-date `main`, create and push
   an annotated tag matching `VERSION`:

   ```powershell
   git tag -a v1.0.0 -m "Release v1.0.0"
   git push origin v1.0.0
   ```

4. The Release workflow validates the tag, builds the tagged commit, runs tests,
   and attaches `Wubuntu-1.0.0-win-x64.zip` to a draft GitHub Release.
5. Download that ZIP from the draft and extract it into a writable folder.
   On Windows with Ubuntu and SSH configured, verify startup, SSH readiness,
   restart, and exit. Save work first: restart and exit stop Ubuntu processes.
6. Edit the generated release notes into a short description of user-visible
   changes. For the first release, include the Windows/.NET/Ubuntu/SSH requirements
   from the README. Publish only after the archive passes this manual check.

The default GitHub source archives contain source code; users need our attached ZIP.
The README download button already points to the releases page.

## Local packaging

Run `./source/package.ps1` from PowerShell. It builds in a fresh temporary folder
and writes the ZIP to `dist/`. Only the EXE, configuration, license, and quick-start
instructions are included. Local packaging does not publish or run tests.

## Failed runs and updates

Fix build or test failures before publishing. A failed run before draft creation
can be rerun against the same unchanged tag. If a draft already exists, inspect it
first: the workflow deliberately fails instead of overwriting an existing release.
An incomplete unpublished draft can be deleted and the workflow rerun.
If the code needs changing, use a new version and tag.

Never move published tags or replace published archives. Ship fixes as a new patch
release. Updating the app is manual: save work, exit Wubuntu, replace the extracted
application files, and launch it again. There is no installer or automatic updater.
