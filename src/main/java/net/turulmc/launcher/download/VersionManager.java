package net.turulmc.launcher.download;

import com.google.gson.Gson;
import com.google.gson.reflect.TypeToken;

import java.io.*;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.channels.Channels;
import java.nio.channels.ReadableByteChannel;
import java.nio.file.Files;
import java.nio.file.StandardCopyOption;
import java.util.*;

public class VersionManager {

    private static final String VERSIONS_URL =
        "https://raw.githubusercontent.com/TurulSpigot/TurulSpigot/gh-pages/versions.json";
    private static final Gson GSON = new Gson();

    private final File versionsDir;
    private final VersionsManifest manifest;

    public VersionManager(File versionsDir) {
        this.versionsDir = versionsDir;
        versionsDir.mkdirs();
        this.manifest = new VersionsManifest(versionsDir);
    }

    public VersionsManifest getManifest() {
        return manifest;
    }

    public List<String> getAvailableVersions() {
        Set<String> versions = new LinkedHashSet<>();

        File[] local = versionsDir.listFiles(File::isDirectory);
        if (local != null) {
            for (File f : local) {
                String name = f.getName();
                if (!name.equals("cache") && !name.startsWith(".")) {
                    versions.add(name);
                }
            }
        }

        for (String id : manifest.getVersionIds()) {
            versions.add(id);
        }

        if (versions.isEmpty()) {
            versions.add("1.8.9");
        }
        return new ArrayList<>(versions);
    }

    public String ensureDownloaded(String version) throws IOException {
        File versionDir = getVersionDir(version);
        File clientJar = new File(versionDir, version + ".jar");
        File forgeJar = new File(versionDir, "forge-universal.jar");

        if (clientJar.exists()) {
            return clientJar.getAbsolutePath();
        }

        versionDir.mkdirs();

        VersionsManifest.VersionEntry entry = manifest.findEntry(version);

        if (entry != null && entry.url != null && !entry.url.isEmpty()) {
            System.out.println("Downloading " + version + " from manifest: " + entry.url);
            downloadFile(entry.url, clientJar);
        } else {
            String url = findVersionInMojangManifest(version);
            if (url == null) {
                throw new IOException("No download URL found for version " + version);
            }
            System.out.println("Downloading " + version + " from Mojang");
            downloadFile(url, clientJar);
        }

        if (entry != null && entry.forgeUrl != null && !entry.forgeUrl.isEmpty()) {
            if (!forgeJar.exists()) {
                System.out.println("Downloading Forge universal for " + version);
                downloadFile(entry.forgeUrl, forgeJar);
            }
        } else {
            String forgeUrl = findForgeUrl(version);
            if (forgeUrl != null && !forgeJar.exists()) {
                System.out.println("Downloading Forge universal for " + version);
                downloadFile(forgeUrl, forgeJar);
            }
        }

        File nativesDir = new File(versionDir, "natives");
        nativesDir.mkdirs();

        return clientJar.getAbsolutePath();
    }

    public String ensureTurulClientDownloaded(String version) throws IOException {
        VersionsManifest.VersionEntry entry = manifest.findEntry(version);
        if (entry == null || entry.turulClientUrl == null || entry.turulClientUrl.isEmpty()) {
            return null;
        }

        File versionDir = getVersionDir(version);
        File turulClientJar = new File(versionDir, "TurulClient-" + version + ".jar");

        if (!turulClientJar.exists()) {
            System.out.println("Downloading TurulClient for " + version);
            downloadFile(entry.turulClientUrl, turulClientJar);
        }

        return turulClientJar.getAbsolutePath();
    }

    public void installTurulClientMod(String version, File modsDir) throws IOException {
        if (modsDir == null) return;
        String turulJarPath = ensureTurulClientDownloaded(version);
        if (turulJarPath == null) return;

        File turulJar = new File(turulJarPath);
        if (!turulJar.exists()) return;

        modsDir.mkdirs();
        File target = new File(modsDir, "TurulClient.jar");

        Files.copy(turulJar.toPath(), target.toPath(), StandardCopyOption.REPLACE_EXISTING);
        System.out.println("Installed TurulClient mod to " + target);
    }

    private File getVersionDir(String version) {
        return new File(versionsDir, version);
    }

    private String findForgeUrl(String version) {
        if ("1.8.9".equals(version)) {
            return "https://maven.minecraftforge.net/net/minecraftforge/forge/" +
                   "1.8.9-11.15.1.2318-1.8.9/" +
                   "forge-1.8.9-11.15.1.2318-1.8.9-universal.jar";
        }
        return null;
    }

    private String findVersionInMojangManifest(String version) throws IOException {
        URL url = new URL("https://piston-meta.mojang.com/mc/game/version_manifest.json");
        try (BufferedReader br = new BufferedReader(
                new InputStreamReader(url.openStream()))) {
            StringBuilder sb = new StringBuilder();
            String line;
            while ((line = br.readLine()) != null) {
                sb.append(line);
            }
            String manifest = sb.toString();
            String searchKey = "\"id\":\"" + version + "\"";
            int idx = manifest.indexOf(searchKey);
            if (idx < 0) return null;

            int urlIdx = manifest.indexOf("\"url\":\"", idx + searchKey.length());
            if (urlIdx < 0) return null;
            urlIdx += 7;
            int urlEnd = manifest.indexOf("\"", urlIdx);
            if (urlEnd < 0) return null;

            String versionUrl = manifest.substring(urlIdx, urlEnd);

            URL vUrl = new URL(versionUrl);
            try (BufferedReader vbr = new BufferedReader(
                    new InputStreamReader(vUrl.openStream()))) {
                StringBuilder vsb = new StringBuilder();
                String vline;
                while ((vline = vbr.readLine()) != null) {
                    vsb.append(vline);
                }
                String vjson = vsb.toString();
                String clientKey = "\"client\":";
                int ci = vjson.indexOf(clientKey);
                if (ci < 0) return null;
                int cu = vjson.indexOf("\"url\":\"", ci);
                if (cu < 0) return null;
                cu += 7;
                int ce = vjson.indexOf("\"", cu);
                return vjson.substring(cu, ce);
            }
        }
    }

    private void downloadFile(String urlStr, File target) throws IOException {
        URL url = new URL(urlStr);
        HttpURLConnection conn = (HttpURLConnection) url.openConnection();
        conn.setConnectTimeout(15000);
        conn.setReadTimeout(30000);
        conn.setInstanceFollowRedirects(true);
        conn.setRequestProperty("User-Agent", "TurulMC-Launcher/1.0");

        File tmp = new File(target.getAbsolutePath() + ".tmp");
        try (InputStream in = conn.getInputStream();
             ReadableByteChannel rbc = Channels.newChannel(in);
             FileOutputStream fos = new FileOutputStream(tmp)) {
            fos.getChannel().transferFrom(rbc, 0, Long.MAX_VALUE);
        }

        Files.move(tmp.toPath(), target.toPath(), StandardCopyOption.REPLACE_EXISTING);
    }
}
