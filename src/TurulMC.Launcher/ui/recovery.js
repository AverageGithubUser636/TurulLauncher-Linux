/* 4.4.5: local crash explanations and explicit, instance-scoped mod recovery. */
(() => {
  "use strict";
  let dialog = null;
  let generation = 0;
  let restoring = false;
  let deferredCrash = null;
  const en = () => document.documentElement.lang === "en";
  const tr = (hu, english) => en() ? english : hu;
  const esc = value => escapeHtml(String(value ?? ""));
  const activeId = () => typeof activeInstanceId === "string" ? activeInstanceId : "";
  const request = (action, data) => sendMsg(action, JSON.stringify(data));
  const scopeNote = () => tr(
    "A modokat és modbeállításokat állítja vissza. A világmentések, képernyőképek, resource packek és shader packek megmaradnak. Ez nem teljes modpack-visszaállítás.",
    "Restores mods and mod configuration. Worlds, screenshots, resource packs and shader packs are kept. This is not a complete modpack rollback.");
  const reasonEn = {
    "Mod telepítése / frissítése előtt": "Before installing / updating a mod",
    "Mod törlése előtt": "Before removing a mod",
    "Mod be- vagy kikapcsolása előtt": "Before enabling / disabling a mod",
    "Modpack-verzió váltása előtt (modok és konfigurációk)": "Before changing modpack version (mods and configuration)",
    "Modpack szinkronizálása előtt (modok és konfigurációk)": "Before syncing a modpack (mods and configuration)",
    "Modok / konfigurációk másolása előtt": "Before copying mods / configuration",
    "Visszaállítás előtti állapot": "State before restoring"
  };
  const reason = value => en() ? (reasonEn[value] || value) : value;
  const date = value => new Date(value).toLocaleString(en() ? "en-GB" : "hu-HU");
  const size = bytes => Number(bytes) < 1024 * 1024 ? `${Math.round(Number(bytes) / 1024)} KiB` : `${(Number(bytes) / 1024 / 1024).toFixed(1)} MiB`;

  function close() {
    if (restoring) return;
    generation++;
    if (dialog) { dialog.close(); dialog.remove(); dialog = null; }
  }
  function show(title, subtitle, body, buttons = []) {
    if (restoring) return null;
    close();
    const token = generation;
    dialog = document.createElement("dialog");
    dialog.className = "recovery-dialog";
    dialog.setAttribute("aria-labelledby", "recoveryTitle");
    dialog.innerHTML = `<div class="recovery-head"><div><h2 id="recoveryTitle">${esc(title)}</h2><div class="recovery-sub">${esc(subtitle)}</div></div><button type="button" class="recovery-close" aria-label="${esc(tr("Bezárás", "Close"))}">×</button></div><div class="recovery-body">${body}</div><div class="recovery-footer"></div>`;
    dialog.querySelector(".recovery-close").addEventListener("click", close);
    dialog.addEventListener("cancel", event => { event.preventDefault(); close(); });
    for (const item of buttons) {
      const button = document.createElement("button");
      button.type = "button";
      button.textContent = item.label;
      if (item.primary) button.className = "recovery-primary";
      button.addEventListener("click", item.run);
      dialog.querySelector(".recovery-footer").appendChild(button);
    }
    document.body.appendChild(dialog);
    dialog.showModal();
    return token;
  }
  function valid(token) { return dialog && generation === token; }
  function errorBody(message) { return `<p class="recovery-warning" role="alert">${esc(message)}</p>`; }

  window.openModRecovery = async function(instanceId = activeId()) {
    if (restoring) return;
    if (!instanceId) { showToast("warning", tr("Előbb válassz egy Instance-t.", "Select an instance first.")); return; }
    const token = show(tr("Mod-visszaállítás", "Mod recovery"), tr("Automatikus mentések", "Automatic snapshots"), `<p>${esc(tr("Mentések betöltése…", "Loading snapshots…"))}</p>`, [{label: tr("Bezárás", "Close"), run: close}]);
    try {
      const data = await request("instance.recovery.list", {instanceId});
      if (!valid(token)) return;
      const points = Array.isArray(data?.snapshots) ? data.snapshots : [];
      dialog.querySelector(".recovery-sub").textContent = data.instanceName || instanceId;
      dialog.querySelector(".recovery-body").innerHTML = `<p class="recovery-note">${esc(scopeNote())}</p><p class="recovery-sub">${esc(tr("Instance-enként az utolsó 5 pont marad meg. A mentés a launcherben végzett modmódosítás előtt készül; nem jelent igazoltan működő összeállítást.", "The last 5 snapshots are kept per instance. A snapshot is taken before a mod change made in the launcher; it is not a verified working setup."))}</p><div class="recovery-list">${points.length ? points.map((point, index) => `<div class="recovery-row"><div><b>${esc(reason(point.reason))}</b><small>${esc(date(point.createdUtc))} · ${esc(size(point.bytes))}</small><small>Minecraft ${esc(point.minecraftVersion)} · ${esc(point.loader)}</small>${point.compatible === false ? `<small class="recovery-warning">${esc(tr("Előbb válts a mentés Minecraft-verziójára és loaderére.", "Select this Minecraft version and loader first."))}</small>` : ""}</div><button type="button" data-snapshot-index="${index}" ${point.compatible === false ? "disabled" : ""}>${esc(tr("Visszaállítás", "Restore"))}</button></div>`).join("") : `<p class="recovery-empty">${esc(tr("Még nincs automatikus modmentés. Az első a következő modtelepítés, törlés vagy kapcsolás előtt készül el.", "No automatic snapshot yet. The first one is created before the next mod install, removal or toggle."))}</p>`}</div>`;
      dialog.querySelectorAll("[data-snapshot-index]").forEach(button => button.addEventListener("click", () => confirmRestore(instanceId, data.instanceName, points[Number(button.dataset.snapshotIndex)])));
    } catch (error) {
      if (valid(token)) dialog.querySelector(".recovery-body").innerHTML = errorBody(error.message);
    }
  };

  function confirmRestore(instanceId, instanceName, snapshot) {
    const token = show(tr("Visszaállítod ezt a modmentést?", "Restore this mod snapshot?"), instanceName,
      `<p><b>${esc(reason(snapshot.reason))}</b><br>${esc(date(snapshot.createdUtc))}</p><p class="recovery-note" style="margin-top:16px">${esc(scopeNote())}</p><p>${esc(tr("A jelenlegi modokról és beállításaikról előbb új mentés készül. A Minecraft legyen bezárva.", "The current mods and configuration will be backed up first. Minecraft must be closed."))}</p><div class="recovery-status" role="status" aria-live="polite"></div>`,
      [{label: tr("Mégse", "Cancel"), run: () => window.openModRecovery(instanceId)}, {label: tr("Mentés és visszaállítás", "Back up and restore"), primary: true, run: async () => {
        if (restoring || !valid(token)) return;
        restoring = true;
        dialog.querySelectorAll("button").forEach(button => button.disabled = true);
        const status = dialog.querySelector(".recovery-status");
        status.textContent = tr("Ellenőrzés és visszaállítás folyamatban…", "Checking and restoring…");
        try {
          const result = await request("instance.recovery.restore", {instanceId, snapshotId: snapshot.id, confirmed: true});
          if (result?.success === false) throw new Error(result.error || tr("Sikertelen visszaállítás.", "Restore failed."));
          restoring = false;
          close();
          showToast("success", tr("A modok és beállításaik visszaállítva. A világmentések megmaradtak.", "Mods and configuration restored. Worlds were kept."));
          if (typeof reloadTurulState === "function") await reloadTurulState("mod-recovery").catch(() => {});
          await window.openModRecovery(instanceId);
        } catch (error) {
          restoring = false;
          if (valid(token)) {
            status.textContent = error.message;
            status.classList.add("recovery-warning");
            dialog.querySelectorAll("button").forEach(button => button.disabled = false);
          }
        } finally {
          restoring = false;
          if (deferredCrash) { const report = deferredCrash; deferredCrash = null; showCrash(report); }
        }
      }}]);
  }

  const englishCrashes = {
    memory: ["Minecraft ran out of memory", "Java reported a memory shortage. A large modpack or high graphics settings can cause this.", ["Close unnecessary applications.", "Check the instance RAM setting and leave memory for Windows.", "Try a shorter render distance or fewer mods."]],
    "memory-reservation": ["Java could not reserve memory", "The requested memory cannot be allocated. Increasing RAM further may make this worse.", ["Reduce the instance RAM allocation.", "Close other applications and try again."]],
    "java-version": ["Incompatible Java version", "Minecraft or a mod requires a different Java version.", ["Choose automatic Java detection in launch settings.", "Check the Java requirement shown in the log."]],
    "duplicate-mod": ["A mod is installed more than once", "The loader found duplicate mod files, often two versions of the same mod.", ["Disable the redundant version in Installed mods.", "If this followed an update, restore the previous mod snapshot."]],
    "mod-dependency": ["Missing or incompatible dependency", "The loader reported a missing dependency or dependency version. The log excerpt helps identify it.", ["Install the required dependency for this Minecraft version.", "If this followed an update, restore the previous mod snapshot."]],
    "mod-version": ["Incompatible mod versions", "The loader reported a version conflict between Minecraft, the loader or mods.", ["Check the instance Minecraft version and loader.", "Install a compatible version of the named mod or restore a snapshot."]],
    graphics: ["Graphics initialization failed", "Minecraft could not create its OpenGL window. GPU support or the graphics driver may be responsible.", ["Check whether the GPU supports this Minecraft version.", "Use the appropriate graphics driver from the GPU manufacturer.", "Try without shaders and graphics mods."]],
    "corrupt-file": ["Damaged JAR or ZIP file", "Java could not read an archive. An interrupted download can cause this.", ["Download the named mod again.", "For damaged Minecraft files, run Smart Repair."]],
    mixin: ["Possible mod conflict", "The log contains a Mixin error. This can indicate incompatible mods, but does not prove which mod caused it.", ["Try restoring a snapshot from before the last change.", "Check recently installed mods for compatibility.", "Create a Support ZIP if the problem continues."]],
    unknown: ["The cause could not be identified reliably", "The available log does not contain a recognized cause. An exit code alone is not a diagnosis.", ["If this started after a mod change, try the preceding snapshot.", "A Support ZIP contains the full logs for further investigation."]]
  };
  function showCrash(report) {
    if (restoring) { deferredCrash = report; return; }
    const translated = en() ? (englishCrashes[report.code] || englishCrashes.unknown) : null;
    const title = translated?.[0] || report.title;
    const summary = translated?.[1] || report.summary;
    const steps = translated?.[2] || report.steps || [];
    const subtitle = `${report.instanceName || report.instanceId} · ${date(report.createdUtc)}${report.exitCode == null ? "" : ` · exit ${report.exitCode}`}`;
    show(title, subtitle, `<p>${esc(summary)}</p><ol>${steps.map(step => `<li>${esc(step)}</li>`).join("")}</ol>${report.evidence ? `<details><summary>${esc((report.recognized === false ? tr("Rendelkezésre álló hibaüzenet", "Available error message") : tr("Felismert naplórészlet", "Recognized log excerpt")))}</summary><pre>${esc(report.evidence)}</pre></details>` : ""}<p class="recovery-sub" style="margin-top:18px">${esc(tr("Az elemzés helyben, a gépeden történik. A javítási lépések javaslatok; a launcher nem töröl automatikusan modokat.", "Analysis runs locally on your computer. These are suggested steps; the launcher does not automatically delete mods."))}</p>`,
      [{label: tr("Bezárás", "Close"), run: close}, {label: tr("Modmentések megnyitása", "Open mod snapshots"), primary: true, run: () => window.openModRecovery(report.instanceId)}]);
  }
  window.showLastCrash = async function() {
    try {
      const result = await request("instance.crash.last", {instanceId: activeId()});
      if (result?.found && result.report) showCrash(result.report);
      else showToast("info", tr("Ehhez az Instance-hez még nincs elmentett hibaelemzés.", "No saved crash explanation for this instance yet."));
    } catch (error) { showToast("error", error.message); }
  };
  const previousHandler = handleNetMessage;
  handleNetMessage = function(action, data) {
    if (action === "recovery.crash") { showCrash(data); return; }
    if (action === "recovery.progress") {
      if (restoring && dialog) dialog.querySelector(".recovery-status").textContent = tr(data.message, "Checking and restoring files…");
      else showToast("info", tr(data.message, "Creating an automatic mod snapshot…"));
      return;
    }
    if (action === "recovery.saved") return;
    return previousHandler(action, data);
  };
  if (typeof TURUL_HU_EN !== "undefined") Object.assign(TURUL_HU_EN, {
    "Mod-visszaállítás": "Mod recovery", "Automatikus mentések kezelése": "Manage automatic snapshots",
    "Hibamagyarázó": "Crash explanation", "Legutóbbi indítási hiba": "Last launch error", "Visszaállítás": "Restore"
  });
})();
