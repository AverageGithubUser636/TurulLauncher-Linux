package net.turulmc.launcher.download;

import com.google.gson.Gson;
import com.google.gson.reflect.TypeToken;

import java.io.*;
import java.net.HttpURLConnection;
import java.net.URL;
import java.util.*;
import java.util.concurrent.CopyOnWriteArrayList;

public class VersionsManifest {

    private static final String MANIFEST_URL =
        "https://raw.githubusercontent.com/TurulSpigot/TurulSpigot/gh-pages/versions.json";

    private static final String FALLBACK_MANIFEST_URL =
        "https://raw.githubusercontent.com/TurulSpigot/turulmc-versions/main/versions.json";

    private static final Gson GSON = new Gson();

    private final File cacheFile;
    private final List<VersionEntry> entries = new CopyOnWriteArrayList<>();
    private long lastFetchTime;
    private static final long CACHE_TTL_MS = 300_000;

    public VersionsManifest(File cacheDir) {
        this.cacheFile = new File(cacheDir, "versions-cache.json");
        loadCache();
    }

    public List<VersionEntry> getEntries() {
        if (entries.isEmpty() || System.currentTimeMillis() - lastFetchTime > CACHE_TTL_MS) {
            try {
                fetchRemote();
            } catch (IOException ignored) {}
        }
        return new ArrayList<>(entries);
    }

    public VersionEntry findEntry(String versionId) {
        for (VersionEntry e : getEntries()) {
            if (e.id.equals(versionId)) return e;
        }
        return null;
    }

    public List<String> getVersionIds() {
        List<String> ids = new ArrayList<>();
        for (VersionEntry e : getEntries()) {
            ids.add(e.id);
        }
        return ids;
    }

    private void fetchRemote() throws IOException {
        List<VersionEntry> fetched = tryFetch(MANIFEST_URL);
        if (fetched == null || fetched.isEmpty()) {
            fetched = tryFetch(FALLBACK_MANIFEST_URL);
        }
        if (fetched != null && !fetched.isEmpty()) {
            entries.clear();
            entries.addAll(fetched);
            lastFetchTime = System.currentTimeMillis();
            saveCache();
        }
    }

    private List<VersionEntry> tryFetch(String urlStr) {
        try {
            URL url = new URL(urlStr);
            HttpURLConnection conn = (HttpURLConnection) url.openConnection();
            conn.setConnectTimeout(5000);
            conn.setReadTimeout(5000);
            conn.setRequestProperty("User-Agent", "TurulMC-Launcher/1.0");

            try (BufferedReader br = new BufferedReader(
                    new InputStreamReader(conn.getInputStream()))) {
                StringBuilder sb = new StringBuilder();
                String line;
                while ((line = br.readLine()) != null) {
                    sb.append(line);
                }
                return GSON.fromJson(sb.toString(),
                    new TypeToken<List<VersionEntry>>(){}.getType());
            }
        } catch (IOException e) {
            return null;
        }
    }

    private void loadCache() {
        if (cacheFile.exists()) {
            try (Reader r = new FileReader(cacheFile)) {
                CacheData data = GSON.fromJson(r, CacheData.class);
                if (data != null && data.entries != null) {
                    entries.addAll(data.entries);
                    lastFetchTime = data.timestamp;
                }
            } catch (IOException ignored) {}
        }
    }

    private void saveCache() {
        try (Writer w = new FileWriter(cacheFile)) {
            CacheData data = new CacheData();
            data.timestamp = System.currentTimeMillis();
            data.entries = new ArrayList<>(entries);
            GSON.toJson(data, w);
        } catch (IOException ignored) {}
    }

    public static class VersionEntry {
        public String id;
        public String url;
        public String forgeUrl;
        public String turulClientUrl;
        public String releaseDate;
        public String type;
        public String md5;
        public int minRam;
        public String notes;

        public boolean isRelease() {
            return "RELEASE".equals(type) || type == null || type.isEmpty();
        }

        public boolean isSnapshot() {
            return "SNAPSHOT".equals(type);
        }
    }

    private static class CacheData {
        long timestamp;
        List<VersionEntry> entries;
    }
}
