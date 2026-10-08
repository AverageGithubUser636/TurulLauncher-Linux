package net.turulmc.launcher.auth;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;

import javax.net.ssl.HttpsURLConnection;
import java.io.*;
import java.net.HttpURLConnection;
import java.net.URL;
import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;

public class AuthManager {

    private static final String AUTH_SERVER = "https://authserver.mojang.com";
    private static final Gson GSON = new GsonBuilder().setPrettyPrinting().create();

    private final File credentialFile;
    private CachedCredentials cached;

    public AuthManager(File credentialFile) {
        this.credentialFile = credentialFile;
        loadCache();
    }

    /**
     * Offline-only auth (4.4.6): nincs OAuth/Mojang-auth sehol.
     */
    public String authenticate(String email, char[] password) throws IOException {
        return authenticateOffline(email);
    }

    private String authenticateOffline(String username) {
        String token = "offline:" + username;
        cacheCredentials(username, token);
        return token;
    }

    private String authenticateMojang(String email, String password) throws IOException {
        String payload = String.format(
            "{\"agent\":{\"name\":\"Minecraft\",\"version\":1}," +
            "\"username\":\"%s\",\"password\":\"%s\"}",
            escapeJson(email), escapeJson(password)
        );

        String response = postJson(AUTH_SERVER + "/authenticate", payload);
        AuthResponse auth = GSON.fromJson(response, AuthResponse.class);

        if (auth == null || auth.accessToken == null) {
            throw new IOException("Authentication failed: no token returned");
        }

        cacheCredentials(email, auth.accessToken);
        return auth.accessToken;
    }

    private boolean validateToken(String token) {
        try {
            String payload = String.format(
                "{\"accessToken\":\"%s\"}", escapeJson(token)
            );
            String response = postJson(AUTH_SERVER + "/validate", payload);
            return response != null && !response.contains("error");
        } catch (IOException e) {
            return false;
        }
    }

    private String postJson(String urlStr, String payload) throws IOException {
        URL url = new URL(urlStr);
        HttpsURLConnection conn = (HttpsURLConnection) url.openConnection();
        conn.setRequestMethod("POST");
        conn.setRequestProperty("Content-Type", "application/json");
        conn.setDoOutput(true);
        conn.setConnectTimeout(10000);
        conn.setReadTimeout(10000);

        try (OutputStream os = conn.getOutputStream()) {
            byte[] input = payload.getBytes(StandardCharsets.UTF_8);
            os.write(input, 0, input.length);
        }

        int code = conn.getResponseCode();
        if (code == 204) return "{}";

        try (BufferedReader br = new BufferedReader(
                new InputStreamReader(
                    code >= 400 ? conn.getErrorStream() : conn.getInputStream(),
                    StandardCharsets.UTF_8))) {
            StringBuilder sb = new StringBuilder();
            String line;
            while ((line = br.readLine()) != null) {
                sb.append(line);
            }
            if (code >= 400) {
                throw new IOException("Auth server error " + code + ": " + sb.toString());
            }
            return sb.toString();
        }
    }

    private String escapeJson(String s) {
        return s.replace("\\", "\\\\").replace("\"", "\\\"");
    }

    private void cacheCredentials(String email, String token) {
        cached = new CachedCredentials(email, token);
        try (Writer w = new OutputStreamWriter(new FileOutputStream(credentialFile), StandardCharsets.UTF_8)) {
            GSON.toJson(cached, w);
        } catch (IOException ignored) {}
    }

    private void loadCache() {
        if (credentialFile.exists()) {
            try (Reader r = new InputStreamReader(new FileInputStream(credentialFile), StandardCharsets.UTF_8)) {
                cached = GSON.fromJson(r, CachedCredentials.class);
            } catch (IOException ignored) {}
        }
    }

    private static class CachedCredentials {
        String email;
        String token;

        CachedCredentials() {}
        CachedCredentials(String email, String token) {
            this.email = email;
            this.token = token;
        }
    }

    private static class AuthResponse {
        String accessToken;
        String clientToken;
    }
}
