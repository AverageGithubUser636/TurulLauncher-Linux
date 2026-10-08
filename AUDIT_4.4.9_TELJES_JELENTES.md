# TurulMC Launcher 4.4.9 — Teljes audit jelentés

**Vizsgált repo:** `C:\Users\-\Desktop\Turul\TurulLauncher`
**Dátum:** 2026-10-03
**Módszer:** statikus forráskód-audit (teljes fájllefedés) + build- és teszt-ellenőrzés
**Hatókör:** `src\` (5 .NET projekt), `tests\`, `src\main\java\` (legacy Java), `installer\`, `tools\`, `server\`, `docs\`, gyökér scriptek és konfigurációk
**Kizárva:** `bin\`, `obj\`, `publish\` build-kimenetek, WebView2 Chromium profil, harmadik féltől származó binárisok tartalma

---

## 1. Vezetői összefoglaló

A TurulLauncher egy **érett, sok funkciós, alapvetően jól megírt launcher**, amelynek biztonsági alapvonala (hash-ellenőrzés a modpackeknél, path traversal védelem, ArgumentList-alapú process-indítás, jogosultság-emelés nélküli per-user telepítés, titokmaszkolás a logokban) **jobb, mint a tipikus hobbi-launchereké**. A 4.4.6-os audit-fix intézkedései valóban benne vannak a kódban (lásd 7. fejezet), a build lefut, a recovery smoke tesztek 20/20, a 62 unit teszt 62/62 zöld — igaz, ez utóbbi épp azt bizonyítja, hogy a tesztkészlet nem a produkciós kódot méri (H-02, H-03).

Ugyanakkor a **frissítési lánc (update chain) és a WebView2 híd (bridge) bizalmi modellje nem áll össze**: a frissítési manifest nincs aláírva, a launcher a manifestből kapott hash-t adja tovább az updaternek, a `--version` érték validálatlanul épül be fájlrendszer-utakba, a frissítő örökre várhat a launcherre, a beágyazott HTML pedig több ponton escapeletlen adatot injektál az `innerHTML`-be, miközben a lap minden joga (játékindítás, fájlműveletek, önfrissítés, shell-open) a webbridge-en keresztül érhető el, **origin- és navigáció-ellenőrzés nélkül**.

**Összesített megállapításszám:** 12 MAGAS (ebből 2 a nem szállított Java legacyban), 42 KÖZEPES, 40 ALACSONY, 6 INFO.
**KRITIKUS besorolású, távolról azonnal kihasználható hiba nem található** — a legsúlyosabb láncok vagy a `turulnetwork.hu` hoszt/manifest feletti ellenőrzést, vagy helyi argumentum-befolyásolást igényelnek. A Java legacy útvonalán viszont **lenne** kritikus szintű hiba (hash nélküli letöltés + tetszőleges jar futtatása, validálatlan verzió-iddel path traversal) — ez ma azért nem az, mert a komponens nincs a buildben és nem szállítjuk.

### A legfontosabb 10 kockázat (javítási sorrendben)

| # | Kockázat | Hol | Súly |
|---|----------|-----|------|
| 1 | A toast-renderelő escapeletlen `innerHTML`-je + mentett szervernév → **XSS a launcher originon**, ami a teljes webbridge-et megnyitja | `ui\index.html:2824`, `:2792` | MAGAS |
| 2 | Nincs `NavigationStarting`/`NewWindowRequested` és nincs `e.Source` ellenőrzés: idegen origin is küldhet privileged üzenetet | `MainWindow.xaml.cs:307-337, 355` | MAGAS |
| 3 | Az update manifest **aláírás nélküli**, a hash a manifestből érkezik (nincs második horgony) | `TurulMC.Updater\Program.cs:31-43` | MAGAS |
| 4 | `--version` validálatlan → **path traversal / UNC írás** (a launcher a manifest verziójából épít könyvtárat) | `TurulMC.Updater\Program.cs:37, 361-364` | MAGAS |
| 5 | A frissítő **korlátlanul vár** a launcher kilépésére, amit a launcher saját close-veto-ja akadályoz meg | `TurulMC.Updater\Program.cs:381, 543-551` | MAGAS |
| 6 | A Minecraft kliens/librériák/native-ok és a Fabric libek letöltése **nincs hash-ellenőrzéshez kötve** (a `ValidateVersionFilesAsync` halott kód) | `MinecraftInstallationService.cs:71-160, 166-186` | MAGAS |
| 7 | A `openUrl` bármely http(s) URL-t shell-open-nel indít; a hír-URL-ből `javascript:` is átjut az inline `onclick`-ba | `MainWindow.xaml.cs:1760-1767`; `index.html:2189-2192` | MAGAS |
| 8 | **A kiadási lánc egyetlen eleme sincs aláírva** (Setup `NotSigned`), a launcher pedig egy felhasználó által írható könyvtárban fut | `build-installer.cmd:44-51`, `installer\TurulLauncher.iss:16` | MAGAS |
| 9 | Két biztonsági teszt (path traversal, CLI argumentumok) **a saját másolatát teszteli**, nem a produkciós kódot | `PathTraversalTests.cs:81-92`; `CliArgumentTests.cs:237-286` | MAGAS |
| 10 | A Java legacy launcher hash nélkül tölt és futtat távoli jart, validálatlan verzió-iddel (ma nem szállított) | `VersionManager.java:101-116, 133-135` | MAGAS* |

---

## 2. A vizsgálat tárgya és módszere

**Elolvasva teljes terjedelemben:** `src\` összes .cs/.xaml fájlja (66 C# fájl, ~15 000 sor), `ui\index.html` (5 273 sor), `ui\recovery.js`, `src\main\java\` (6 fájl), `installer\TurulLauncher.iss`, `tools\*.ps1`, minden gyökér `*.cmd`, `server\**`, `docs\**`, `*.csproj`, `TurulMC.sln`, `pom.xml`, `FINAL_RELEASE_CHECKLIST.txt`, `PATCH_4.4.6_AUDIT_FIX.txt`.

**Futtatás/ellenőrzés (nem csak olvasás):**

| Vizsgálat | Eredmény |
|-----------|----------|
| `dotnet build TurulMC.sln -c Release` | ✅ **Build succeeded**, 0 hiba (csak NU1900 figyelmeztetés: a NuGet sebezhetőségi adatbázis nem érhető el a mérési környezetből) |
| `dotnet run --project tests\TurulMC.Recovery.SmokeTests` | ✅ **20/20 PASS** — „All 20 recovery/diagnostic checks passed.” |
| `tests\TurulMC.Core.Tests` összes tesztesete (62) | ✅ **PASS=62, FAIL=0, SKIP=0** — a `dotnet test` testhostja a sandboxban nem indul el (ezért jelent meg „Alkalmazáshiba” dialógus), a tesztmetódusok in-process futtatva, lásd 9.1 |
| `publish\TurulLauncher-4.4.9.zip` tartalma (620 entry, 337 MB kitömörítve) | ✅ nincs PDB, nincs WebView2 profil **ebben** a példányban |
| `prereqs\vc_redist.x64.exe` Authenticode | ✅ `Valid`, aláíró: `CN=Microsoft Corporation` |
| `publish\Installer\TurulLauncher-Setup-4.4.9.exe` Authenticode | ❌ **`NotSigned`** (lásd H-10) |
| Kiadási hash-lánc (ZIP ↔ `publish\stable.json` ↔ `server\update\stable.json`) | ✅ mindhárom `503d2390225dc02bb22983deeb5edd26ab67fc2cdc2cb49657870b6e029bfa97` |
| Titok-keresés (token/jelszó/kulcs/`C:\Users\`) a forrásfában | ✅ forrásban nincs beégetett hitelesítő adat |

**Korlátok:** dinamikus/teljesítmény- és runtime-viselkedés nem lett mérve (nem indítottam el a GUI-t, a frissítést és a játékot). A `publish\` és `bin\` mappa nem képezi az audit tárgyát, csak a kiadási higiénia szempontjából vizsgáltam. A `turulnetwork.hu` szerveroldal (manifest-kiszolgálás, hosztolás, CDN) nem volt elérhető, ezért a szerveroldali állítások nem ellenőrizhetők.

---

## 3. Rendszerkép: mi épül miből

```
TurulMC.sln  (5 projekt)
├── src\TurulMC.Core            net10.0            – domain: telepítés, indítás, modpack, recovery, státusz
├── src\TurulMC.Infrastructure  net10.0            – settings/profil perzisztencia (+ 3 halott duplikált osztály)
├── src\TurulMC.Launcher        net10.0-windows    – WinUI 3 (WindowsAppSDK 2.4.0) + WebView2 UI (ui\index.html)
├── src\TurulMC.Cli             net10.0            – CLI (TurulLauncher.CLI.exe)
└── tests\TurulMC.Core.Tests    net10.0 / xunit v2 – 64 teszteset

NINCS a solutionben:  src\TurulMC.Updater  (a frissítő, biztonságkritikus!)
                     tests\TurulMC.Recovery.SmokeTests
```

- **A termék a C# launcher.** A `src\main\java\` + `pom.xml` (1.0.0, Java 8, Gson/Guava) **legacy maradvány**: egyetlen build-, publish- vagy telepítő script sem hivatkozik rá, és a `publish-all.cmd` folyamat kizárólag a .NET projekteket építi. A Java oldal karbantartása tiszta veszteség, a benne lévő ismeretlen állapotú és integritás-ellenőrzés nélküli letöltő/auth-kód pedig kockázat (lásd J-01, J-02, M-34…M-35, M-42).
- **A GUI egyetlen beágyazott HTML-lap** (`ui\index.html`), amelyet WebView2 jelenít meg a `https://turul.local` virtuális hoszton. Nincs `AddHostObjectToScript`/`ExecuteScriptAsync` — a híd kétirányú JSON (`WebMessageReceived` ↔ `PostWebMessageAsJson`), egy ~130 ágas `switch`-csel (`MainWindow.xaml.cs:355-493`). Ez jó döntés (nincs privilegizált COM-objektum a JS-ben), **de minden jog a lapon keresztül érhető el**, ezért a lap XSS-állósága kritikus.
- **Verzió:** minden csproj/script/installer 4.4.9-en áll (41 találat), a gyökérben lévő `PATCH_*`/`TEST_*`/`DISCORD_CHANGELOG_*` fájlok történeti dokumentumok. Egyedül a `FINAL_RELEASE_CHECKLIST.txt` és a `server\update\beta.json` maradt elavult (4.3.4, illetve 4.4.5).

---

## 4. Build-, teszt- és kiadási állapot

| Terület | Állapot | Megjegyzés |
|---|---|---|
| Build (Release, 5 projekt) | ✅ zöld | 0 warning a saját kódból; NU1900 csak környezeti |
| Unit tesztek (62 eset) | ✅ 62/62 zöld (in-process futtatva) | a zöld suite **nem érzékeli** a produkciós kód eltéréseit (H-02, H-03, L-15) |
| Recovery smoke tesztek (20 eset) | ✅ 20/20 | valódi, értelmes állításokkal |
| Fedettség | ⚠️ hiányos | a **frissítőnek, a GUI bridge-nek, a pack-telepítőnek és a Minecraft-telepítésnek nincs egyetlen automatizált tesztje sem** |
| Kiadási pipeline | ⚠️ működik, de sérülékeny | `publish.cmd` lokkol, ZIP-et készít, manifestet generál, `Validate-Release.ps1` ellenőriz (lásd M-26…M-32) |
| Verziókezelés | ❌ **nincs** | nincs `.git`, nincs `.gitignore`; a fában 1,59 GB build-artefaktum és bináris (lásd M-29) |

---

## 5. Megállapítások

Jelölések: **H** = magas, **M** = közepes, **L** = alacsony, **I** = info. Minden tételnél a hivatkozott sorok ellenőrzöttek; ahol saját magam is visszaellenőriztem a kódot, ott a hivatkozás mellett nincs külön jelzés (a mintavételes ellenőrzés a H-01…H-09 tételek mindegyikére kiterjedt).

### 5.1 MAGAS (HIGH)

**H-01 — Nincs hash-ellenőrzés a Minecraft kliens/librériák/native-ok/assets és a Fabric libek letöltésénél; az egyetlen ellenőrző halott kód**
`src\TurulMC.Core\Minecraft\MinecraftInstallationService.cs:71-160, 166-186` · `src\TurulMC.Core\Minecraft\FabricLoaderService.cs:108-115`
Bizonyíték: `DownloadTrackedAsync(detail.Downloads.Client.Url, clientJarPath, …)` (`:79-81`), a library-ciklus (`:121`), az asset-index (`:153-155`) és az asset-ciklus (`:243-248`) soha nem hasonlít semmit a `Downloads.Client.Sha1` / `Artifact.Sha1` értékhez; a `MinecraftLibrary.Sha1` és `AssetIndex.Sha1` mezőket a kód **sehol nem olvassa** (`grep '\.Sha1'` a Core-ban csak a `:174`/`:180` halott metódusra talál). A `ValidateVersionFilesAsync` (`:166-186`) egyetlen hívója sincs a repóban.
Hatás: a `-cp`-re kerülő és **végrehajtott** jar-ok integritása nem ellenőrzött. A modpack-útvonalon viszont kötelező a hash — ez az aszimmetria a legfeltűnőbb.
Javítás: a `DownloadTrackedAsync`-ben a temp fájl átnevezése előtt számolt SHA-1/SHA-256 összevetése (a Mojang metadata sha1-et ad), eltéréskor törlés + kivétel; a Fabric libeknél ugyanez.

**H-02 — A `CliArgumentTests` a saját másolatait teszteli, és el is tér a produkciótól**
`tests\TurulMC.Core.Tests\CliArgumentTests.cs:237-286`
Bizonyíték: a fájl végén `// --- Local helper methods mirroring MinecraftLauncherService logic ---` és saját `BuildJvmArguments`/`BuildGameArguments`/`BuildClasspath`; egyetlen teszt sem példányosít `MinecraftLauncherService`-t. A másolatok **mást állítanak**, mint amit a produkció csinál: a teszt `-XX:+UseG1GC`-t vár (`:29`) és `SystemProperties` → `-Dkey=value` leképezést (`:33-48`), miközben a valódi `BuildJvmArguments` (`MinecraftLauncherService.cs:550-567`) egyiket sem tartalmazza, a `LaunchConfig.SystemProperties`-et pedig a repó sehol nem olvassa; a teszt `--versionType` = `TurulMC`-t vár (`:204`), a produkció `config.VersionType` = `release`-t ad (`:584`); a teszt classpath-szűrője üres stringeket dob, a produkció `File.Exists`-et (`:595`).
Hatás: a zöld teszt **hamis biztonságérzet**; a valódi argumentum-építés (és annak quoting/injection-viselkedése) teszt nélkül van. **Mérve:** mind a 62 teszteset zölden lefutott — köztük a `BuildJvmArguments_IncludesG1GC` (`:29`) —, miközben a produkciós `BuildJvmArguments` (`MinecraftLauncherService.cs:550-567`) nem ad `-XX:+UseG1GC`-t, tehát a suite **bizonyítottan** nem érzékeli az eltérést.
Javítás: a valódi szolgáltatás példányosítása és a `ProcessStartInfo.ArgumentList` eredményének vizsgálata; a másolatok törlése.

**H-03 — A `PathTraversalTests` soha nem hívja a `PathSecurity`-t**
`tests\TurulMC.Core.Tests\PathTraversalTests.cs:81-92` (állítások: `:13-78`)
Bizonyíték: minden elmélet és tény a fájl végén lévő privát `SanitizePath` helperen megy át, amely a produkciós logika **duplikátuma**; a `PathSecurity`-t egyedül az `AuditFixTests.cs:13-26` érinti. A tartalmazás-ellenőrzés `Assert.StartsWith(InstanceDir, fullPath)` (`:68`, `:78`) sima prefix-teszt elválasztó nélkül.
Hatás: a 12 teszt akkor is zölden fut, ha a produkciós védelem teljesen eltűnik. **Mérve:** a suite 62/62 zöld, és a `PathSecurity`-t egyedül az `AuditFixTests` három esete érinti — a `PathTraversalTests` 12 esete a saját másolatát zöldíti.
Javítás: a helper törlése, `PathSecurity.ResolveInsideRoot` közvetlen hívása `..`/abszolút/`:` bemenetekre, elválasztó-tudatos tartalmazás-ellenőrzéssel.

**H-04 — A toast-renderelő escapeletlen `innerHTML`-je, mentett szervernévből táplálva → XSS a launcher originon**
`src\TurulMC.Launcher\ui\index.html:2824` (forrás: `:2792`, `:2762`, `:2783`)
Bizonyíték: `el.innerHTML = '<span class="toast-icon">' + (toastIcons[type] || toastIcons.info) + '</span><span class="toast-msg">' + msg + '</span>';` — a `msg` **nincs escape-elve**, miközben a fájl ~50 másik helyen helyesen használ `escapeHtml`-t. Konkrét útvonal: `showToast("success", `Törlve: ${name||""}`)` (`:2792`), ahol a `name` a felhasználó által szerkeszthető, perzisztált szervernév (`servers.add`, `:2783`), és az `escapeHtml` a `<img src=x onerror=…>` alakot nem semlegesíti (a tag-név utáni szóközt nem kódolja).
Hatás: tetszőleges JS fut a `https://turul.local` originon, ahonnan a **teljes privilegizált híd** elérhető (játékindítás, `saveSettings`, `modpack.import`, `openUrl`, önfrissítés).
Javítás: `el.querySelector('.toast-msg').textContent = String(msg)` (vagy `escapeHtml(msg)`).

**H-05 — Nincs navigáció-korlátozás és nincs origin-ellenőrzés a webbridge-en**
`src\TurulMC.Launcher\MainWindow.xaml.cs:307-337, 355-365`
Bizonyíték: az egyetlen keményítés a `SetVirtualHostNameToFolderMapping("turul.local", uiFolder, Allow)` + `Navigate("https://turul.local/index.html")`; a repóban **nincs** `NavigationStarting`, `NewWindowRequested`, és az `OnWebMessageReceived` soha nem vizsgálja az `e.Source`-ot, miközben `IsWebMessageEnabled = true` (`:317`), a DevTools pedig csak a `#else` ágban tiltott (`:309`/`:312`).
Hatás: a lapot bármely navigáció (vagy a H-04 XSS) idegen originra viheti, amely örökli az üzenetküldési jogot, és `{"action":"game.launch"}`/`saveSettings`/… üzeneteket küldhet.
Javítás: `NavigationStarting`/`NewWindowRequested` megszakítása minden nem `https://turul.local/` célra, és az üzenetek elutasítása, ha `e.Source` nem az a origin; `AreDevToolsEnabled = false` feltétel nélkül.

**H-06 — Az `openUrl` bármely http(s) URL-t shell-open-nel indít; a hír-URL-ből `javascript:` is átjut az inline `onclick`-ba**
`src\TurulMC.Launcher\MainWindow.xaml.cs:1760-1767`, `:1603-1609` · `src\TurulMC.Launcher\ui\index.html:2189-2192`
Bizonyíték: `if (Uri.TryCreate(url, …) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)) Process.Start(new ProcessStartInfo { FileName = uri.ToString(), UseShellExecute = true });` — csak a séma szűrt, a hoszt nem. A hírkártya pedig `onclick="openNews('${escapeHtml(n.url).replace(/&#39;/g, "\\'")}')"` módon épül: az escape-elés **után** visszaalakított aposztróf és a nyers `"` miatt az attribútum kiszakítható; a `NormalizeNewsUrl` (`:1603-1609`) nem ellenőriz sémát, így a `TryParseNewsJson`-ból (`:1508`) érkező `javascript:` érték túlél.
Hatás: a launcher originjáról (és a távoli hírtartalomból) indítható tetszőleges URL/handler, valamint a H-04-hez hasonló markup-injekció.
Javítás: hoszt-allowlist (turulnetwork.hu + ismert docs-oldalak), `NormalizeNewsUrl` csak http/https, és az inline `onclick` cseréje `addEventListener` + `data-*` attribútumra.

**H-07 — Az update manifest nincs aláírva; az integritás egyetlen horgonya a manifestből érkező hash**
`src\TurulMC.Updater\Program.cs:31-43` · `src\TurulMC.Launcher\MainWindow.xaml.cs:1739-1740, 1661-1677`
Bizonyíték: az updater a várt digestet közvetlenül az argv-ből veszi (`Require(parsed, "sha256")`, `:34`) és csak az alakját ellenőrzi (`^[a-fA-F0-9]{64}$`, `:42`); az érték a `https://turulnetwork.hu/launcher/update/{stable,beta}.json`-ból származik. A manifest csak `version/required/url/sha256/publishedAt/changelog` mezőket tartalmaz aláírás nélkül (`server\update\stable.json`); a repóban nincs `X509`/`SignedCms`/`WinVerifyTrust`/`ECDsa` hivatkozás.
Hatás: aki a manifestet kiszolgálni/ módosítani tudja (hoszt/CND kompromittálás, kiszivárgott deploy-kulcs, DNS+TLS törés), **saját ZIP-et a saját hash-ével** publikál, és kódot futtat minden kliensen; a hash-ellenőrzés csak korrupció ellen véd.
Javítás: a manifest aláírása (Ed25519/ECDSA) a binárisba égetett kulccsal, az aláírás ellenőrzése a `version+url+sha256` felett, még az updater indítása előtt; a hash-ellenőrzés maradjon második védelmi vonal.

**H-08 — A `--version` validálatlan, fájlrendszer-utat épít belőle (traversal / UNC)**
`src\TurulMC.Updater\Program.cs:37, 361-364` · `src\TurulMC.Launcher\MainWindow.xaml.cs:1715-1725`
Bizonyíték: `parsed.GetValueOrDefault("version", "unknown")` (`:37`) — se regex, se `Path.GetFullPath` (ellentétben az `--install-dir`/`--launcher` értékekkel, `:35-36`), majd `Path.Combine(…, "TurulMC", "updates", _options.Version)` (`:361`) és `$"TurulLauncher-{_options.Version}.zip.part"` (`:363`). A launcher ugyanezt a könyvtárat használja (`:1715-1717`, `:1724-1725`), és a `manifest.Version`-t adja tovább validálatlanul (`:1745-1746`).
Hatás: `..\..\..\Windows\Temp\x` kilép a staging fából; gyökérrel kezdődő érték esetén a `Path.Combine` szó szerint azt adja vissza, így `\\attacker\share\x` **UNC-írás** (SMB/NTLM) vagy tetszőleges helyi írás érhető el a hivatalos ZIP + valódi hash kombinációjával is. A manifest-ági kihasználás előfeltétele, hogy a verzió-string összehasonlítás „újabbnak” lássa (pl. `9.9.9\..\..\…`, mert a nem parse-olható komponens 0-nak számít), a helyi argumentum-befolyásolás pedig önmagában elég.
Javítás: szigorú `^[0-9A-Za-z._-]{1,32}$` validálás **mindkét** oldalon, gyökér/UNC elutasítása, és GUID-alapú staging könyvtár a verzió helyett.

**H-09 — A frissítő korlátlanul várhat a launcherre; a launcher saját close-veto-ja életben tartja**
`src\TurulMC.Updater\Program.cs:381, 543-551` (és `:343`) · `src\TurulMC.Launcher\MainWindow.xaml.cs:1751-1755, 3281-3292`
Bizonyíték: `using var process = Process.GetProcessById(pid); await process.WaitForExitAsync(token);` — nincs határidő, csak `catch (ArgumentException) { }`; a cancel-ág ráadásul `WaitForProcessAsync(_options.Pid, CancellationToken.None)`-nal **nem megszakíthatóan** vár (`:343`). A launcher az updater indítása után `Close()`-t hív (`:1751-1755`), de a saját bezárás-kezelője ezt megtiltja (`args.Cancel = true`, `:3281-3288`), hacsak `_forceWindowClose` nincs beállítva (csak `ForceExitLauncher()`, `:3290-3292`). `CloseBehavior = "ask"` esetén (`LauncherSettings.cs:58-59`) a dialógus alapértelmezett gombja a tálcára rejtés → a folyamat fut tovább → a frissítő örökre „Waiting for TurulLauncher to close” állapotban marad. PID-újrahasznosítás esetén ugyanez.
Hatás: a felhasználó beragad egy félkész frissítésbe; rossz esetben nem indul újra a launcher.
Javítás: a launcher kényszerített kilépése (`_forceWindowClose = true` + tálcaikon elengedése) az updater indítása előtt, valamint az updater oldalán korlátos, identitás-ellenőrzött várakozás (`Process.StartTime`/`MainModule.FileName` vagy named event), fail-closed timeouttal.

**H-10 — A kiadási lánc egyetlen eleme sincs aláírva, miközben a launcher felhasználó által írható könyvtárban fut**
`build-installer.cmd:44-51` · `installer\TurulLauncher.iss:16` · `tools\Validate-Release.ps1:15-26`
Bizonyíték: `Get-AuthenticodeSignature publish\Installer\TurulLauncher-Setup-4.4.9.exe` → **`NotSigned`** (saját mérés); sem a `build-installer.cmd`, sem a `publish.cmd`, sem a csproj nem hív `signtool`-t és nem állít aláírás-tulajdonságot; a 620 fájlos ZIP payload szintén aláíratlan. A launcher `{localappdata}\Programs\TurulLauncher`-ba települ (`.iss:16`), és indításkor nincs integritás-ellenőrzés.
Hatás: a felhasználó nem tudja hitelesíteni a letöltést (SmartScreen/„ismeretlen kiadó” figyelmeztetés is), és bármely helyi folyamat csendben kicserélheti a `TurulMC.Launcher.exe`/`TurulMC.Updater.exe` párost, amelyek egymást frissítik.
Javítás: Authenticode-aláírás a Setupra, a launcherre és az updaterre, plusz a Setup SHA-256 összegének publikálása a letöltési link mellett.

**Java legacy — magas súlyú, de ma nem szállított komponens** (a `src\main\java` nincs a solutionben, egyetlen script sem hivatkozik rá, a ZIP/telepítő nem tartalmazza; ezért nem része a termék kockázatának, de **azonnal kritikussá válik, ha bárki újraéleszti vagy szállítani kezdi**):

- **J-01 — A Java launcher hash-ellenőrzés nélkül tölt le és futtat távoli jart.** `src\main\java\net\turulmc\launcher\download\VersionManager.java:101-116`: a `VersionsManifest.VersionEntry` parse-ol egy `md5` mezőt (`VersionsManifest.java:120`), amit **soha nem olvas ki senki**; a letöltött `turulClientUrl` tartalma digest/méret/aláírás nélkül a mods könyvtárba kerül, és az a classpathra (`MinecraftProcess.java:15-18, 145-153`). A manifest forrása `https://raw.githubusercontent.com/TurulSpigot/TurulSpigot/gh-pages/versions.json` (`VersionsManifest.java:14-18`) → távoli kódfuttatás a játékban, integritás-kapu nélkül.
- **J-02 — Validálatlan verzió-id → path traversal írás.** `VersionManager.java:133-135`: `new File(versionsDir, version)` és `new File(versionDir, version + ".jar")`, ahol a `version` a manifestből feltöltött combo box értéke (`:47-49`, `TurulLauncher.java:220`); egy `..\..\..\Users\<u>\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\x` alakú id a `~/.turulmc`-n kívülre ír. Javítás: `[A-Za-z0-9._-]` allowlist + a C# oldalon már meglévő `ResolveInsideRoot`-hoz hasonló védelem — vagy a komponens törlése.

### 5.2 KÖZEPES (MEDIUM)

**Core**

- **M-01 — A metadata-vezérelt letöltéseknél nincs séma/hoszt ellenőrzés.** `EnsureSafeHttpsUrl` létezik (`Security\PathSecurity.cs:68-74`), de csak a `ModpackService` (`:48,63`) és a `LocalServerService` (`:36`) hívja; a `MinecraftInstallationService` (`:318-345`) és a `FabricLoaderService` (`:108-115`) a metadata URL-jét változtatás nélkül tölti le. Javítás: minden letöltés előtt `EnsureSafeHttpsUrl`.
- **M-02 — A native-jar kinyerés nem gyökér-ellenőrzött (zip-slip felület).** `MinecraftInstallationService.cs:405-427`: `Path.Combine(nativesDir, entry.Name)` + `ExtractToFile`; a .NET a `Name`-et `ParseFileName(FullName, versionMadeByPlatform)`-on keresztül adja, a Unix-ág nem vágja le a backslasht, így egy Unix-on készült archívum `..\..\evil.dll` entryje kiléphet. Javítás: `PathSecurity.ResolveInsideRoot(nativesDir, entry.FullName)`.
- **M-03 — A Maven-koordináta szegmensek validálatlanok.** `MinecraftInstallationService.cs:289-316`, `MinecraftLauncherService.cs:712-721`, `FabricLoaderService.cs:190-202`: `lib.Name.Split(':')` eredménye közvetlenül útvonalba kerül; `com.x:y:..\..\..\evil` esetén kilépés. Javítás: szegmens-validálás vagy `ResolveInsideRoot`.
- **M-04 — A `PathSecurity` csak a levél-elemet vizsgálja reparse szempontból.** `Security\PathSecurity.cs:10-66` vs. a helyesen járó `ModRecoveryService.EnsureNoLinks` (`Recovery\ModRecoveryService.cs:288-296`): egy `<instance>\mods` → `C:\Windows\Temp` junction (nem igényel emelést) mellett a string-ellenőrzések átengedik az instance-on kívüli írást/törlést. Javítás: az ancestor-walking ellenőrzés átemelése a `PathSecurity`-be és hívása a `ResolveInsideRoot`-ból.
- **M-05 — A Java-verziódetektálás blokkolhat (a timeout elérhetetlen).** `Java\JavaRuntimeService.cs:165-188`: a `StandardError.ReadToEnd()`/`StandardOutput.ReadToEnd()` a `WaitForExit(5000)` **előtt** fut szinkron, így egy nem záródó stream véglegesen blokkol; a `using` nem öl meg folyamatot. Ez a JAVA_HOME, négy Program Files-fa, a `.minecraft\runtime` és minden PATH-elem vizsgálatakor lefut. Javítás: `WaitForExitAsync` timeouttal + `Kill(entireProcessTree: true)`.
- **M-06 — A letöltési temp fájl fix nevű, a commit delete-then-move.** `Http\LauncherHttpClient.cs:51-105`: `destinationPath + ".tmp"` → két párhuzamos letöltés ütközik, a `catch` a másik temp fájlját törli; a `File.Delete` + `File.Move` nem atomikus. Javítás: GUID-os `.part` + `File.Move(..., overwrite: true)`.
- **M-07 — Hibás/null JSON NRE-t és KeyNotFound-ot dob a clean hiba helyett.** `MinecraftInstallationService.cs:85-100` (`"libraries": null` → NRE a `detail.Libraries.Count`-nál), `:209-234` (`"versions": null`, hiányzó `objects`, rövid hash → `hash[..2]` kivétel), `MinecraftLauncherService.cs:480` (`minecraftVersion: null`). Javítás: null-coalescing + hash-formátum ellenőrzés.
- **M-08 — A `"files": null` manifest NRE-t okoz, a `Path` nincs validálva.** `Modpacks\ModpackService.cs:55-64, 140, 158`. Javítás: `manifest.Files ?? new()` és `Path` előzetes ellenőrzés.
- **M-09 — `ServerListStorage`: null `Id`/`Name` → NRE, és a korrupt `servers.json` csendben ürül.** `Servers\ServerListStorage.cs:21-70`: csak `Host`-ot szűr (`:28`), minden kivételt elnyel (`:30`), majd a következő mentés **felülírja** a fájlt. Javítás: null-biztos összehasonlítás és hiba jelzése (`.bak`) az ürítés helyett.
- **M-10 — A modpack-export escapeletlen zip entry neveket ír.** `Modpacks\ModpackPackager.cs:19-39`: `"overrides/" + f.Path.Replace('\\','/')` a nyers manifest-úttal, miközben a `ResolveInsideRoot` a `..` szegmenseket **eldobja** (nem dob) → `overrides/../../evil.txt` kerül az archívumba. Javítás: `SanitizeRelative` + eltérés esetén hiba.
- **M-11 — Az `ImportMrpackAsync` eldobja a modokat és nincs kicsomagolási korlát.** `Modpacks\ModpackPackager.cs:41-62`: csak az `overrides/`/`client-overrides/` másolódik, a `files` tömböt sosem olvassa, így „sikeres” import 0 moddal; méretkorlát sincs (zip-bomba). Javítás: `files` deszerializálása + tömörítetlen méret/totál cap.
- **M-12 — A Core/Infrastructure duplikátumok már elcsúsztak.** `Core\Logging\LauncherLogger.cs:82-93` vs. `Infrastructure\Logging\LauncherLogger.cs:84-93`: az Infrastructure `MaskSecrets`-ből hiányzik az `auth_access_token` szabály, így egy „azonos nevű” csere elkezdené maszkolatlanul logolni. Ugyanez a helyzet a `LauncherHttpClient` (60 s vs 30 s, eltérő progress-típus) és a bájt-azonos `Sha256Service` esetében.
- **M-13 — A `LauncherSettingsValidator.Validate(LauncherSettings)` soha nem hívott.** `Validation\LauncherSettingsValidator.cs:18-37`: a RAM-korlátok, port, host, loader- és manifest-URL ellenőrzés **hatástalan**; a validálatlan `settings.LoaderVersion` közvetlenül útvonalba/URL-be kerül (`MinecraftLauncherService.cs:527-528`, `FabricLoaderService.cs:41,51`).

**Launcher (GUI)**

- **M-14 — A splash-preload 30 s-ig várhat egy már megsemmisített ablakra.** `MainWindow.xaml.cs:142-164`: `SW_HIDE` után `await _webViewReadyTcs.Task.WaitAsync(TimeSpan.FromSeconds(30))`, miközben a rejtett ablak bezárása nem szakítja meg a várakozást; utána `ShowAfterSplash()`/`Close()` egy halott ablakon fut, végül FatalStartup dialógus.
- **M-15 — A `settings.json` read-modify-write verseny miatt beállítás veszhet el.** `MainWindow.xaml.cs:1775-1857`: kulcsonként `LoadSettingsAsync` → módosítás → `SaveSettingsAsync`, a `SettingsStorage` (`:43-84`) csak az írást lockolja; a lap több `settings.save`-ot küld párhuzamosan (`index.html:3464-3484`). Javítás: egy nem újrabelépő kritikus szakasz, vagy többkulcsos patch API.
- **M-16 — Rendszerszintű kivétel-elnyelés, nyers kivételszöveggel a UI-ban.** `App.xaml.cs:139-170`, `MainWindow.xaml.cs:467-488`, `SingleInstance.cs:22-43`: ~40 üres `catch { }`; a `SingleInstance.TryAcquire` hibája esetén a második indítás **csendben semmit nem tesz**; a message-pump `ex.Message`-t ad vissza a UI-nak, amely azt `innerHTML`-be teszi (lásd H-04).

**Updater / CLI / Infrastructure**

- **M-17 — A teljes `*.turulnetwork.hu` zóna bizalmas frissítési forrás.** `TurulMC.Updater\Program.cs:76-82` (és `MainWindow.xaml.cs:1653-1659`): `Host.EndsWith(".turulnetwork.hu")` — egy felhasználói tartalomra használt vagy elfeledett subdomain önmagában elég az „arbitrary ZIP → install dir → indítás” lánchoz. Javítás: hoszt **és** útvonal-prefix (`turulnetwork.hu/launcher/releases/`) pin.
- **M-18 — `--install-dir` és `--launcher` validálatlan + `ShellExecute`.** `TurulMC.Updater\Program.cs:35-36, 626-631`: nincs ellenőrzés, hogy a telepítési könyvtárban ott van a `TurulMC.Launcher.exe`, illetve hogy a `--launcher` azon belül van; a `UseShellExecute = true` bármit elindít/megnyit. Hatás: tetszőleges felhasználói könyvtár felülírása a hivatalos payloaddal + „megbízhatónak látszó” indítás (emelés nélkül, mert a telepítés per-user).
- **M-19 — Nem atomikus, helyben történő csere; a félig írt fájl nem kerül a rollback-listára.** `TurulMC.Updater\Program.cs:513-523, 743-767`: `File.Copy(..., true)` közvetlenül az élő binárisra, a `written.Add(rel)` csak **utána** fut (`:519`), így a 12 próbálkozás után dobott hiba esetén a csonka `TurulMC.Launcher.exe`/`.dll` nem áll vissza.
- **M-20 — A rollback hibái elnyelve, a backup azonnal törölve.** `TurulMC.Updater\Program.cs:525-531, 755-769`: `catch { }` a helyreállításban, majd `finally { TryDelete(workRoot); }`; az `update-success.txt` (`:384`) írásra kerül, de **a repóban senki nem olvassa** (nincs önjavítás/észlelés).
- **M-21 — Nincs HTTP timeout és stall-detektálás a letöltésen.** `TurulMC.Updater\Program.cs:112`: `Timeout = Timeout.InfiniteTimeSpan` + `ResponseHeadersRead` + idle-határidő nélküli olvasóciklus → egy „csatlakozik, majd hallgat” szerver örökre befagyasztja a frissítőt; a resume csak a `.part` hosszát hiszi el.
- **M-22 — A CLI felülírja a közös `instances.json`-t parse-hiba után.** `TurulMC.Cli\Program.cs:21-66`: `catch { return ("", new()); }` (`:40-43`) + `File.Delete` üres listánál (`:48-53`) + `File.WriteAllText` (`:61-65`) — a GUI ugyanezt `.tmp`+`.bak`-kal, figyelmeztetéssel teszi (`MainWindow.xaml.cs:1866-1884, 1985-2015`). Egy korrupt fájl + `clean` csendben **törli a listát**.
- **M-23 — A `server.connect` más id-sanitizálást használ, mint a `play`.** `TurulMC.Cli\Program.cs:746-751` vs. `:305-312`: a `play` elutasítja az üres eredményt, a `server.connect` nem → `---` id esetén `Path.Combine(appData,"instances","")`, azaz **a közös instances gyökér** kapja a telepítést.
- **M-24 — A `CreateLocalProfileAsync` gyengébb usernév-szabályt enged, mint a rename.** `Infrastructure\Authentication\LocalAuthenticationService.cs:26-51` vs. `:72-73`: létrehozásnál csak hossz-ellenőrzés van, átnevezésnél `[A-Za-z0-9_]`; a profil offline identitás (`AccessToken = "0"`, determinisztikus MD5-UUID, `:47-48, 144-151`), és a launcher nem jelzi, hogy nincs authentikáció. Javítás: egységes `^[A-Za-z0-9_]{3,16}$` + explicit offline jelölés.
- **M-25 — A settings-betöltési hiba „default”-t ad, amit a hívó visszament.** `Infrastructure\FileSystem\SettingsStorage.cs:21-41` (fogyasztó: `TurulMC.Cli\Program.cs:324-344`): a megjegyzés szerint backupot kellene próbálni, de a `catch` üres; egy pillanatnyi IO-hiba után a CLI három mezőt felülírva **véglegesen** elveszejti a `javaPathOverride`, `theme`, `language`, `updateChannel`, `closeBehavior`, `uiScalePercent`, `firstRunCompleted` értékeket. A mentés maga jó (egyedi temp + atomikus move, `:59-82`).

**Build / kiadás / folyamat (saját megállapítások)**

- **M-26 — A `TurulMC.sln` nem tartalmazza a `TurulMC.Updater` és a `TurulMC.Recovery.SmokeTests` projektet.** `TurulMC.sln:6-19`: a solution 5 projektet sorol fel, a frissítőt nem. Következmény: `dotnet build TurulMC.sln` (és bármely CI/IDE build) **nem fordítja a biztonságkritikus komponenst**, az `update.cmd:40` „teljes” buildje sem; a `publish.cmd` külön, kézzel karbantartott útvonalon építi (`:28`).
- **M-27 — Az installer `PrivilegesRequired=admin`, miközben per-user könyvtárba telepít; a dokumentáció ezt másképp állítja.** `installer\TurulLauncher.iss:16, 19`: `DefaultDirName={localappdata}\Programs\TurulLauncher` + `PrivilegesRequired=admin`; a `FINAL_RELEASE_CHECKLIST.txt:20-24` („Setup telepul admin nelkul”) és a `docs\INSTALLER_DEPLOY.md:40` („Admin jog nem szükséges”) egyaránt ellentmond. Emelésre valójában csak a VC++ redist és a HKLM64 próba miatt van szükség (`.iss:58, 62-67`). Emelt telepítésnél over-the-shoulder UAC esetén a `{localappdata}` a **magasabb jogú fiók** profiljára oldódhat fel, így a normál felhasználó nem a saját profiljában találja a launchert — miközben az updater emelés nélkül, ugyanabba a könyvtárba ír (`InstallPackageAsync`), ezért a ZIP-alapú önfrissítés is elakadhat. Javítás: `PrivilegesRequired=lowest`, a VC++ runtime csak hiány esetén, saját emeléssel (`runasoriginaluser`), vagy valódi per-machine telepítés; a dokumentumok javítása.
- **M-28 — A `publish\GUI`-ban maradhat a helyi WebView2 felhasználói profil, és bekerülhet a telepítőbe/ZIP-be.** A profil (`TurulMC.Launcher.exe.WebView2`, **285 fájl, 36,9 MB**, `EBWebView\Default\…` cache/session adatok) a `13:16`-os `publish\GUI\TurulMC.Launcher.exe` futtatásakor keletkezett; a `build-installer.cmd:44-45` csak a `publish\Installer`-t tisztítja, az `.iss:50` viszont `..\publish\GUI\*`-ot csomagol, a `publish.cmd:53/62` pedig a `publish\GUI\*`-ból zip-el. A `Validate-Release.ps1:28-37` egyedül a PDB-ket tiltja. Jelenlegi artefaktok tiszták (a ZIP 02:30-kor, a telepítő 02:31-kor készült, a profil 13:16-kor), de a következő `build-installer.cmd` már szennyezett lehet. Javítás: explicit `userDataFolder` a `%LOCALAPPDATA%` alatt (lásd L-21), `*.WebView2` kizárása a csomagolásból, és `EBWebView` tiltása a validátorban.
- **M-29 — Nincs verziókezelés és nincs `.gitignore`; a fában 1,59 GB artefaktum és bináris van.** `git status` → „not a git repository”; `.gitignore` nincs. Terület: `bin\` 380 MB / 1170 fájl, `obj\` 26 MB, `publish\` 1160 MB (ebből ZIP 134,7 MB, Setup 111 MB kétszer, updater 110,9 MB háromszor), `prereqs\vc_redist.x64.exe` 18,7 MB, `tools\bin\` (aria2c.exe 5,6 MB + cache), `dependency-reduced-pom.xml` a gyökérben. Következmény: nincs visszagörgethető történet, nincs kód-review, a „mi van élesben” kérdés nem megválaszolható, és a felesleges binárisok minden másolatban ott vannak. Nincs `.github`/CI sem, tehát a `tools\Validate-Release.ps1` és a tesztkészlet futtatása **kézzel, kihagyhatóan** történik. A release GitHub-névtér egy személyes fiók literálja (`AverageGithubUser636/TurulLauncher-Releases`), öt helyen megismételve (`prepare-free-hosting.cmd:8`, `upload-github-release.cmd:8`, `tools\Prepare-FreeHosting.ps1:2, 8`, `tools\Publish-GitHubRelease.ps1:2`). Javítás: verziókezelés + `.gitignore` (a `publish\`, `bin\`, `obj\`, `prereqs\`, `tools\bin\` kizárásával), CI, ami a validátort és a teszteket futtatja, és a release-névtér egy helyen definiálva.
- **M-30 — A build-eszközök letöltése hash-ellenőrzés nélkül történik.** `tools\Ensure-BuildTools.ps1:69-114`: az `aria2c` (GitHub release) és a 7-Zip (`7zr.exe`, `7z2409-extra.7z`) letöltése után **nincs** checksum/ Authenticode-ellenőrzés, a kicsomagolt exe pedig a build során lefut. Pozitív ellenpont: a `tools\Get-VCRedist.ps1:19-43` helyesen követeli meg a Microsoft aláírást (ellenőriztem: `Valid`, `CN=Microsoft Corporation`). Javítás: rögzített SHA-256 a letöltött archívumokhoz.
- **M-31 — A beta csatorna működésképtelen.** `server\update\beta.json:2-5`: `version: 4.4.5`, `sha256: 000…0` — a `Regex.IsMatch(…, "^[a-fA-F0-9]{64}$")` (`MainWindow.xaml.cs:1675`) ezt **átengedi**, így a 4.4.4-es beta felhasználó letöltene egy 4.4.5-ös ZIP-et, ami garantáltan megbukik a hash-ellenőrzésen, a 4.4.5+ felhasználó pedig soha nem kap ajánlatot. A `beta.json`-t egyetlen script sem generálja/feltölti (`publish.cmd:64` és `tools\Prepare-FreeHosting.ps1:27` csak a stable csatornát kezeli), miközben a `docs\UPDATE_DEPLOY.md:5-6` beta URL-t hirdet. Javítás: beta.json generálása a publish folyamatban, vagy a beta csatorna eltávolítása a launcherből és a doksikból.
- **M-32 — A manifest-generátor minden jövőbeli verzióhoz a 4.4.9 changelogját írja.** `tools\Make-UpdateManifest.ps1:24-30`: az öt Base64-elt magyar szöveg akkor kerül a manifestbe, ha a verzió eltér a meglévőtől (`:34-35` csak egyező verziónál tartja meg a régit) — így egy `-Version 4.5.0` kiadás **4.4.9-es kiadási jegyekkel** megy ki a játékosokhoz. A `docs\UPDATE_DEPLOY.md:15` ezt „Írd át a changelog tömböt” módon dokumentálja, azaz a helyes tájékoztatás egy Base64 blokk kézi szerkesztésén múlik. Jelen állapot: a `server\update\stable.json:7-13` a 4.4.2-es ablak-viselkedés jegyeket tartalmazza 4.4.9 néven, miközben a valódi 4.4.9 tartalom a betöltőképernyő (`DISCORD_CHANGELOG_4.4.9.txt:3-10`). Javítás: a changelog verziózott fájlból (pl. `DISCORD_CHANGELOG_<verzió>.txt`) olvasása, hiba, ha hiányzik vagy más verzió szövegét tartalmazza.
- **M-33 — A `docs\architecture.md` más rendszert ír le, mint ami van.** `docs\architecture.md:5, 29-33, 55-61`: „WinUI 3 / WindowsAppSDK, unpackaged” szerepel, de „Views/ AXAML views”, „ViewModels (CommunityToolkit.Mvvm)”, „Controls/”, „Resources/” mappák és „MVVM” — ilyen réteg **nincs** a repóban (a valóság: WinUI 3 + egyetlen beágyazott HTML + WebView2 bridge); a dokumentum a Core/Infrastructure leírást is pontatlanul adja vissza. Egy ilyen doc aktív félrevezetés új fejlesztő számára.

**Telepítő, publish-folyamat, Java legacy**

- **M-34 — A Java launcher halott, párhuzamos implementáció.** `TurulMC.sln:6-19` nem tartalmazza, egyetlen `.cmd`/`.ps1` sem hivatkozik rá (se `mvn`, se `pom.xml`, se `javac`/`jar`), `target\` és `.jar` nincs, a ZIP/telepítő csak a `publish\GUI`-t szállítja — a `PATCH_4.4.6_AUDIT_FIX.txt:29-31` viszont „Java legacy (src/main)” javításokat dokumentál, azaz audit- és fejlesztési energia megy olyan kódra, amit egyetlen felhasználó sem tud elindítani. Javítás: `pom.xml`, `dependency-reduced-pom.xml` és `src\main\java` törlése, vagy áthelyezése egy egyértelműen nem szállított `legacy\` mappába.
- **M-35 — A Java launcher hidegen soha nem tud elindulni.** `src\main\java\…\util\MinecraftProcess.java:61-96`: a `VersionManager.ensureDownloaded()` csak a kliens jart, opcionális Forge jart és egy üres `natives` mappát hoz létre (`VersionManager.java:95-96`), a `libraries`/`assets` fa sosem töltődik le, a classpath viszont a `~/.minecraft/libraries` és a verziókönyvtár beolvasásával épül (`MinecraftProcess.java:129-156`), `-Djava.library.path` pedig nincs; a `--assetIndex 1.8`, `--mods`, `net.minecraft.launchwrapper.Launch`, `--tweakClass …FMLTweaker` beégetett (`:64, 75, 92-96`), így az ugyanabban a GUI-ban kínált „TurulNetwork (1.26.2) / Fabric” profil (`LauncherProfiles.java:80-86`) elvileg sem működhet.
- **M-36 — Az emelt jogú telepítő aláírás-ellenőrzés nélkül futtat egy repóban tárolt binárist egy felhasználó által írható temp könyvtárból.** `installer\TurulLauncher.iss:51-58`: `Source: "..\prereqs\vc_redist.x64.exe"; DestDir: "{tmp}"`, majd `[Run] Filename: "{tmp}\vc_redist.x64.exe" … /install /quiet` **admin joggal**. Az egyetlen aláírás-ellenőrzés a build idején fut (`tools\Get-VCRedist.ps1:39-43` — a jelenlegi fájl valóban `Valid`, Microsoft-aláíróval, ezt ellenőriztem), a telepítés pillanatában viszont semmi; egy felhasználói folyamat a kicsomagolás és a futtatás között kicserélheti a fájlt, és emeléshez jut. Javítás: Authenticode-ellenőrzés a `[Code]` szakaszban a futtatás előtt, vagy `Flags: runasoriginaluser` (a redist saját magát emeli).
- **M-37 — Nincs `AppMutex`, és a single-instance hívás nem kivétel-biztos.** `installer\TurulLauncher.iss:31-32`: `CloseApplications=yes` + `RestartApplications=no` és **`AppMutex` nélkül** → a telepítő/eltávolító szó nélkül bezárja a futó launchert, pedig a launcher készít stabil mutexet (`Global\TurulMC.Launcher.4x.SingleInstance`, `SingleInstance.cs:10-11`), csak a telepítő nem hivatkozik rá. Ráadásul a `SingleInstance.TryAcquire()` a `new Mutex(true, MutexName, out _)` hívást a `try`-on **kívül** végzi (`SingleInstance.cs:18-29`), és az `App.OnLaunched` a saját `try` blokkja előtt hívja (`App.xaml.cs:71-75`), így egy `UnauthorizedAccessException` (foglalt/letiltott globális objektum) egy `async void` kezelőből kifelé szállva **összeomlasztja az indítást** ahelyett, hogy „induljunk el akkor is” lenne. Javítás: `AppMutex=…` a .iss-ben + a mutex-létrehozás `try`-ba csomagolása.
- **M-38 — A verzió öt-hat független helyen él, és a validáció nem hasonlítja a binárist a kiadási verzióhoz.** `4.4.9` beégetve: `publish.cmd:19,43,53,62,64,83`, `build-installer.cmd:50-55`, `publish-all.cmd:10,15,20,22`, `prepare-free-hosting.cmd:10`, `tools\Validate-Release.ps1:2`, `tools\Prepare-FreeHosting.ps1:3`, `installer\TurulLauncher.iss:2`, miközben a `tools\Publish-GitHubRelease.ps1:19-29` a csproj `<Version>`-jéből dolgozik (`TurulMC.Launcher.csproj:13`); nincs `Directory.Build.props`, verziófájl vagy CI, ami összetartaná őket (a 4.4.7/4.4.8/4.4.9 changelogok mind külön kiírják: „Verziók X-re szinkronizálva”). A `Validate-Release.ps1:15-26` csak létezést, JSON-mezőket, hasht és PDB-mentességet ellenőriz, a ZIP-ben lévő exe **FileVersion-jét nem** — így egy eltérő csproj-verzióból készült build is átmegy a kapun, és rossz címke alatt megy ki. Javítás: verzió egy forrásból (`Directory.Build.props` + `/DMyAppVersion=` az ISCC-nek), és a validátorban `VersionInfo.FileVersion -like "$Version.*"` ellenőrzés.
- **M-39 — A `publish.cmd` figyelmen kívül hagyja a „publish törlése” és a build-eszköz lépések hibáit.** `publish.cmd:21-25`: `if exist publish rmdir /s /q publish` és a két `Ensure-BuildTools` hívás **nem** ellenőrzi az `errorlevel`-t és nincs `|| goto :fail`; ha a törlés részleges (pl. a futó launcher fájljai lockolják a `publish\GUI`-t), a `dotnet publish -o publish\GUI` a maradékba olvaszt, és a ZIP régi+új fájlok keverékét kapja — amit a `Validate-Release.ps1` igazolni fog, mert csak a manifest és a ZIP hash-ének egyezését nézi. Javítás: `errorlevel`-ellenőrzés a törlés/ eszközlépések után, vagy publish friss, időbélyeges könyvtárba.
- **M-40 — A CLI elkészül, de nem kerül terjesztésre, miközben a dokumentáció futtatásra biztatja a felhasználót.** `publish.cmd:42` legyártja a `publish\CLI\TurulLauncher.CLI.exe`-t és a `:72` ki is írja deliverable-ként, a ZIP viszont kizárólag a `publish\GUI`-ból készül (ellenőrizve: nincs CLI entry a ZIP 620 fájlja között), a telepítő is csak a `publish\GUI`-t csomagolja (`.iss:50`), a `tools\Prepare-FreeHosting.ps1:28-29` csak a ZIP-et és a Setupot másolja, a `Validate-Release.ps1` pedig a CLI-t nem is ismeri. Közben a `docs\setup.md:86-96` konkrét felhasználói parancsokat dokumentál (`TurulLauncher.CLI.exe manifest-hash …`). Javítás: a CLI bevétele a ZIP-be/telepítőbe és a release assetek közé, vagy egyértelmű „csak fejlesztői” jelölés a doksikban.
- **M-41 — Az önfrissítés soha nem törli azokat a fájlokat, amelyeket az új verzió elhagyott.** `TurulMC.Updater\Program.cs:486-523`: az `InstallPackageAsync` csak végigmásolja/ felülírja a payload fájljait, „a telepített fájlkészlet” nyilvántartása nincs, törlés csak rollback esetén történik; mivel a frissítés egy 620 fájlos teljes payloadot másol a telepítési könyvtárba, az elavult natív/runtime DLL-ek és átnevezett UI-assetek **örökre felhalmozódnak**. Javítás: telepített fájllista (`update-manifest.json`) és a hiányzó fájlok törlése, ugyanazzal a backup/rollback védelemmel (később delta payload alapja is lehet).
- **M-42 — A Java GUI jelszót kér, amit csendben eldob, a tokent pedig nyílt szövegben cache-eli.** `src\main\java\…\auth\AuthManager.java:29-31`: az `authenticate(String, char[])` **figyelmen kívül hagyja** a jelszót és offline tokent ad (`"offline:" + username`, `:33-37`), miközben a felület „Password (blank=offline)” címkét ír (`TurulLauncher.java:111-113`), így a felhasználó azt hiszi, bejelentkezett; a `passwordField.getPassword()` eredményét (`TurulLauncher.java:219`) senki nem nullázza; a `cacheCredentials()` pedig `{"email":…,"token":…}` tartalmat ír a `~/.turulmc/credentials.json`-ba alapértelmezett ACL-lel (`:106-111`). Javítás: a mező eltávolítása (vagy valódi auth), a `char[]` azonnali nullázása, a cache fájl jogosultságának szűkítése.

### 5.3 ALACSONY (LOW)

**Core**

| ID | Megállapítás | Hely |
|----|--------------|------|
| L-01 | `GetAwaiter().GetResult()` sync-over-async a UI-útvonalon (ma ártalmatlan, holnap deadlock) | `MinecraftLauncherService.cs:517` |
| L-02 | A teljes java parancssor (benne a session token) logolása; a maszkolás regex-függő, idézőjellel megkerülhető | `MinecraftLauncherService.cs:157`, `Logging\LauncherLogger.cs:87-89` |
| L-03 | A JVM-denylist nem fedi a manifestből érkező jvm-argumentumokat; `AdditionalJvmArgs` soha nem töltött (halott ág) | `MinecraftLauncherService.cs:550-567`, `Validation\LauncherSettingsValidator.cs:11-16` |
| L-04 | Távoli metadata-ból épített `Regex` timeout nélkül fut | `MinecraftLauncherService.cs:644-647` |
| L-05 | Session-tracker versenyek cancellation esetén (a grace-delay megszakad, a session bejegyzés bennmarad) | `MinecraftLauncherService.cs:202-231` |
| L-06 | Progress-számlálók szinkronizálatlanok 8-16 párhuzamos feladat között | `MinecraftInstallationService.cs:119-131` |
| L-07 | A logger soronként nyit/zár fájlt, és hibasoronként külön error-fájlt ír | `Logging\LauncherLogger.cs:41-80` |
| L-08 | Fabric loader verzió escapeletlen az URL-ben és könyvtárnévben; a profil JSON string-surgery-vel javított; nem dispose-olt `JsonDocument` | `FabricLoaderService.cs:41-67, 134-151` |
| L-09 | `LocalServerService`: installonkénti `HttpClient`, `online-mode=false` alapértelmezés figyelmeztetés nélkül, kilépett `Process` nem dispose-olt | `Servers\LocalServerService.cs:41-46, 72, 118-120` |
| L-10 | Halott privát helperek + elválasztó nélküli prefix-összehasonlítás | `Modpacks\ModpackService.cs:217-238` |
| L-11 | Használaton kívüli publikus API és modellek (`SafeDeleteDirectory`, `GetBytesAsync`, `GetFabricJarPathAsync`, `Sha1`, `SystemProperties`, `AdditionalJvmArgs`) | `PathSecurity.cs:51-66`, `Http\LauncherHttpClient.cs:29-49`, `FabricLoaderService.cs:161-166` |
| L-12 | Beégetett windowsos elérési utak, `26.1.2` default verzió három helyen, `RequiredJavaMajor = 25`, „26.” prefix-heurisztika, eltérő user-agentek (`2.0`, `4.4.9`, `0.10`), `-Dminecraft.launcher.version=0.1` | `Java\JavaRuntimeService.cs:28-46`, `Models\LauncherSettings.cs:8`, `MinecraftLauncherService.cs:480, 503, 563` |
| L-13 | A SRV-válasz tranzakció-ID és forrás ellenőrzése nélkül kerül feldolgozásra (a `UdpClient` nincs `Connect`-elve) | `Networking\ServerStatusService.cs:365-412` |
| L-14 | SHA-1 összehasonlítás case-sensitive (minden más hash case-insensitive) — halott metódusban | `MinecraftInstallationService.cs:180` |
| L-15 | A tesztkészlet nagyrészt tautologikus (`ComputeManifestHash(x) == ComputeManifestHash(x)`), a csomagverziók floatolnak (`17.*`, `2.*`) | `AuditFixTests.cs:55-59, 93-108`, `tests\TurulMC.Core.Tests.csproj:12-14` |

**Launcher (GUI)**

| ID | Megállapítás | Hely |
|----|--------------|------|
| L-16 | A hibajelentő `catch` maga is megdöntheti a folyamatot (árva `PostWebMessageAsJson`, `async void`) | `MainWindow.xaml.cs:465-488` |
| L-17 | „Newest-wins” verseny a recovery dialógusban (`dialog` null lehet a generáció-váltás után) | `ui\recovery.js:74-93` |
| L-18 | `SingleInstance.Release()` soha nem hívott; a `WakeLoop` nem megszakítható, a szál a kilépésnél bent ragad | `SingleInstance.cs:12-13, 25, 67-71` |
| L-19 | A honosítás megáll a HTML-nél: tálca-menü, ablakcím, `GameLoadingWindow` szövegei fix magyarok | `MainWindow.Tray.cs:237, 298-300`, `GameLoadingWindow.xaml:59-63`, `MainWindow.xaml:5, 28, 54` |
| L-20 | Négy párhuzamos honosítási mechanizmus; a szótár pontos, teljes string-egyezést vár, a dinamikusan épített szövegek kimaradnak (pl. „Smart Repair kész: ”) | `ui\index.html:5532-5578, 5012`, `ui\recovery.js:16-25` |
| L-21 | WebView2 keményítési hiányok: `AreDefaultScriptDialogsEnabled` nincs beállítva (alapból `true`), DevTools csak nem-DEBUG buildben tiltott, nincs explicit `userDataFolder` (ezért írja a profilt az exe mellé — lásd M-28) | `MainWindow.xaml.cs:293-317` |
| L-22 | `environment.info` kiadja a Windows-fiók- és gépnevet a lapnak (a `machineName`-t semmi nem használja) | `MainWindow.xaml.cs:377-383` |
| L-23 | Verzió-duplikációk: `ResolveAppVersion` fallback „4.4.9”, a crash-log beégetett „Version: 4.4.9”, öt HTML-placeholder, elavult Modrinth user-agent (`/0.10`) | `App.xaml.cs:30, 159`, `ui\index.html:1488-1570`, `MainWindow.xaml.cs:503, 1614` |
| L-24 | Halott natív title-bar: `Height="0"` + `Collapsed` sor, hívó nélküli gombok, üres handler, önmagának ellentmondó komment, `SetTitleBar(null)` | `MainWindow.xaml:9, 14-16`, `MainWindow.xaml.cs:42-49, 279, 3200-3204` |

**Updater / CLI / Infrastructure**

| ID | Megállapítás | Hely |
|----|--------------|------|
| L-25 | A ZIP entry-nevek nincsenek validálva (ADS `x.txt:payload`, `NUL`/`CON` eszköznevek átcsúsznak) | `TurulMC.Updater\Program.cs:721-733` |
| L-26 | Az install-fában lévő reparse point-okat az írás és a rollback is követi (nincs junction-védelem, szemben a Core-ral) | `TurulMC.Updater\Program.cs:507-518, 755-767` |
| L-27 | A CLI nem kezeli a `--`-t, és a dokumentált `--server`/`--port` flagek nem léteznek (`server.connect --server host` a `--server` stringet tekinti hosztnak) | `TurulMC.Cli\Program.cs:383, 733-764, 106` |
| L-28 | Help/implementáció drift: `serve` nincs dokumentálva, halott `GetDiffAsync` hívás, `create`/`set-version` nem támogatja a több szavas nevet | `TurulMC.Cli\Program.cs:775-801, 490, 551` |
| L-29 | Destruktív CLI-parancsok megerősítés és backup nélkül írják a GUI élő konfigurációját; a `delete` nem takarítja az instance mappát | `TurulMC.Cli\Program.cs:610-648` |
| L-30 | Halott, elcsúszott duplikált infrastruktúra-osztályok (HTTP, Security, Logging) — a HTTP-változat ráadásul méretkorlát nélkül bufferel | `Infrastructure\Http\LauncherHttpClient.cs`, `…\Security\Sha256Service.cs`, `…\Logging\LauncherLogger.cs` |
| L-31 | Sikeres frissítés után a ZIP és az updater egyedi másolata korlátlanul a lemezen marad (több száz MB verzióként) | `TurulMC.Updater\Program.cs:361-366, 384`, `MainWindow.xaml.cs:1724-1725` |

**Folyamat / dokumentáció / higiénia**

| ID | Megállapítás | Hely |
|----|--------------|------|
| L-32 | A `FINAL_RELEASE_CHECKLIST.txt` 4.3.4-es; olyan `publish\WebDeploy` mappát említ, amit egyetlen script sem hoz létre (ellenőrizve: nem létezik; a `tools\Prepare-FreeHosting.ps1:20-29` `publish\WebPatch`-et és `publish\GitHubRelease`-et gyárt), és „admin nélkül települ” állítást tartalmaz, amit a `PrivilegesRequired=admin` cáfol | `FINAL_RELEASE_CHECKLIST.txt:1-31`, `installer\TurulLauncher.iss:19` |
| L-33 | 56 gyökér `*.txt` (PATCH_/TEST_/DISCORD_CHANGELOG_), `dependency-reduced-pom.xml` a gyökérben, és **nincs** `README.md`, `LICENSE`, `.editorconfig`, `Directory.Build.props`, `global.json`, CI-workflow | repo gyökér |
| L-34 | A `preview-updater.cmd`/`update.cmd`/`test-recovery.cmd` a `publish\`-ot és a `bin\obj`-t részben takarítja (az Updater/Cli `bin\obj` nem), így elavult binárisok maradhatnak | `update.cmd:22-33` |
| L-35 | A `publish.cmd` `.publish.lock`-ja összeomlás után kézzel törlendő (nincs automatikus feloldás, nincs PID-ellenőrzés) | `publish.cmd:6-17` |

**Java legacy / eszközök**

| ID | Megállapítás | Hely |
|----|--------------|------|
| L-36 | A Java launcher a hivatalos launcher `.minecraft` könyvtárába ír (mods), és **minden** ott talált jart a classpathra tesz; a saját profilfájlt szerencsére külön tartja (`~/.turulmc/profiles.json`), a `launcher_profiles.json`-t nem írja | `MinecraftProcess.java:15-18, 106-113, 145-153` |
| L-37 | A Java argumentumok több ponton hibásak (`--uuid` = token, `--username` = e-mail, `--userType mojang` offline sessionhöz, beégetett 1.8 asset index), és nincs folyamat-követés: a `pb.start()` `Process`-ét eldobja, a GUI azonnal `System.exit(0)`-t hív, így playtime/exit code/log/stop nincs | `MinecraftProcess.java:66-83, 103`, `TurulLauncher.java:244, 265-268` |
| L-38 | Dokumentum- és felületi verzió-drift: a telepítői doksi 4.3.4-et ír, a `CHANGELOG_4.3.1.md` „4.3.3”, a `CHANGELOG_4.3.9.md` „4.4.0”, a beágyazott UI még „TurulLauncher 4.4.5 — Texture pack + Modpack support” címkét tartalmaz, az updater preview „4.4.5 PREVIEW”, a `docs\setup.md:35` pedig Fabric 0.19.3 alapértelmezést állít, miközben a kód `Loader = "none"`, `LoaderVersion = ""` | `docs\INSTALLER_DEPLOY.md:20-21,29`, `docs\CHANGELOG_*.md`, `ui\index.html:5234`, `TurulMC.Updater\Program.cs:98`, `LauncherSettings.cs:11-14` |
| L-39 | Maven-konfiguráció: `maven.compiler.source/target=8` `release` és rögzített compiler-plugin nélkül (JDK 25-tel fordítva a 8-as API-n túli hívások is átcsúszhatnak), a Guava 32.1.3-jre sehol nincs importálva (feleslegesen shaded), a generált `dependency-reduced-pom.xml` pedig a repóban van | `pom.xml:15-33` |
| L-40 | A build-eszköz átadó fájl ASCII-ként íródik, így a nem-ASCII (pl. magyar) útvonalak elromlanak, és a 7-Zip/aria2 csendben a lassabb tartalék útra esik vissza | `tools\Ensure-BuildTools.ps1:117-121`, `publish.cmd:47` |

### 5.4 INFO

- **I-01** — A SHA-256 összehasonlítás nem fixed-time (`TurulMC.Updater\Program.cs:371-372`), de mivel nyilvános tartalom-digestet hasonlít, ez **nem** sebezhetőség.
- **I-02** — A `CompareSemVer` (`MainWindow.xaml.cs:1624-1651`) numerikusan helyes a `4.4.10 > 4.4.9` esetre, és nem kínál downgrade-et; él-case: a nem parse-olható komponens 0-nak számít (lásd H-08), és a pre-release összehasonlítás `OrdinalIgnoreCase` string-compare (`4.4.9-beta.10 < 4.4.9-beta.9`).
- **I-03** — Az updater argumentum-parser megengedő (ismeretlen/duplikált flagek, nincs `--help`), a hibák MessageBoxban végződnek (`TurulMC.Updater\Program.cs:57-74`).
- **I-04** — A projektekben nincs `TreatWarningsAsErrors`, `AnalysisLevel`/`EnableNETAnalyzers`, `NuGetAudit`; nullable warningok csak tanácsadók (`TurulMC.Core.csproj:1-13`).
- **I-05** — Az `update-success.txt` írásra kerül, de a repóban nincs olvasója: nincs észlelés/önjavítás egy megszakadt frissítésre (`TurulMC.Updater\Program.cs:384`).
- **I-06** — A `server\update\stable.json` és a `publish\stable.json` UTF-8 BOM-mal kezdődik (ellenőrizve: `EF BB BF`), mert a `tools\Make-UpdateManifest.ps1:50` Windows PowerShell 5.1 alatt `Set-Content -Encoding UTF8`-at használ. A launcher `ReadAsStringAsync()`-kel olvas, ami a BOM-ot eltávolítja, ezért az auto-update érintetlen — egy nyers bájtokat olvasó harmadik fél (böngésző JS, Node) viszont megbotolhat benne. Javítás: BOM nélküli írás.

---

## 6. Javasolt javítási sorrend

**0–2 nap (biztonsági tűzoltó):**
1. H-04: `toast-msg` `textContent`-tel (1 soros javítás, azonnal zárja a legvalószínűbb RCE-utat).
2. H-05: `NavigationStarting` + `NewWindowRequested` tiltás nem `turul.local` célra, `e.Source` ellenőrzés az üzenetkezelőben, DevTools tiltása feltétel nélkül.
3. H-06: `openUrl` hoszt-allowlist + `NormalizeNewsUrl` http/https kényszer; a hírkártya `onclick` cseréje `addEventListener`-re.
4. H-08: `--version` szigorú validálása a launcherben **és** az updaterben; staging GUID könyvtárral.
5. H-09: a launcher kényszerített kilépése frissítés előtt + korlátos, identitás-ellenőrzött várakozás az updaterben.

**1–2 hét:**
6. H-07: manifest-aláírás (ECDSA/Ed25519) + ellenőrzés; a publikus kulcs a binárisban.
7. H-01: kötelező SHA-ellenőrzés a kliens/librária/native/asset és Fabric letöltéseknél.
8. H-10: Authenticode-aláírás a Setupra, a launcherre és az updaterre (+ a Setup SHA-256-jának publikálása).
9. M-28 + L-21: `userDataFolder` a `%LOCALAPPDATA%` alá, `*.WebView2`/`EBWebView` kizárása és tiltása a validátorban.
10. M-17, M-18, M-19, M-20, M-21, M-41: az updater trust-boundary és tranzakcionalitás megerősítése (host+path pin, argumentum-validálás, atomikus csere, backup megőrzés, timeout, elhagyott fájlok törlése).
11. M-37: `AppMutex` a telepítőben + a `TryAcquire` kivétel-biztosítása.
12. H-02, H-03, L-15: a hamis tesztfedettség megszüntetése (valódi belépési pontok tesztelése), és a frissítő/GUI-bridge első tesztjei.
13. M-26, M-38, M-39: az Updater és a SmokeTests felvétele a solutionbe, verzió egy forrásból, a publish lépések hibakezelése.
14. J-01, J-02 vagy M-34: a Java legacy azonnali eltávolítása (a legegyszerűbb és legbiztosabb javítás), vagy a hiányzó hash-/útvonal-védelem pótlása.

**1 hónap (fenntarthatóság):**
15. M-29: verziókezelés bevezetése (`.git` + `.gitignore`), a `bin/obj/publish/prereqs/tools\bin` kivétele; CI, ami a validátort és a teszteket futtatja; a release-névtér egy helyen.
16. M-12, M-13, L-11, L-30: duplikátumok és halott kód felszámolása (egy implementáció a Core-ban), a validátor bekötése.
17. M-01…M-11: a Core bemenet-validációs rétegének egységesítése (null-biztos JSON, URL-séma, koordináta-szegmensek, atomikus letöltés, junction-védelem a `PathSecurity`-ben).
18. M-27, M-36, M-22…M-25, M-31, M-32, M-40, M-42: telepítő-jogosultság és VC++ futtatás, egy közös instance-store, beta csatorna, changelog-generálás, CLI-terjesztés, a dokumentáció valósághoz igazítása (M-33, L-32, L-38).
19. A Java legacy (`src\main\java`, `pom.xml`, `dependency-reduced-pom.xml`) eltávolítása vagy tudatos, dokumentált különválasztása (M-34, M-35, L-36, L-37, L-39).

---

## 7. A `PATCH_4.4.6_AUDIT_FIX.txt` állításainak ellenőrzése

| Állítás | Eredmény |
|---|---|
| „ModpackService.GetManifestAsync csak https:// (DEBUG-ban file/localhost ok)” | ✅ igaz (`ModpackService.cs:40-53`) |
| „Minden modpack/szerver/Modrinth fájl kötelező hash-sel; hiány vagy eltérés = törlés + kivétel” | ✅ igaz a modpack/szerver/Modrinth útvonalon (`ModpackService.cs:59-63, 98-120`; `LocalServerService.cs:36-68`) — ⚠️ **de a Minecraft kliens/librária/native/asset letöltésekre nem terjed ki (H-01)** |
| „PathSecurity.ResolveInsideRoot minden Core-útvonalon; mrpack import zip-slip guarddal” | ✅ nagyrészt igaz (`PathSecurity.cs:10-35`, `ModpackPackager.cs:46-53`) — ⚠️ kivétel: `MinecraftInstallationService` maven-koordináták (M-03) és a `ModpackPackager` export entry-nevei (M-10) |
| „törlések symlink (ReparsePoint) ellenőrzéssel” | ⚠️ részben: csak a **levél**-elemre (`PathSecurity.cs:46-47, 62-63`); a `ModRecoveryService` járja az ősöket, a `PathSecurity` nem (M-04) |
| „LauncherSettingsValidator: javaPath csak létező java.exe/javaw.exe …; jvmArgs denylist” | ✅ igaz a két hívott metódusra (`JavaRuntimeService.cs:108`, `MinecraftLauncherService.cs:109`) — ⚠️ de a `Validate(LauncherSettings)` **soha nem fut** (M-13), a denylist pedig a manifest-jvm-argumentumokra nem terjed ki (L-03) |
| „Modrinth: cdn.modrinth.com pin, 512 MB fájlcap, max 25 függőség-mélység” | ✅ igaz (`MainWindow.xaml.cs:967-971`, `MainWindow.Packs.cs`) |
| „Logger token-maszkolás (Core + Infrastructure)” | ⚠️ részben: az **aktív** Core logger maszkol (`LauncherLogger.cs:87-89`), az Infrastructure-másolatból hiányzik az `auth_access_token` szabály (M-12) |
| „DevTools csak DEBUG-ban” | ✅ igaz (`MainWindow.xaml.cs:309/312`) — ⚠️ önmagában ez nem elég, mert a navigáció/origin nincs korlátozva (H-05) |
| „LocalAuthenticationService lockolva (SemaphoreSlim)” | ✅ igaz (`LocalAuthenticationService.cs:14, 28-60`) |
| „Java legacy: AuthManager offline-only, Mojang-auth nélkül; credential-fájl UTF-8; MinecraftProcess: cert-ignore flag kivéve, jvmArgs denylist” | ⚠️ **részben hamis**: az élő útvonal valóban offline (`AuthManager.java:29-37`), a credential-fájl UTF-8 (`:108, 115`), nincs trust-all TLS-felülírás, és a `jvmArgs` denylist megvan (`MinecraftProcess.java:48-59`) — **de** az `authenticateMojang()` + `validateToken()` + a `https://authserver.mojang.com` konstans **benne maradt** a fájlban (`:15, 39-67`), csak hívója nincs, tehát a „nincs Mojang-auth sehol” állítás nem igaz. A Java oldalon emellett **semmilyen** letöltés-integrity nincs (J-01), és a komponens nem része a buildnek (M-34) |
| „a release validáció elutasítja [a dev `server/manifest/manifest.json`-t]” | ❌ **hamis**: a `tools\Validate-Release.ps1` kizárólag a ZIP/manifest/Setup/PRI/ikon létezését, a `stable.json` verzióját/URL-jét/hash-ét és a PDB-mentességet ellenőrzi — a modpack-manifestet **semmilyen** script nem vizsgálja (`grep` a `tools\`-ban és a `*.cmd`-okban: nincs találat). A minta ráadásul futásidőben **soha nem szinkronizálható**: a `ModpackService.SyncModpackAsync` üres `Sha256`-nál dob (`ModpackService.cs:59-64`), és a `file://` engedmény ellenére a HTTPS-kényszer a DEBUG módot is érinti, így a `http://localhost:8080/...` URL-ek nem mehetnek át |
| „publish.cmd / publish-all.cmd és server/update JSON-ok most nem frissültek” | ✅ akkor igaz volt; ma már 4.4.9-re szinkronban vannak (a changelog tartalma viszont elavult, M-32) |

---

## 8. Ami bizonyítottan jól működik

1. **Nincs privilegizált COM-objektum a JavaScriptben** — se `AddHostObjectToScript`, se `ExecuteScriptAsync`, se `NavigateToString`, se `ObjectForScripting` a Launcher projektben; a híd kizárólag JSON-üzenet.
2. **A dinamikus HTML-építés túlnyomórészt escape-elt** — ~50 helyen `escapeHtml`, a `recovery.js` minden interpolált értéket `esc`-el; a H-04 egyetlen kivétel.
3. **A modpack-integritás valódi:** kötelező `sha256`/`sha512`, eltéréskor törlés + kivétel, `OrdinalIgnoreCase` összehasonlítás, manifest nem tudja kikapcsolni; a Modrinth hoszt pin-elt.
4. **A folyamatindítás biztonságos:** `UseShellExecute=false`, `CreateNoWindow`, `RedirectStandard*`, és minden argumentum `psi.ArgumentList`-en keresztül (nincs quoting/injection rés); a `javaPath` létező `java.exe`/`javaw.exe`-re van kötve.
5. **Az `.mrpack` import zip-slip védett**, a `PathSecurity.ResolveInsideRoot` a `:`-t és érvénytelen karaktereket is elutasítja, és a ModpackService minden írást/törlést ezen keresztül végez.
6. **A recovery alrendszer a projekt legerősebb kódja:** ősöket járó reparse-point tiltás, 4 GiB / 50 000 fájl cap, journal + `committed` marker automatikus rollbackkel, teljes SHA-256 + byte-count ellenőrzés a visszaállítás előtt (20/20 smoke teszt zöld).
7. **A titokmaszkolás valódi, a logok felhasználói profilban vannak** (`%APPDATA%\TurulMC\logs`), és a repó forrásában nincs beégetett hitelesítő adat, token vagy fejlesztői abszolút útvonal.
8. **Nincs jogosultság-emelés a telepítés után:** az `app.manifest` nem kér emelést, minden artefaktum per-user helyen van; a `LocalServerService` HTTPS-t, kötelező SHA-256-ot, 1 GiB capet és temp+move commitot használ.
9. **Több írás atomikus:** playtime, game-session, `servers.json`, `settings.json` (egyedi temp + `File.Move(..., true)`), a `ServerStatusService` varint- és név-olvasása korlátos.
10. **A ZIP-hash ellenőrzés fail-closed, és a self-replacement megoldás helyes:** az updater a `%LOCALAPPDATA%`-ból fut, megvárja a launcher PID-jét, a ZIP-et staging könyvtárba, zip-slip védelemmel bontja ki, és a restart a named-mutex single-instance őrön megy át.
11. **A kiadási hash-lánc konzisztens** (saját mérés): `publish\TurulLauncher-4.4.9.zip` SHA-256 = `503d2390225dc02bb22983deeb5edd26ab67fc2cdc2cb49657870b6e029bfa97` = `publish\stable.json` = `server\update\stable.json` = a `publish\GitHubRelease` példány; a generátor, a validátor, a launcher és az updater ugyanazt az algoritmust/ fájlt használja, és a ZIP a `TurulMC.Launcher.exe`-t a gyökérben tartalmazza, PDB nélkül (620 entry) — pontosan amit az updater `ResolvePayloadRoot`-ja megkövetel.
12. **A telepítő bemenetei egyeznek a publish kimenetével:** a `.iss` a `publish\GUI\*`-t csomagolja, abban ott a launcher, a `.pri`, az updater és az ikon; a Setup neve/verziója (4.4.9.0/4.4.9) egyezik a GUI exével; a `prereqs\vc_redist.x64.exe` valóban Microsoft-aláírt.
13. **Az eltávolítás nem viszi magával a felhasználói adatot:** a `.iss`-ben nincs `[UninstallDelete]`, `[UninstallRun]` vagy `[Registry]` szakasz, így a `%APPDATA%\TurulMC` (modpackek, világok, profilok) és a letöltött frissítéscsomagok megmaradnak.
14. **A PowerShell/CMD hibakezelés fegyelmezett** (két kivétellel, lásd M-39): minden `tools\*.ps1` `$ErrorActionPreference='Stop'`-ot állít, a natív exit code-okat ellenőrzi, a wrapperek `|| goto :fail` / `if errorlevel 1` mintát használnak.
15. **Nincs titok és nincs fejlesztői abszolút útvonal a scriptekben** (`C:\Users`, PAT-minták, Discord webhook, `api_key` keresésre csak a szándékos release-repo literál és a Java offline token találat); a Java forrásokban nincs trust-all TLS-felülírás.

---

## 9. Amit nem sikerült ellenőrizni

1. **A 62 unit teszt futtatása.** A `dotnet test` útja ebben a környezetben nem járható: a `testhost.exe` a sandboxban nem tudja megnyitni a szülőfolyamatát (`System.ComponentModel.Win32Exception (5): A hozzáférés megtagadva`), és a kezeletlen kivétel (`0xe0434352`) a felhasználó asztalán egy „Alkalmazáshiba” dialógust nyit — **ez a dialógus innen származott, ártalmatlan, a repót nem érinti**. A vizsgálat közben a gép .NET SDK-ja **frissült (10.0.400 → 10.0.401, 20:49:01)**, ezért tűnt el átmenetileg a `C:\Program Files\dotnet\sdk` könyvtár (innen a „No .NET SDKs were found” hiba), majd az új SDK első-indítási sentineljét (`C:\Users\-\.dotnet\10.0.401.toolpath.sentinel`) a sandbox szintén nem engedte kiírni. Ezért a `DOTNET_CLI_HOME`-ot a workspace-be irányítva, **in-process futtatóval** (testhost nélkül, a `[Fact]`/`[Theory]` metódusok reflexiós hívásával) hajtottam végre ugyanazokat a teszteket: **PASS=62, FAIL=0, SKIP=0**. A `dotnet build` és a 20 recovery smoke teszt szintén zöld. A repóban csak a jelentésfájl maradt; a mérőeszközt töröltem.
2. **Runtime viselkedés:** a GUI, a WebView2 híd, a frissítés és a játék indítása nem lett kipróbálva; a H-04/H-05/H-06 láncok és a H-09 holtpont kód alapján bizonyítottak, nem megfigyeltek.
3. **Szerveroldal / élő telepítés:** a `https://turulnetwork.hu/launcher/update/stable.json` és a release ZIP URL a mérésből nem volt elérhető (a kapcsolat mindkétszer lezárult), ezért nem igazolható, hogy amit a játékos letölt, megegyezik-e a helyi `503d23…fa97` artefaktummal, illetve hogy a `_redirects` GitHub Releases-re mutató átirányítása élesben működik-e.
4. **Az updater és a telepítő runtime viselkedése** (resume/Range, rollback, `update-success.txt`, újraindítás; telepítés futó launcher fölé, eltávolítás) — nem lett kipróbálva.
5. **A `TEST_4.4.5.txt`/`TEST_4.4.6.txt` checklista állításai** („59 sikeres xUnit teszt”, GUI bridge smoke tesztek) nem lettek végrehajtva, így azok nem igazoltak.
6. **CVE-státusz** csak verziószámokból következtetve (Gson 2.10.1, Guava 32.1.3-jre: nyitott tanácsadás nem azonosítható, de élő NVD/OSV lekérdezés nem történt).
7. **Effektív fájl-jogosultságok** (`icacls`) a `%APPDATA%\TurulMC\*.json` és `%LOCALAPPDATA%\TurulMC\updates` fájlokon; az AV/EDR hatása a frissítés közbeni cserére; a `update-success.txt` esetleges repón kívüli fogyasztója.
8. **A Java legacy tényleges működése** (nem része a buildnek, ezért nincs mód összeállítani/ futtatni a jelen build-láncban; `target\` és `.jar` nem is létezik).

---

## 10. Melléklet — mérési adatok

| Mérőszám | Érték |
|---|---|
| C# forrásfájlok / sorok | 66 fájl / ~15 000 sor (`MainWindow.xaml.cs` 3 080, `MainWindow.Packs.cs` 1 006, `Cli\Program.cs` 793, `Updater\Program.cs` 774, `MinecraftLauncherService.cs` 646) |
| Beágyazott web UI | `ui\index.html` 5 273 sor + `recovery.js` 146 + `recovery.css` 28 |
| Java legacy | 6 fájl, `pom.xml` (1.0.0, Java 8, Gson 2.10.1, Guava 32.1.3) — egyetlen script sem hivatkozik rá |
| Tesztek | 54 `[Fact]` + 3 `[Theory]` (8 `[InlineData]`) = **62 futtatható eset**, kizárólag a Core-ra; + 20 recovery smoke check. Eredmény: **62/62 és 20/20 zöld** |
| Repó méret | 1 586 MB / 2 447 fájl, ebből `publish\` 1 158 MB, `bin\` 380 MB, `obj\` 26 MB |
| Kiadási artefaktumok | ZIP 134,7 MB (620 entry, 337 MB kitömörítve), Setup 111 MB (×2), updater 110,9 MB (×3), CLI 70,4 MB |
| Verzióegyezés | 4.4.9 minden csproj/script/installer/manifest esetében; kivétel: `FINAL_RELEASE_CHECKLIST.txt` (4.3.4), `server\update\beta.json` (4.4.5) |
| Külső bináris a fában | `prereqs\vc_redist.x64.exe` 18,7 MB (aláírás ellenőrizve ✅), `tools\bin\aria2c.exe` 5,6 MB + cache |
| Aláírás-állapot | `publish\Installer\TurulLauncher-Setup-4.4.9.exe` → **NotSigned** ❌; `prereqs\vc_redist.x64.exe` → `Valid`, `CN=Microsoft Corporation` ✅ |
| Elérhető eszközlánc a gépen | .NET SDK 10.0.400 → **10.0.401 frissítés a mérés közben** (lásd 9.1), JDK 25 Temurin, Maven 3.9.16, Inno Setup 7 (`%LOCALAPPDATA%\Programs\Inno Setup 7\ISCC.exe`), GitHub CLI; 7-Zip és aria2c nincs a PATH-ban (a publish a portable letöltésre vagy `Compress-Archive`-ra esik vissza) |
| NuGet cache | `Microsoft.WindowsAppSDK 2.4.0` és `Microsoft.Web.WebView2 1.0.3719.77` jelen van (a build offline is lefutott) |

**A jelentés készítésének módja:** teljes forrásfa-olvasás, 5 párhuzamos rész-audit (Core; Launcher/WebView2; Updater/CLI/Infrastructure; Java/telepítő/scriptek; build- és higiénia-ellenőrzés), a legmagasabb súlyú megállapítások független visszaellenőrzése a forrásban, és a rendelkezésre álló dinamikus ellenőrzések (build, smoke teszt, aláírás-vizsgálat, ZIP-tartalom) lefuttatása.

---

## 11. Utólagos állapot — a 4.5.0 fejlesztések után (2026-10-03)

A felhasználó által jóváhagyott két fejlesztés (automatikus Java telepítés + Doctor panel) elkészült, és az alábbi audit-tételeket érintette:

| Audit-tétel | Állapot |
|---|---|
| **M-05** — a Java-verziópróba blokkolhat (a timeout elérhetetlen) | ✅ **javítva**: a `JavaRuntimeService.GetJavaVersionAsync` aszinkron, 8 s időkorláttal, időtúllépéskor `Kill(entireProcessTree)`, plusz 24 órás gyorsítótár és legfeljebb 12 próba scanenként |
| **M-12 / L-30** — duplikált logika (Core ↔ Infrastructure, illetve három kompatibilitási szabály) | ⚠️ részben: a Java-kompatibilitás egyetlen forrása a `JavaVersionMap.IsCompatible` (a Doctor és a `JavaRuntimeService` is ezt hívja); a Core/Infrastructure duplikátumok még állnak |
| **L-15** — a teszt csomagverziók floatolnak (`17.*`, `2.*`) | ✅ **javítva**: rögzített `Microsoft.NET.Test.Sdk 17.14.1`, `xunit 2.9.3`, `xunit.runner.visualstudio 2.8.2` — így a restore megismételhető (és offline is működik a helyi csomagcache-ből) |
| **M-16** — ad-hoc Support Bundle a GUI-ban, duplikált logika | ✅ **javítva**: egyetlen `SupportBundleService` a Core-ban, a GUI és a CLI ugyanazt a csomagot készíti, immár Doctor-jelentéssel |
| **M-26** — a solution nem tartalmazza a teljes terméket | ⚠️ részben: az új `TurulMC.Features.SmokeTests` bekerült a `TurulMC.sln`-be; a `TurulMC.Updater` és a `TurulMC.Recovery.SmokeTests` továbbra sem |
| **M-38** — a verzió 5-6 helyen él | ⚠️ tudatosan nem nyúltam hozzá: a 4.5.0 verzióemelés (csproj-ok, publish scriptek, `.iss`, `Make-UpdateManifest.ps1` changelog) külön kiadási lépés, lásd a fejlesztési összefoglalóban |
| **H-01** — nincs hash-ellenőrzés a Minecraft kliens/libráriák letöltésénél | ❌ nyitva (a Java letöltés viszont SHA-256-tal ellenőrzött) |
| **H-04…H-10, M-17…M-21** — WebView2 híd, update-lánc, aláírás | ❌ nyitva (a Doctor és a Java funkció ezeket nem érinti) |

**Új, a fejlesztéssel keletkezett felület, amit érdemes auditálni a következő körben:** `src\TurulMC.Core\Java\AdoptiumReleaseClient.cs`, `JavaRuntimeProvisioner.cs`, `JavaVersionMap.cs`, `src\TurulMC.Core\Diagnostics\*.cs` (17 ellenőrzés + support bundle), valamint a GUI `java.*`/`doctor.*` bridge-végpontjai (`MainWindow.Doctor.cs`). Ezekre 33 új, hálózat nélkül futó füstteszt készült (`tests\TurulMC.Features.SmokeTests`), és a teljes meglévő készlet (62 xunit + 20 recovery) zölden fut.

## 12. Utáni állapot — a 4.6.0-s mod-kezelési kör után (2026-10-03)

Ez a kör a felhasználói visszajelzésre készült: Smart Repair ellenőrizze a modok verzióit, az
Instance-szerkesztő támogasson több mindent, az Instance-másolás férjen el a képernyőn és jelezze a
verzióütközést (1.21.11 → 26.2), és legyen tesztelhető a 26.2-es szerver.

| Audit-tétel | Állapot |
|---|---|
| **M-13 / L-20** — a mod-kompatibilitás csak hálózattal, a Modrinth metaadatból volt ellenőrizhető | ✅ **javítva**: új, offline szkenner (`TurulMC.Core\Mods\*`) a JAR-ok `fabric.mod.json` leírása alapján (Minecraft-tartomány, Fabric Loader igény, duplikált mod id, nem Fabric mod, sérült JAR) |
| **M-14** — a Smart Repair nem vizsgálta a modokat | ✅ **javítva**: a Smart Repair 5. lépése a mod-szkenner, javítással (letiltás / karantén + leltár) és automatikus modmentéssel a javítás előtt |
| **M-32** — a `Make-UpdateManifest.ps1` még a 4.4.9-es changelogot írja be | ❌ nyitva (a 4.6.0-s changelog megvan, de a script még nem hivatkozik rá) |
| **L-05** — a verziólista négy fix értékre volt írva a GUI-ban (egy 26.2-es Instance szerkesztésénél csendben visszaállt volna a verzió) | ✅ **javítva**: a létrehozó és szerkesztő modál a Mojang manifest valódi listáját használja (kiadások + pillanatképek), a jelenlegi verzió mindig megmarad |
| **L-31** — a launcher/Instance szintű extra JVM argumentumok elvesztek, ha a Mojang metaadat tartalmazott JVM argumentumot | ✅ **javítva**: `BuildJvmArguments` minden esetben hozzáfűzi a felhasználói argumentumokat (duplikáció nélkül) |
| **H-01** — nincs hash-ellenőrzés a Minecraft kliens/libráriák letöltésénél | ❌ továbbra is nyitva (a mod- és Java-letöltés viszont ellenőrzött) |
| **H-04…H-10, M-17…M-21** — WebView2 híd, update-lánc, aláírás | ❌ nyitva |

**Új felület ebből a körből, amit a következő auditnak néznie kell:** `src\TurulMC.Core\Mods\*.cs`
(verziótartomány-egyeztetés, JAR-olvasás, szkenner, Modrinth-tervező, cél-Instance ütközésvizsgáló),
`src\TurulMC.Launcher\MainWindow.Mods.cs` (modfrissítés), `MainWindow.Repair.cs` (Smart Repair 2.0),
a `instances.copyPreview`/`instances.copyContent` új ága (`modStrategy`) és a GUI új
`mods.*`/`instance.repair` hívásai. Ezekre 10 új, hálózat nélkül futó füstteszt készült
(33 → **43** ellenőrzés), a meglévő készletek (62 xunit + 20 recovery) változatlanul zöldek.

**Független verifikáció (két kör: 135 + 142 saját ellenőrzés) és az abból javított hibák:**
az első kör egy HIGH és hat MEDIUM, a második négy további MEDIUM hibát talált — mindet javította a
fejlesztés; részletes táblázatok a `docs\CHANGELOG_4.6.0.md` „Független ellenőrzés” fejezetében.
A legfontosabb tanulságok: a terv és a telepítő feltételei nem térhetnek el (H1), a „nincs
visszalépés” védelmet a valós Modrinth verziószám-formátumokon is működtetni kell (D1), a telepítő
és a tervező ugyanazt az összehasonlítási szabályt használja (D2), ellenőrzés nem törölhet
felhasználói adatot (M2/D3), és a másolásnak a **cél** Instance állapotát is figyelnie kell (M3/D4).
A verifikáció azt is megerősítette, hogy a JAR-méretkorlátok valódiak, a `depends` tömbök `||`
összefűzése helyes, és hogy a Fabric `^0.0.3` → `0.0.9` egyezése a Fabric saját szabálya szerint is
helyes.

**Amit itt sem sikerült igazolni:** az élő Modrinth-letöltés, a Modrinth verziólista valós
válasza és az Adoptium-letöltés — ezek hálózatot igényelnek, ezért a felhasználói teszt a mérvadó.
Emellett a WebView2 felület csak statikusan (JS-szintaxis, bridge- és id-konzisztencia) ellenőrzött,
kattintós végigpróbálás nem történt.

