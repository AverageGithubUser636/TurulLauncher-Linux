# TurulLauncher 4.5.0

## Added
- Automatic Java runtime install: when the selected Minecraft version needs a Java version that
  is not on the machine, the launcher downloads the matching Eclipse Temurin JRE from the
  Adoptium API, verifies the package with SHA-256 and installs it under `%APPDATA%\TurulMC\java`.
- Java version map per Minecraft version (1.16 and older → 8, 1.17 → 16, 1.18–1.20.4 → 17,
  1.20.5+ → 21, 26.x → 25), with the version JSON's `javaVersion.majorVersion` taking priority.
- Java runtime manager in Settings: detected and launcher-installed runtimes with source badges,
  one-click install per required major, and removal of installed runtimes.
- `AutoInstallJava` setting (on by default) plus a progress indicator while Java is downloading.
- Doctor diagnostics panel in the GUI (Game page → Doctor): 17 checks covering the system,
  settings/instance JSON health, configured/detected/required Java, a real timed `java -version`
  probe, instance folder, write access, free disk space, VC++ and WebView2 dependencies, Mojang /
  Modrinth / update-server reachability and log health, each with a one-click fix where possible.
- Support bundle now includes a `doctor.txt` report and is built by the shared Core service, so the
  GUI and the CLI produce exactly the same package.
- CLI: `doctor` (full report, exit code from health), `support` (bundle), `java list`,
  `java install [major]`, `java remove <id>`.
- New smoke-test project `tests\TurulMC.Features.SmokeTests` (33 checks) covering the version map,
  Adoptium response parsing, host allow-list, provisioner path safety, the Java runtime service and
  the Doctor/support-bundle behaviour — all offline and wired to the production entry points.

## Changed
- Java detection is now asynchronous and cached (24 h): a Java that never answers `-version` can no
  longer block the launcher, and repeated scans no longer start a process per candidate path.
- `JavaRuntimeService.SetJavaPathAsync` writes `settings.json` atomically.
- The Doctor, the launch path and the CLI all share one compatibility rule
  (`JavaVersionMap.IsCompatible`) instead of three copies.
- Test package versions are pinned (`Microsoft.NET.Test.Sdk` 17.14.1, `xunit` 2.9.3,
  `xunit.runner.visualstudio` 2.8.2) so restores are reproducible.
- `TurulMC.sln` now contains the feature smoke-test project.

## Notes
- Installing Java requires `autoInstallJava` to be enabled (Settings → Java); when it is disabled
  the launcher reports which Java version is missing instead of downloading anything.
- Adoptium downloads are restricted to `github.com`, `*.githubusercontent.com` and
  `api.adoptium.net` over HTTPS; the package hash is verified before extraction and every archive
  entry is validated against the install root (zip-slip guard).
