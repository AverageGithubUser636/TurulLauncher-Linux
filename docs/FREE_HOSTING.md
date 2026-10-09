# TurulLauncher hosting – EGY weboldal

- **Cloudflare Pages:** csak a meglévő `turulnetwork.hu` projekt.
- **Launcher oldal:** `https://turulnetwork.hu/launcher/`
- **Update manifest:** `https://turulnetwork.hu/launcher/update/stable.json`
- **GitHub Releases:** a nagy ZIP és a Setup EXE.
- **launcher.turulnetwork.hu:** csak Cloudflare Redirect Rule a `/launcher/` útvonalra; nincs külön Pages projekt.

Build: `publish-all.cmd`
Hosting patch: `prepare-free-hosting.cmd`
GitHub release: `upload-github-release.cmd`
