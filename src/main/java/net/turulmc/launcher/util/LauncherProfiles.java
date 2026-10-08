package net.turulmc.launcher.util;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.reflect.TypeToken;

import java.io.*;
import java.util.*;

public class LauncherProfiles {

    private static final Gson GSON = new GsonBuilder().setPrettyPrinting().create();

    private final File profileFile;
    private final Map<String, ServerProfile> profiles = new LinkedHashMap<>();
    private final Map<String, ProfileCategory> categories = new LinkedHashMap<>();

    public LauncherProfiles(File profileFile) {
        this.profileFile = profileFile;
        load();
        ensureDefaults();
    }

    public List<String> getProfileNames() {
        return new ArrayList<>(profiles.keySet());
    }

    public Map<String, ProfileCategory> getCategories() {
        return categories;
    }

    public List<ServerProfile> getProfilesByCategory(String categoryId) {
        List<ServerProfile> result = new ArrayList<>();
        for (ServerProfile p : profiles.values()) {
            if (categoryId == null || categoryId.equals(p.category)) {
                result.add(p);
            }
        }
        return result;
    }

    public ServerProfile getProfile(String name) {
        return profiles.get(name);
    }

    public void addProfile(ServerProfile profile) {
        profiles.put(profile.name, profile);
        save();
    }

    public void removeProfile(String name) {
        profiles.remove(name);
        save();
    }

    private void ensureDefaults() {
        categories.put("turulmc", new ProfileCategory("turulmc", "TurulMC Network",
            "TurulSpigot 1.8.8 PvP servers"));
        categories.put("turulnetwork", new ProfileCategory("turulnetwork", "TurulNetwork",
            "Paper 1.26.2 production servers"));
        categories.put("custom", new ProfileCategory("custom", "Custom Servers",
            "User-configured servers"));

        if (profiles.isEmpty()) {
            ServerProfile local = new ServerProfile();
            local.name = "Local Development";
            local.serverIp = "localhost";
            local.serverPort = 25565;
            local.description = "Local TurulSpigot development server";
            local.category = "turulmc";
            local.gameDir = new File(System.getProperty("user.home"), ".turulmc/game/dev").getPath();

            ServerProfile publicTest = new ServerProfile();
            publicTest.name = "TurulMC Public";
            publicTest.serverIp = "play.turulmc.net";
            publicTest.serverPort = 25565;
            publicTest.description = "Official TurulMC public server (1.8.8)";
            publicTest.category = "turulmc";

            ServerProfile networkTest = new ServerProfile();
            networkTest.name = "TurulNetwork (1.26.2)";
            networkTest.serverIp = "play.turulnetwork.net";
            networkTest.serverPort = 25565;
            networkTest.description = "TurulNetwork Paper 1.26.2 production";
            networkTest.category = "turulnetwork";
            networkTest.jvmArgs = "-XX:+UseZGC -XX:ZUncommitDelay=30";

            profiles.put(local.name, local);
            profiles.put(publicTest.name, publicTest);
            profiles.put(networkTest.name, networkTest);
            save();
        }
    }

    private void load() {
        if (profileFile.exists()) {
            try (Reader r = new FileReader(profileFile)) {
                ProfileData data = GSON.fromJson(r, ProfileData.class);
                if (data != null) {
                    if (data.profiles != null) {
                        profiles.putAll(data.profiles);
                    }
                    if (data.categories != null) {
                        categories.clear();
                        categories.putAll(data.categories);
                    }
                }
            } catch (IOException ignored) {}
        }
    }

    private void save() {
        try (Writer w = new FileWriter(profileFile)) {
            ProfileData data = new ProfileData();
            data.profiles = profiles;
            data.categories = categories;
            GSON.toJson(data, w);
        } catch (IOException ignored) {}
    }

    public static class ServerProfile {
        public String name;
        public String serverIp = "localhost";
        public int serverPort = 25565;
        public String description = "";
        public String jvmArgs = "-XX:+UseG1GC -XX:-UseAdaptiveSizePolicy";
        public String gameDir;
        public boolean fullscreen;
        public int width = 854;
        public int height = 480;
        public String category = "custom";
        public String minecraftVersion = "1.8.9";
        public boolean installTurulClient = true;
    }

    public static class ProfileCategory {
        public String id;
        public String displayName;
        public String description;

        public ProfileCategory() {}
        public ProfileCategory(String id, String displayName, String description) {
            this.id = id;
            this.displayName = displayName;
            this.description = description;
        }
    }

    private static class ProfileData {
        Map<String, ServerProfile> profiles;
        Map<String, ProfileCategory> categories;
    }
}
