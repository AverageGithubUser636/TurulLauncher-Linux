TurulLauncher for Linux
=======================

A) AppImage (ajánlott — egyetlen fájl, nem kell telepíteni):
  1. Futtathatóvá:  chmod +x TurulLauncher-Linux-*.AppImage
  2. Indítsd:       ./TurulLauncher-Linux-*.AppImage
  3. Menübe:        ./install-appimage.sh TurulLauncher-Linux-*.AppImage
     (Super-gomb → TurulLauncher; minden disztrón: GNOME, KDE, XFCE…)

B) Tarball (klasszikus telepítés):
  1. Csomagold ki:  tar xzf TurulLauncher-Linux-*.tar.gz
  2. Telepítsd:     cd TurulLauncher-Linux-*/ && sudo ./install.sh
     (sudo nélkül:  ./install.sh --user)
  3. Indítsd:       TurulLauncher   (vagy a menüből)

Követelmények:
  - 64 bites Linux (x86_64 vagy ARM64, a csomag neve mutatja)
  - .NET NEM kell (self-contained csomag)
  - Java NEM kell előre (a launcher letölti, vagy használd a gépedét)
  - Minecraft futtatáshoz: OpenGL-képes GPU + driver

Adatok:
  ~/.local/share/TurulMC   (instance-ok, modok, beállítások, logok)

Frissítés:
  A launcher a Beállítások > Frissítés alatt magától frissít
  (hordozható módban). Csomagos telepítésnél töltsd le az új
  kiadást innen, és futtasd újra az install.sh-t.

Hiba esetén:
  - Doctor lap a launcherben (Futtatás gomb)
  - Logok: ~/.local/share/TurulMC/logs/
  - Support csomag: a Doctor lapon készül (terv szerint)

Verzió: lásd a csomag nevét. Forrás és hibajegyek a GitHub-repóban.
