# TurulLauncher 4.6.0

Ezek a változások a 4.5.0-s (Java + Doctor) munka után készültek, ugyanabban a fejlesztési körben.
A verziószám még nincs átállítva — lásd a „Kiadás előtt" fejezetet.

## Added — mod-verzió ellenőrzés és javítás

- **Smart Repair mostantól a modokat is ellenőrzi** (nem csak a Minecraft fájlokat és a loadert).
  Offline, a mod JAR-ok `fabric.mod.json` leírása alapján:
  - nem támogatja az Instance Minecraft-verzióját (`depends.minecraft`, pl. `>=1.21 <1.22` egy 26.2-es Instance-ban),
  - újabb Fabric Loadert igényel, mint a telepített (`depends.fabricloader`),
  - nem Fabric mod (Forge/NeoForge/quilt) Fabric Instance-ban,
  - duplikált mod azonosító (két verzió ugyanabból a modból — klasszikus összeomlás-ok),
  - olvashatatlan/sérült JAR vagy hiányzó leíró.
- **Egy kattintásos javítás** a riportból: az inkompatibilis modok letiltása (`.jar.disabled`,
  a Modok oldalon visszakapcsolható) vagy karanténba helyezése
  (`mods-quarantine\<dátum>\` + `quarantine.json` leltár, visszaállítható).
  Javítás előtt a launcher automatikus modmentést készít.
- **Modfrissítés a Modrinthről** (Modok oldal → *Frissítés*, illetve a Smart Repair riportból):
  - csak a célverzióhoz és loaderhez illő kiadásokat ajánlja (`game_versions` + `loaders`),
  - **nincs visszalépés**: a telepített verziónál régebbi kiadást soha nem ajánl fel,
  - stabil kiadás előnyt élvez a bétával szemben, de ha a telepítettnél csak béta az újabb, azt jelzi,
  - a verziót **mindig a launcher oldalán oldja fel újra** (a felület csak azonosítókat küld),
  - a letöltött JAR SHA-ellenőrzése kötelező, a lecserélt régi fájl a `mods-backup\<dátum>` mappába kerül,
    a művelet előtt automatikus modmentés készül.
- **Instance-másolás verzióütközés-felismeréssel**: a másolás modáljelzi, ha a forrás és a cél
  Instance Minecraft-verziója eltér (pl. 1.21.11 → 26.2), és megmutatja, hány mod nem indul el a
  célverzióval. Választható stratégia:
  1. **Átmásol, majd a modokat frissíti** a célverzióra (Modrinth),
  2. az inkompatibilis modok **kihagyása**,
  3. minden másolása változatlanul (figyelmeztetéssel, mert a játék valószínűleg nem indul el).
- **Modok oldal**: „Kompatibilitás" (offline vizsgálat, modonkénti állapot és indoklás) és
  „Frissítés" (Modrinth terv + kijelölhető frissítés) gomb.
- **Instance szerkesztő bővítése**: Fabric Loader verzió (üres = automatikus), Instance-szintű
  Java útvonal felülírás, extra JVM argumentumok, játékablak méret, megjegyzés, valamint gyors
  műveletek (Instance mappa, Modok mappa, Kompatibilitás ellenőrzése, Smart Repair).
- **Valódi Minecraft-verziólista** a létrehozó és szerkesztő modálban a Mojang manifestből
  (kiadások + pillanatképek), így a 26.2 is azonnal választható — korábban négy fix verzió volt,
  és egy 26.2-es Instance szerkesztésnél a lista csendben visszaállította volna a verziót.
- Window méret (`--width/--height`) támogatás a játék indításában, és a launcher/Instance szintű
  extra JVM argumentumok mostantól **akkor is érvényesülnek, ha a Mojang metaadat tartalmaz JVM
  argumentumokat** (korábban ilyenkor csendben eldobódtak).
- Új CLI-kompatibilitás: a Doctor/Java parancsok mellett a mod-ellenőrzés a GUI-ból érhető el.

## Changed

- `LauncherInstance` új mezői: `javaPath`, `jvmArgs`, `windowWidth`, `windowHeight`, `notes`
  (a `instances.json` visszafelé kompatibilis, a hiányzó mezők alapértéket kapnak).
- `instances.update` validálja az új mezőket: Java útvonal csak létező `java.exe`/`javaw.exe` lehet,
  a JVM argumentumok tiltott fragmentjeit a `LauncherSettingsValidator` szűri, az ablakméret
  párosan adható meg.
- Smart Repair takarítás: 1 óránál régebbi `*.part` félkész letöltések és a már nem létező modokhoz
  tartozó `mods/.turul-meta` bejegyzések törlése.
- `instance.repair` és `mods.update` művelet előtt automatikus modmentés (recovery pont) készül.
- A mod-vizsgálat minden mod JAR-t méretkorláttal, kivételmentesen olvas (sérült ZIP nem állítja meg
  a teljes ellenőrzést).

## Tests

- `tests\TurulMC.Features.SmokeTests` bővítve (33 → **41 ellenőrzés**): verzió-összehasonlítás,
  verziótartomány-egyeztetés (a konkrét 1.21.11 → 26.2 esettel és a határesetekkel), fabric.mod.json
  olvasás, Forge/sérült JAR kezelés, mod-szkenner (duplikáció, loader-igény, vanilla figyelmeztetés),
  Modrinth verzióválasztás (release/béta rangsor, visszalépés-védelem dátum és verziószám alapján,
  család-egyeztetés, „már naprakész” felismerés, hibás JSON), cél-Instance ütközésvizsgálat
  (`ModTargetReconciler`: újabb verzió, duplikátum, hiányzó mappa), valamint az indítási
  argumentumok (ablakméret, extra JVM argumentumok).
- Meglévő készletek változatlanul zöldek: xunit **62/62**, recovery smoke **20/20**.

## Független ellenőrzés és az abból javított hibák

Egy külön, független verifikációs kör (135 saját ellenőrzés + a fenti készletek) az alábbi valós
hibákat találta meg, amelyek ezután javításra kerültek:

| Hiba | Javítás |
|---|---|
| **H1** — a frissítés-terv „verziócsalád” egyezést kínált fel telepítésre, de a telepítő (helyesen) pontos egyezést kért, így az mindig elhasalt | a család-egyezés mostantól **tájékoztató** (`Csak verziócsalád-egyezés`), nem jelölhető ki; a terv és a telepítő ugyanazt a feltételt használja (`ModUpdatePlanner.IsInstallable`) — a tesztek ezt rögzítik |
| **M1** — a „nincs visszalépés” védelem csak akkor működött, ha a telepített verzió dátuma ismert és benne volt a listában; verziószámot sosem hasonlított | a tervező megkapja a telepített verziószámot (a JAR leírójából), dátum híján verziószám alapján dönt; a korábban elérhetetlen „ez a legfrissebb kompatibilis” ág valós `uptodate` állapot lett, és nem állítja tévesen, hogy nincs kompatibilis kiadás |
| **M2** — a Smart Repair **ellenőrzés közben is törölt** (félkész `.part`, elavult metaadat), miközben ilyenkor nincs automatikus modmentés | törlés csak kifejezett javításkor (`fix=disable\|quarantine`) történik — ellenőrzéskor csak jelzi, mi vár takarításra; a javítás előtt automatikus modmentés készül |
| **M3** — másolás/frissítés figyelmen kívül hagyta a **cél** Instance mod-készletét: régebbi kiadás kerülhetett egy újabb helyére, vagy két azonos mod maradt a mappában | új `ModTargetReconciler`: ha a célban újabb verzió van, a frissítés kimarad (a letöltött fájl törlődik); az azonos mod-azonosítójú duplikátumok biztonsági mentésbe kerülnek |
| **M4** — két azonos pillanatkép (pl. `25w14a`) „verzióütközést” jelzett | `IsSame` először szöveg szerint hasonlít |
| **M5** — `"1.20.1 - 1.21.4"` és a csupasz `"||"` hamis „nem kompatibilis” volt; a `">=1.21<1.22"` és a nem törhető szóközös alak csendben elvesztette a felső korlátot | hyphen range támogatás, értelmezhetetlen tartomány → `Unknown`, elválasztó-normalizálás (szóköz beszúrása operátor előtt) |
| **M6** — a letiltás felülírhatta a felhasználó által félretett `.jar.disabled` példányt | a meglévő példány előbb `mods-backup\<dátum>` mappába kerül; ha ez nem sikerül, a fájl kimarad |
| **M7** — a kihagyott mod metaadata átmásolódott a cél Instance-ba (ott „szellem mod” lett) | a másolás a `mods\.turul-meta\<fájl>.json` bejegyzést is kihagyja |
| **C1** — a modfrissítés átvette a felülettől a célverziót | a szerver oldal az Instance saját verzióját használja, és elutasítja az eltérő kérést |
| **C3/C4/C5/C6/C8** — Quilt mod ismeretlen loaderrel „kompatibilisnek” látszott; a 2000 modos cap nem determinisztikus és néma volt; a Modok oldali gomb a feliratából tippelte a műveletet; a javítás az ellenőrizhetetlen (idegen/sérült) JAR-okat is letiltotta; holt kód | Quilt + nem támogatott loader → „nem ellenőrizhető”; a cap betűrend szerinti és jelzett; a művelet `data-action` attribútumból jön; az ellenőrizhetetlen fájlokat a javítás nem tiltja le, csak jelzi; holt kód törölve |

A verifikáció megerősítette azt is, hogy a JAR-olvasás méretkorlátai valódiak (hamisított
central directory-val sem lehet átverni), hogy a `fabric.mod.json` `depends` tömbjeinek `||`
összefűzése helyes (a Fabric VAGY-kapcsolatot használ), és hogy a `^0.0.3` → `0.0.9` egyezés a
Fabric szabályai szerint is helyes.

### Második verifikációs kör (a javítások ellenőrzése)

A javításokat egy második, szintén független kör (142 saját ellenőrzés) vizsgálta. Az általa
talált négy valós hiba szintén javítva:

| Hiba | Javítás |
|---|---|
| **D1** — a Modrinth valós `version_number` formátumai (`mc26.3-0.9.3-alpha.1-neoforge`, `0.9.2-fabric`) nem értelmezhetők szemverzióként, ezért a visszalépés-védelem **néma** volt a valós adatokon | új `ModVersionOrder`: kiemeli a numerikus magot (platform-előtag levágása) és a kiadás-előtti utótagot is figyelembe veszi; ezt használja a tervező és a cél-Instance ellenőrzés is |
| **D2** — a terv dátuma-alapú, a telepítő verziószám-alapú szabályt használt → a varázsló kipipálható frissítést kínált, amit a telepítő elutasított; ráadásul a szám-alapú szabály blokkolta a jogos „másik MC-verzióhoz készült példány cseréjét" | egységes, **verziószám-elsődleges** szabály (dátum csak akkor dönt, ha a verziók nem összehasonlíthatók), és a cél-Instance ellenőrzés csak akkor tilt, ha a célban lévő újabb példány a **célverzióval is működik** |
| **D3** — a `fix:"none"` átjutott a javítás-kapun, így takarítás futott automatikus modmentés nélkül | a „javítás" csak `disable`/`quarantine` lehet; a két kapu (repair + recovery) ugyanazt a feltételt használja |
| **D4** — ha a letöltendő fájlnév megegyezett a célban lévő (újabb) fájlnévvel, a döntés a letöltés **után** született, és a kizárás miatt nem látta a meglévő verziót | a döntés a **letöltés előtt** történik (a fájlnévhez tartozó mod-azonosító a cél-indexből ismert); a letöltés utáni ellenőrzés pedig már nem zárja ki a friss fájlt |
| D5/D6 | a kiadás-előtti utótagok összehasonlítása (`1.0.0-beta.1 < 1.0.0-beta.2 < 1.0.0`), így a `IsSame("1.21.4-pre1","1.21.4")` már hamis |
| D7 | további tartomány-alakok: `">= 1.20.1"` (operátor utáni szóköz), `"1.20.1-1.21.4"` (szóköz nélküli hyphen range), és a hibás `">=1.0 - 2.0"` alak most `Unknown` (nem hamis „nem kompatibilis") |
| D8/D9 | üres loader → `none` a szkennerben; egy zárolt JAR nem szakítja meg a teljes javítást |
| további | a letöltési URL nélküli fájlokat a telepítő is kihagyja (a tervezővel egyezően) |

**Nyitva maradt, tudatos döntésként:** az „ismeretlen adat esetén elfogadjuk a jelöltet" elv
(helyette nem tiltunk le semmit), a Quilt modok jelzése feloldhatatlan loader esetén, valamint a
4 GiB / 50 000 fájl modmentési korlát (nagy modpacknál a mod-művelet inkább el sem indul, mint hogy
mentés nélkül módosítson).


## Javított indítási hiba: `instances.json.tmp` — Access denied (2026-10-05)

**Tünet:** a launcher indításkor ezzel a hibával elhalt:

```
UnauthorizedAccessException: Access to the path
'…\AppData\Roaming\TurulMC\instances.json.tmp' is denied. (0x80070005)
```

**Ok:** a `SaveInstances()` egy **fix nevű** `instances.json.tmp` fájlt írt felül
(`File.WriteAllText`), majd `File.Move`-dal cserélt. Ha az a fájl írásvédett, egy másik folyamat
épp törli/átnevezi („delete pending” → ERROR_ACCESS_DENIED), vírusirtó/indexelő fogja, vagy
véletlenül mappa áll a helyén, a művelet `UnauthorizedAccessException`-t dob — és mivel ez a
`MainWindow` konstruktorában fut, a launcher el sem indult. Ugyanez a minta („fix .tmp”) megvolt a
`profiles.json`, a Java-nyilvántartás és a szerverlista írásánál is.

**Javítás:**

- Új, közös `TurulMC.Core\Storage\AtomicFile` író: **egyedi temp fájlnév**, útvonalankénti zár,
  elárvult fix nevű `.tmp` (és a helyén álló mappa) eltakarítása, **írásvédett attribútum
  leválasztása**, `.bak` biztonsági másolat, hosszú útvonal (`\\?\`) támogatás, újrapróbálkozás
  rövid várakozással, és `TryWriteAllText`, amely **soha nem dob** — hiba esetén eredményt ad.
- Bekötve: `instances.json`, `profiles.json`, `settings.json` (Java felülírás), Java
  `java-cache.json` és `runtimes.json`, `servers.json`, játékidő/session, utolsó crash-riport.
- A mentés hibája **nem állítja meg a launchert**: napló + egyszeri hiba-toast a felületen, a
  memóriabeli állapot megmarad.
- **Induláskori öngyógyítás**: a launcher a `%APPDATA%\TurulMC` mappában maradt, 2 percnél régebbi
  `*.tmp` fájlokat (és temp mappákat) eltakarítja, mielőtt bármit írna.
- **Single-instance hiba javítva**: ha az előző launcher összeomlott, a mutex „gazdátlan" maradt, és
  a `new Mutex(...)` `AbandonedMutexException`-t dobott — ez mostantól nem indítási hiba, hanem
  folytatódó indulás (más felhasználó példányánál pedig csendes kilépés).

**Teszt:** 6 új füstteszt (`AtomicFile`): írásvédett célfájl, írásvédett elárvult `.tmp`, mappa a
temp helyén, friss temp érintetlenül hagyása, hibaág kivétel nélkül, 24 párhuzamos mentés,
induláskori takarítás. Élesben ellenőrizve: a launcher a javított build-del **elindult**
(splash → főablak, kivétel nélkül).

## UI: szebb updater, splash és Minecraft-betöltő képernyő

- **Splash képernyő**: fázis-jelzők (Beállítások → Instance-ok → Felület → Kész) kipipálással és
  témaszínű kiemeléssel, lassan forgó gyűrű a logó körül, pulzáló fény a haladássáv mögött,
  nagyobb, rendezettebb elrendezés (620×430), „v” jelölés a verziónál.
- **Minecraft-betöltő ablak**: fázislista (Java → fájlok/Fabric → indítás → ablak megjelenése)
  állapotjelzőkkel, forgó gyűrű, sorszámot mutató, automatikusan görgető naplópanel,
  **tipp-rotátor** (8 hasznos tipp, 7 másodpercenként), letisztult fejléc, eltelt idő óra:perc
  formában, és siker/hiba esetén odaillő záró szöveg.
- **Updater**: a launcher **témaszínét követi** (a `settings.json`-ból olvassa: yellow/green/red/
  purple/sky), fázisjelző csík (Letöltés → Ellenőrzés → Telepítés → Kész) a folyamat állapotából,
  animált fénycsík a haladássávon, glow a logó körül, fejlécben „→ verzió” jelvény, naplópanel
  fejléccel; az ablak mérete igazodik az új tartalomhoz.
- Ellenőrzés: mind a launcher, mind az updater elindul és válaszol (az updater preview módban).
  A képernyőmentés ebben a környezetben nem elérhető, ezért a **vizuális végső ellenőrzés a
  felhasználóé** — a build és az indítás viszont igazolt.

## Javított: ablak-villogás bezárás után (tálcára rejtés ciklus)

**Tünet:** a launcher X-szel való bezárása (tálcára rejtés) után az ablak másodpercenként
többször ugrált kicsinyített és normál/teljes méret között.

**Ok:** a `HideToTray()` minden hívás elején visszaállította (`presenter.Restore()`) az ablakot,
majd megpróbálta létrehozni a tálca ikont. Ha a tálca ikon nem hozható létre (explorer újraindulás,
értesítési terület hibája, másik példány), a hib ág **minimalizálta** az ablakot — ami újabb
`AppWindow.Changed` eseményt keltett, az pedig megint `HideToTray`-t hívott: végtelen
„visszaállít → minimalizál” ciklus. (Mérve a javítás előtt: **38 állapotváltás 4 másodperc alatt**;
a javítás után: **1 állapot, stabil**.)

**Javítás (`MainWindow.Tray.cs`):**

- a rejtés már **nem állítja vissza** az ablakot (a visszaállítás a `RestoreFromTray` dolga),
- **újrabelépés-védelem** (`_trayTransition`): egy tranzakció alatt nem indul újabb,
- **időalapú csillapítás** (`_suppressAutoHideUntilUtc`): az egymás után érkező minimize
  események nem indítanak új ciklust (a régi egyszeri `_ignoreNextMinimizeToTray` helyett),
- **két sikertelen tálca-létrehozás után a launcher feladja** az automatikus tálcára rejtést
  erre a munkamenetre, és egyszerűen minimalizál (nincs több próbálkozás → nincs ciklus),
- minden rejtés/visszaállítás naplózva (`HideToTray`/`restored from native system tray`).

## Javított: updater „Access to the path '%TEMP%\<random>' is denied”

**Ok:** az updater egyrészt a `%TEMP%\TurulMC-Updater\...` mappát használta a kicsomagoláshoz,
másrészt a self-contained single-file .NET hoszt alapból a `%TEMP%`-be csomagol ki natív
fájlokat (a `Path.GetRandomFileName()` mintájú temp fájl pontosan innen jön). Ha a Temp mappa
írásvédett, tele van, vagy egy vírusirtó blokkolja, az updater **el sem indult**.

**Javítás:**

- az updater a saját, garantáltan írható munkakönyvtárát használja
  (`%LOCALAPPDATA%\TurulMC\updates\work\<guid>`), és ha az nem hozható létre, a
  `%APPDATA%\TurulMC\updates\work` a tartalék — `%TEMP%` már nem kell,
- `TurulMC.Updater.csproj`: `IncludeNativeLibrariesForSelfExtract=false`,
  `EnableCompressionInSingleFile=false` → nincs temp kicsomagolás induláskor,
- a launcher az updater indításakor beállítja a `DOTNET_BUNDLE_EXTRACT_BASE_DIR`, `TEMP` és `TMP`
  változókat a saját `updates\<verzió>\extract` mappájára,
- ha minden útvonal írásvédett, az updater **érthető magyar hibaüzenetet** ad a nyers
  „Access denied” helyett.

## Javított: „A launcher felülete nem tölthető be” (WebView2)

**Ok:** a WebView2 a leggyakrabban a felhasználói adatmappa sérülése/foglaltsága miatt nem indul
el (pl. erőből leállított launcher lock fájljai, vagy egy **részben alkalmazott frissítés** miatt
kevert verziójú `Microsoft.Web.WebView2.Core.dll` / natív `WebView2Loader.dll`), és ilyenkor a
hiba olvashatatlan `NullReferenceException` / `HRESULT 0x80004003` formában jelenik meg.
A naplóban emellett `ERROR_INVALID_STATE (0x8007139F)` volt látható a
`PostWebMessageAsJson` hívásnál (a felület épp töltött).

**Javítás:**

- `InitWebView` **önjavító**: 1) adatmappa az exe mellett → 2) ha az sérült/lockolt, **félreteszi**
  (`.broken-<dátum>`, nem törli!) és újrapróbálja → 3) tartalék mappa a
  `%LOCALAPPDATA%\TurulMC\WebView2` alatt; ha fájl áll a mappa helyén, azt is félreteszi,
- a hibaüzenet a felületen most részletes: kivétel, HRESULT, **WebView2 runtime verzió**,
  adatmappa útvonal, architektúra, és mit lehet tenni,
- **minden** WebView-üzenetküldés egy hibatűrő `TryPostWebMessage` segédfüggvényen megy át:
  a „még nem kész” állapot (`0x8007139F`) nem hiba, csak kimarad — így nem keletkezik hamis
  hibaüzenet és nem szakad meg a művelet.

## Javított: „Launcher felA!llÃ­tA!sa” — kódolás-rontás a magyar szövegekben

**Ok (a fejlesztés hibája):** három forrásfájlt (`MainWindow.xaml.cs`, `MainWindow.Packs.cs`,
`MainWindow.Recovery.cs`) egy PowerShell-alapú szövegcserével módosítottam, és a
`Get-Content`/`WriteAllText` a **magyar ANSI (CP1250)** kódlapon olvasta/írta a fájlokat. Ezzel a
UTF-8 tartalom CP1250-ként dekódolva **kétszeresen** bekerült a fájlba, ezért a felületen
`Launcher felA!llÃ­tA!sa elÅ‘kÃ©szÃ­tÃ©se` jelent meg.

**Javítás:** a rontott szakaszok visszafejtése (CP1250 → UTF-8) kontroll-karakter és
csere-karakter ellenőrzéssel, majd a fájlok mentése **UTF-8 BOM-mal** (így a fordító és minden
eszköz helyesen olvassa). Ellenőrzés: minden forrásfájl tiszta (nincs mojibake, nincs
0x80–0x9F tartományú karakter), és a **lefordított DLL-ben** is igazoltam a helyes magyar
szövegeket (UTF-16 kereséssel: „A launcher felülete nem tölthető be.” ✓, a rontott alak ✗).

## Javított: „Kilépés” után a launcher csak eltűnt (nem lépett ki)

**Ok:** a `ForceExitLauncher()` csak `Close()`-t hívott. A WinUI 3 nem garantálja, hogy az utolsó
ablak bezárásával a folyamat is kilép (a WebView2 háttérfolyamatai életben tartják), így a
launcher „eltűnt”, de tovább futott — a felhasználó számára úgy tűnt, mintha csak lekicsinyítődött
volna.

**Javítás:** a `ForceExitLauncher()` az ablak bezárása után **350 ms-mal kényszerítetten kilép**
(`Environment.Exit(0)`), a tálca ikon és a WebView2 is felszabadul; a kilépés naplózva van.

## Javított: a másolás-modál nem fért el a képernyőn

**Ok:** a modál `max-height`-ja CSS-ből örökölt értékre támaszkodott, hosszú
verzióütközés-listánál pedig magasabb lett a nézetnél: a teteje és a **lábléc (a „Másolás
indítása” gomb) is kicsúszott** a képernyőről.

**Javítás:**

- `fitModalToViewport()` — a modál megnyitásakor (és ablak-átméretezéskor) **JS-ből, inline
  stílussal** állítja be a `max-height`-ot a látható területhez, flex oszlopos elrendezéssel,
  görgethető törzzsel és mindig látható lábléccel. Ez CSS-hibáktól függetlenül garantált.
- CSS: az overlay görgethető (`align-items:flex-start` + `margin:auto`), a modál `max-height`
  `vh` **és** `dvh` fallbackkel, a mod-lista saját kis görgetéssel (112 px, alacsony ablaknál
  88 px), alacsony ablaknál kisebb belső margók és összecsukott „Másolandó tartalom”.
- A verzióütközés-panel listája 6 helyett 4 modot mutat („+N további mod”).

## Javított: „Access to the path '…\settings.json.<guid>.tmp' is denied” — és a játék indítása

**Tünet:** a játék indítása elhasalt, és a Hibamagyarázó ezt írta:
`Access to the path '…\AppData\Roaming\TurulMC\settings.json.<guid>.tmp' is denied.`

**Ok:** a launcher a beállításokat egy **új** temp fájlba írja, majd áthelyezi. A gépen egy
vírusirtó / a Windows **Controlled Folder Access** az AppData-ba írást blokkolja, ezért a temp
fájl létrehozása `Access denied`-be futott — és mivel ez a mentés az **indítási folyamat közepén**
történt, az egész indítás megszakadt.

**Javítás:**

- `SettingsStorage.SaveSettingsAsync` már **soha nem dob** (`AtomicFile`), és jelzi az eredményt
  (`LastSaveSucceeded` / `LastError`).
- `AtomicFile` új, **végső tartalék** lépcsője: ha a temp fájl létrehozása blokkolt, a launcher
  **közvetlenül a célfájlba ír** (a `.bak` biztonsági másolat ilyenkor is elkészül) — sok
  vírusirtó a már létező, ismert fájl felülírását engedi, csak az új temp fájlokat blokkolja.
- **Egy mentési hiba nem állítja meg a játékot**: az indítás lefut, a launcher pedig **egyszeri,
  érthető figyelmeztetést** küld a felületen („valószínűleg vírusirtó vagy Controlled Folder
  Access blokkolja a %APPDATA%\TurulMC mappát…”), és a naplóba is bekerül.
- A `settings.json`, `profiles.json`, `instances.json`, `servers.json`, Java-nyilvántartás és a
  crash-riport ugyanezt a robusztus írót használja.

**Amit a felhasználónak érdemes tennie:** a vírusirtóban (Defender → Védett mappák / Controlled
Folder Access, illetve a telepített AV) engedélyezni a `TurulMC.Launcher.exe`-t, vagy a mappát
kivenni a védett listából. E nélkül a beállítások nem maradnak meg (de a játék elindul).

## Szebb UI: saját megerősítő ablak a böngésző natív ablaka helyett

A másolás/„nem kompatibilis modok” kérdések eddig a WebView2 **natív** megerősítő ablakát
nyitották („A(z) turul.local üzenete”) — idegen, rendszerstílusú ablak a témaszínes launcherben.
Helyette új `askConfirm()`: saját, témaszínes, billentyűzet-barát (Esc = mégse) megerősítő réteg,
amely **külön rétegen** jelenik meg, így az alatta lévő modál (és a benne beállított cél Instance,
stratégia, jelölések) érintetlen marad. Átállítva mind a 7 helyen: szerver-verzió váltás, indítás
előtti mod-figyelmeztetés, Smart Repair javítás, másolás (fájlütközés / nincs frissítés /
változatlan másolás), mod törlése.

## Javított: ha a `%APPDATA%\TurulMC` nem írható, a launcher átvált (Instance-váltás is)

**Tünet:** a launcher ezt jelezte: „A launcher beállításait nem sikerült kiírni a lemezre…
`instances.json` is denied”, és **az Instance kiválasztása sem történt meg** (a főoldalon nem
változott az aktív Instance).

**Ok:** a gépen egy szűrő (vírusirtó / Windows Controlled Folder Access) **minden** írást
blokkol a `%APPDATA%\TurulMC` mappába. Ez nemcsak a mentést érintette: az Instance mappájának
létrehozása (`instances\<id>`) is `Access denied`-be futott, ezért az `instances.select` elhasalt,
és a felület nem tudott váltani.

**Javítás:**

- Új `TurulMC.Core.Storage.LauncherPaths`: **írhatósági próbával** választ adatkönyvtárat:
  `%APPDATA%\TurulMC` → `%LOCALAPPDATA%\TurulMC` → `%USERPROFILE%\TurulMC` → az exe melletti
  `data` mappa. Erre áll át az `instances.json`, `settings.json`, `profiles.json`,
  `servers.json`, a naplók, a modmentések, a Java runtime-ok és a Doctor is — így blokkolt
  mappa esetén is **minden mentés működik**.
- Váltáskor a **kis konfigurációs fájlok átmásolódnak** az új könyvtárba (az Instance-lista és a
  beállítások nem tűnnek el); a **nagy Instance-tartalom a helyén marad**, ezért nem kell újra
  letölteni a Minecraftot.
- `ConfigureInstanceServices` már **nem dob** mappahiba esetén: az Instance kiválasztása/váltása
  a memóriabeli állapottal akkor is működik.
- A launcher a felületen **egyszer jelzi**, ha nem a megszokott mappát használja (és megmondja,
  melyiket); a napló pedig mindig az aktuális, írható könyvtárba kerül.
- Új füstteszt (`LauncherPaths`) igazolja, hogy a feloldott könyvtár írható és a mentés oda
  sikerül — **50/50 zöld**.

**Tartós megoldás a gépen:** Defender → *Vírus- és fenyegetésvédelem* → *Zsarolóvírus-védelem*
(Controlled folder access) → engedélyezett alkalmazások közé a `TurulMC.Launcher.exe`, vagy vedd
ki a `%APPDATA%\TurulMC` mappát a védett listából; illetve a telepített vírusirtóban adj rá
kivételt. E nélkül a launcher a tartalék mappában dolgozik (működik, csak más helyen vannak az
adatok).

## Új: „Vírusirtó kizárás kérése” gomb (Beállítások) — de kizárás nélkül is működik

A Beállítások oldalon új sor: **Vírusirtó kizárás kérése**. Egy kattintásra a launcher
rendszergazdai engedéllyel (UAC) kizárást kér a Windows Defenderben:

- kizárt folyamat: `TurulMC.Launcher.exe`,
- kizárt útvonal: `%APPDATA%\TurulMC`,
- *Controlled Folder Access* → engedélyezett alkalmazás: a launcher exe-je.

Utána ellenőrzi, hogy a megszokott mappa tényleg írható-e, és sikeres kizárásnál jelzi, hogy
**újraindítás után** a launcher visszatér a megszokott adatmappához.

**A UAC megszakítása nem hiba:** ha a felhasználó nem ad engedélyt (vagy a kérés nem
érvényesül), a launcher **zavartalanul működik tovább** a tartalék adatkönyvtárban
(`LauncherPaths`), minden mentéssel és Instance-váltással együtt — ez a viselkedés külön
füstteszttel védve van.

## Javított: msedgewebview2.exe „Alkalmazáshiba (0x80000003)”

**Ok:** a WebView2 adatmappája (alapból az exe melletti `TurulMC.Launcher.exe.WebView2`) nem
volt írható — a vírusirtó / Controlled Folder Access blokkolta. A `msedgewebview2.exe`
ilyenkor belső végzetes hibát (`STATUS_BREAKPOINT 0x80000003`) jelez, és a Windows
„Alkalmazáshiba” ablakát dobja fel (jellemzően a betöltő képernyő megjelenésekor, mert akkor
ír először intenzíven a WebView2).

**Javítás:**

- a WebView2 adatmappa **írhatósági próbán** megy át; ha az exe melletti mappa nem írható,
  a launcher **eleve nem is próbálkozik vele**, hanem a `LauncherPaths` által igazolt,
  írható adatkönyvtárba teszi (`<adatkönyvtár>\WebView2`),
- a `msedgewebview2.exe` beépített crash-riportere kikapcsolva
  (`WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--disable-crash-reporter --disable-breakpad`),
  így egy háttérfolyamat hibája nem dob fel felhasználói hibapárbeszédet,
- a hiba továbbra is naplózva van, és a felület hibatájékoztatót mutat a szokásos módon.

## Kiadás előtt (még nyitott)

- A verziószám továbbra is 4.4.9 — a 4.6.0-ra lépéshez 6+ helyet kell összehangolni:
  `src\TurulMC.Launcher\TurulMC.Launcher.csproj`, `src\TurulMC.Cli\TurulMC.Cli.csproj`,
  `installer\TurulLauncher.iss`, `publish.cmd`, `build-installer.cmd`, `publish-all.cmd`,
  `tools\Validate-Release.ps1`, `tools\Prepare-FreeHosting.ps1`, `src\TurulMC.Cli\Program.cs`,
  `tools\Make-UpdateManifest.ps1` (ez még a 4.4.9-es changelogot írja be — audit M-32).
- Az élő Modrinth-letöltés és az Adoptium-letöltés ebben a környezetben nem volt kipróbálható
  (nincs hálózat): a hálózati útvonalak éles tesztje a felhasználóra vár.
