package net.turulmc.launcher.util;

import net.turulmc.launcher.download.VersionManager;

import java.io.File;
import java.io.IOException;
import java.util.ArrayList;
import java.util.List;

public final class MinecraftProcess {

    private MinecraftProcess() {}

    public static void launch(LaunchConfig config) throws IOException {
        if (config.profile != null && config.profile.installTurulClient && config.versionManager != null) {
            File modsDir = getModsDir(config);
            config.versionManager.installTurulClientMod(config.version, modsDir);
        }

        String javaPath = findJava();
        List<String> args = new ArrayList<>();

        args.add(javaPath);

        args.add("-Xms" + Math.min(config.maxRam / 2, 512) + "M");
        args.add("-Xmx" + config.maxRam + "M");

        args.add("-XX:+UseG1GC");
        args.add("-XX:-UseAdaptiveSizePolicy");
        args.add("-XX:+DisableExplicitGC");
        args.add("-XX:+ParallelRefProcEnabled");
        args.add("-XX:MaxGCPauseMillis=50");

        args.add("-XX:+UnlockExperimentalVMOptions");
        args.add("-XX:+AlwaysPreTouch");
        args.add("-XX:G1NewSizePercent=30");
        args.add("-XX:G1MaxNewSizePercent=40");
        args.add("-XX:G1HeapRegionSize=8M");
        args.add("-XX:G1ReservePercent=20");
        args.add("-XX:G1HeapWastePercent=5");
        args.add("-XX:G1MixedGCCountTarget=4");
        args.add("-XX:InitiatingHeapOccupancyPercent=15");
        args.add("-XX:G1MixedGCLiveThresholdPercent=90");
        args.add("-XX:G1RSetUpdatingPauseTimePercent=5");
        args.add("-XX:SurvivorRatio=32");
        args.add("-XX:MaxTenuringThreshold=1");

        if (config.jvmArgs != null && !config.jvmArgs.isEmpty()) {
            for (String part : config.jvmArgs.split("\\s+")) {
                if (part.isEmpty()) continue;
                String lower = part.toLowerCase();
                if (lower.startsWith("-agentlib") || lower.startsWith("-agentpath")
                    || lower.startsWith("-javaagent") || lower.startsWith("-xbootclasspath")
                    || lower.contains(";") || lower.contains("|") || lower.contains("&")) {
                    throw new IOException("Tiltott JVM argumentum: " + part);
                }
                args.add(part);
            }
        }

        args.add("-cp");
        args.add(buildClasspath(config));

        args.add("net.minecraft.launchwrapper.Launch");

        args.add("--username");
        args.add(config.username);
        args.add("--version");
        args.add(config.version);
        args.add("--gameDir");
        args.add(config.gameDir != null ? config.gameDir : getDefaultGameDir().getPath());
        args.add("--assetsDir");
        args.add(new File(getDefaultGameDir(), "assets").getPath());
        args.add("--assetIndex");
        args.add("1.8");
        args.add("--uuid");
        args.add(config.token);
        args.add("--accessToken");
        args.add(config.token);
        args.add("--userType");
        args.add("mojang");
        args.add("--versionType");
        args.add("TurulClient");

        if (config.serverIp != null && !config.serverIp.isEmpty()) {
            args.add("--server");
            args.add(config.serverIp);
            args.add("--port");
            args.add(String.valueOf(config.serverPort));
        }

        args.add("--tweakClass");
        args.add("net.minecraftforge.fml.common.launcher.FMLTweaker");

        args.add("--mods");
        args.add(getModsDir(config).getAbsolutePath());

        ProcessBuilder pb = new ProcessBuilder(args);
        pb.directory(new File(System.getProperty("user.home")));
        pb.inheritIO();

        System.out.println("Launching Minecraft " + config.version + " with " + config.maxRam + "MB RAM...");
        pb.start();
    }

    private static File getModsDir(LaunchConfig config) {
        File gameDir = config.gameDir != null ? new File(config.gameDir) : getDefaultGameDir();
        return new File(gameDir, "mods");
    }

    private static File getDefaultGameDir() {
        return new File(System.getProperty("user.home"), ".minecraft");
    }

    private static String findJava() {
        String javaHome = System.getProperty("java.home");
        if (javaHome == null) {
            javaHome = System.getenv("JAVA_HOME");
        }
        if (javaHome != null) {
            File javaExe = new File(javaHome, "bin/java.exe");
            if (javaExe.exists()) return javaExe.getAbsolutePath();
            javaExe = new File(javaHome, "bin/java");
            if (javaExe.exists()) return javaExe.getAbsolutePath();
        }
        return "java";
    }

    private static String buildClasspath(LaunchConfig config) {
        File versionDir = new File(config.clientPath).getParentFile();
        StringBuilder cp = new StringBuilder();

        cp.append(config.clientPath);

        File forge = new File(versionDir, "forge-universal.jar");
        if (forge.exists()) {
            cp.append(File.pathSeparator).append(forge.getAbsolutePath());
        }

        File libsDir = new File(versionDir, "libraries");
        if (libsDir.exists()) {
            addJars(cp, libsDir);
        }

        File mcLibs = new File(getDefaultGameDir(), "libraries");
        if (mcLibs.exists()) {
            addJars(cp, mcLibs);
        }

        File modsDir = getModsDir(config);
        if (modsDir.exists()) {
            addJars(cp, modsDir);
        }

        return cp.toString();
    }

    private static void addJars(StringBuilder cp, File dir) {
        File[] files = dir.listFiles();
        if (files == null) return;
        for (File f : files) {
            if (f.isDirectory()) {
                addJars(cp, f);
            } else if (f.getName().endsWith(".jar")) {
                if (cp.length() > 0) cp.append(File.pathSeparator);
                cp.append(f.getAbsolutePath());
            }
        }
    }

    public static class LaunchConfig {
        public String clientPath;
        public String username;
        public String token;
        public String version;
        public int maxRam = 1024;
        public String jvmArgs;
        public String serverIp;
        public int serverPort = 25565;
        public String gameDir;
        public LauncherProfiles.ServerProfile profile;
        public VersionManager versionManager;
    }
}
