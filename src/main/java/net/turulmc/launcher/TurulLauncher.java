package net.turulmc.launcher;

import net.turulmc.launcher.auth.AuthManager;
import net.turulmc.launcher.download.VersionManager;
import net.turulmc.launcher.util.LauncherProfiles;
import net.turulmc.launcher.util.MinecraftProcess;

import javax.swing.*;
import java.awt.*;
import java.io.File;
import java.io.IOException;
import java.util.Map;
import java.util.concurrent.ExecutionException;

public class TurulLauncher {

    private static final String LAUNCHER_VERSION = "1.0.0";
    private static final File LAUNCHER_DIR = new File(
        System.getProperty("user.home"), ".turulmc"
    );

    private final AuthManager auth;
    private final VersionManager versions;
    private final LauncherProfiles profiles;

    private JFrame frame;
    private JTextField emailField;
    private JPasswordField passwordField;
    private JComboBox<String> versionBox;
    private JComboBox<String> profileBox;
    private JLabel profileDescription;
    private JSpinner ramSpinner;
    private JButton playButton;
    private JLabel statusLabel;
    private JLabel connectedIndicator;
    private JComboBox<String> categoryBox;

    public TurulLauncher() {
        LAUNCHER_DIR.mkdirs();
        this.auth = new AuthManager(new File(LAUNCHER_DIR, "credentials.json"));
        this.versions = new VersionManager(new File(LAUNCHER_DIR, "versions"));
        this.profiles = new LauncherProfiles(new File(LAUNCHER_DIR, "profiles.json"));
    }

    public static void main(String[] args) {
        try {
            UIManager.setLookAndFeel(UIManager.getSystemLookAndFeelClassName());
        } catch (Exception ignored) {}
        SwingUtilities.invokeLater(() -> new TurulLauncher().buildAndShow());
    }

    private void buildAndShow() {
        frame = new JFrame("TurulMC Launcher " + LAUNCHER_VERSION);
        frame.setDefaultCloseOperation(JFrame.EXIT_ON_CLOSE);
        frame.setSize(560, 500);
        frame.setResizable(false);
        frame.setLocationRelativeTo(null);
        frame.setLayout(new BorderLayout());

        frame.add(createHeader(), BorderLayout.NORTH);
        frame.add(createCenterPanel(), BorderLayout.CENTER);
        frame.add(createFooter(), BorderLayout.SOUTH);

        loadCategories();
        loadProfiles();
        loadVersions();

        frame.setVisible(true);
    }

    private JComponent createHeader() {
        JPanel header = new JPanel(new BorderLayout());
        header.setBackground(new Color(0x2D, 0x2D, 0x2D));
        header.setBorder(BorderFactory.createEmptyBorder(12, 16, 12, 16));

        JPanel leftGroup = new JPanel(new FlowLayout(FlowLayout.LEFT, 8, 0));
        leftGroup.setOpaque(false);

        JLabel title = new JLabel("TurulMC");
        title.setFont(new Font("SansSerif", Font.BOLD, 20));
        title.setForeground(Color.WHITE);
        leftGroup.add(title);

        connectedIndicator = new JLabel("");
        connectedIndicator.setForeground(new Color(0x88, 0x88, 0x88));
        connectedIndicator.setFont(new Font("SansSerif", Font.PLAIN, 11));
        leftGroup.add(connectedIndicator);

        header.add(leftGroup, BorderLayout.WEST);

        statusLabel = new JLabel("Ready");
        statusLabel.setForeground(new Color(0x88, 0x88, 0x88));
        header.add(statusLabel, BorderLayout.EAST);

        return header;
    }

    private JComponent createCenterPanel() {
        JPanel center = new JPanel(new GridBagLayout());
        center.setBorder(BorderFactory.createEmptyBorder(16, 16, 16, 16));
        GridBagConstraints c = new GridBagConstraints();
        c.fill = GridBagConstraints.HORIZONTAL;
        c.insets = new Insets(4, 4, 4, 4);

        int row = 0;

        addLabel(center, "Email / Username:", c, 0, row);
        emailField = new JTextField(20);
        addField(center, emailField, c, 1, row++);

        addLabel(center, "Password (blank=offline):", c, 0, row);
        passwordField = new JPasswordField(20);
        addField(center, passwordField, c, 1, row++);

        addLabel(center, "Server Category:", c, 0, row);
        categoryBox = new JComboBox<>();
        categoryBox.addActionListener(e -> loadProfiles());
        addField(center, categoryBox, c, 1, row++);

        addLabel(center, "Server Profile:", c, 0, row);
        profileBox = new JComboBox<>();
        profileBox.addActionListener(e -> updateProfileDescription());
        addField(center, profileBox, c, 1, row++);

        profileDescription = new JLabel(" ");
        profileDescription.setFont(new Font("SansSerif", Font.ITALIC, 11));
        profileDescription.setForeground(new Color(0x66, 0x66, 0x66));
        c.gridx = 1; c.gridy = row++;
        center.add(profileDescription, c);

        addLabel(center, "Client Version:", c, 0, row);
        versionBox = new JComboBox<>();
        addField(center, versionBox, c, 1, row++);

        addLabel(center, "RAM (MB):", c, 0, row);
        SpinnerModel ramModel = new SpinnerNumberModel(1024, 512, 8192, 256);
        ramSpinner = new JSpinner(ramModel);
        JSpinner.DefaultEditor editor = (JSpinner.DefaultEditor) ramSpinner.getEditor();
        editor.getTextField().setColumns(6);
        addField(center, ramSpinner, c, 1, row++);

        return center;
    }

    private void addLabel(JPanel panel, String text, GridBagConstraints c, int x, int y) {
        c.gridx = x; c.gridy = y;
        c.weightx = 0.0;
        JLabel label = new JLabel(text);
        label.setHorizontalAlignment(SwingConstants.RIGHT);
        panel.add(label, c);
    }

    private void addField(JPanel panel, JComponent field, GridBagConstraints c, int x, int y) {
        c.gridx = x; c.gridy = y;
        c.weightx = 1.0;
        panel.add(field, c);
    }

    private JComponent createFooter() {
        JPanel footer = new JPanel(new FlowLayout(FlowLayout.CENTER, 8, 8));

        playButton = new JButton("Play");
        playButton.setFont(new Font("SansSerif", Font.BOLD, 14));
        playButton.setPreferredSize(new Dimension(120, 36));
        playButton.setBackground(new Color(0x4C, 0xAF, 0x50));
        playButton.setForeground(Color.WHITE);
        playButton.setFocusPainted(false);
        playButton.addActionListener(e -> launchGame());
        footer.add(playButton);

        JButton refreshButton = new JButton("Refresh");
        refreshButton.addActionListener(e -> {
            loadVersions();
            loadProfiles();
        });
        footer.add(refreshButton);

        return footer;
    }

    private void loadCategories() {
        categoryBox.removeAllItems();
        Map<String, LauncherProfiles.ProfileCategory> cats = profiles.getCategories();
        for (Map.Entry<String, LauncherProfiles.ProfileCategory> entry : cats.entrySet()) {
            categoryBox.addItem(entry.getKey());
        }
    }

    private void loadVersions() {
        versionBox.removeAllItems();
        for (String v : versions.getAvailableVersions()) {
            versionBox.addItem(v);
        }
    }

    private void loadProfiles() {
        profileBox.removeAllItems();
        String category = (String) categoryBox.getSelectedItem();
        for (LauncherProfiles.ServerProfile p : profiles.getProfilesByCategory(category)) {
            profileBox.addItem(p.name);
        }
        updateProfileDescription();
    }

    private void updateProfileDescription() {
        String name = (String) profileBox.getSelectedItem();
        if (name != null) {
            LauncherProfiles.ServerProfile p = profiles.getProfile(name);
            if (p != null && p.description != null && !p.description.isEmpty()) {
                profileDescription.setText(p.description);
                return;
            }
        }
        profileDescription.setText(" ");
    }

    private void launchGame() {
        String email = emailField.getText().trim();
        char[] password = passwordField.getPassword();
        String version = (String) versionBox.getSelectedItem();
        String profileName = (String) profileBox.getSelectedItem();
        int ram = (Integer) ramSpinner.getValue();

        if (email.isEmpty()) {
            JOptionPane.showMessageDialog(frame, "Please enter a username.");
            return;
        }
        if (version == null) {
            JOptionPane.showMessageDialog(frame, "Please select a client version.");
            return;
        }

        setStatus("Authenticating...");
        playButton.setEnabled(false);

        new SwingWorker<MinecraftProcess.LaunchConfig, Void>() {
            @Override
            protected MinecraftProcess.LaunchConfig doInBackground() throws Exception {
                String token = auth.authenticate(email, password);
                String clientPath = versions.ensureDownloaded(version);

                MinecraftProcess.LaunchConfig config = new MinecraftProcess.LaunchConfig();
                config.clientPath = clientPath;
                config.username = email;
                config.token = token;
                config.version = version;
                config.maxRam = ram;
                config.profile = profiles.getProfile(profileName);
                config.versionManager = versions;

                if (config.profile != null) {
                    config.serverIp = config.profile.serverIp;
                    config.serverPort = config.profile.serverPort;
                    config.jvmArgs = config.profile.jvmArgs;
                    config.gameDir = config.profile.gameDir;
                }
                return config;
            }

            @Override
            protected void done() {
                try {
                    MinecraftProcess.LaunchConfig config = get();
                    setStatus("Launching...");
                    MinecraftProcess.launch(config);
                    frame.setVisible(false);
                    frame.dispose();
                    System.exit(0);
                } catch (InterruptedException | ExecutionException | IOException e) {
                    String msg = e.getCause() != null ? e.getCause().getMessage() : e.getMessage();
                    setStatus("Error: " + msg);
                    JOptionPane.showMessageDialog(frame,
                        "Failed to launch: " + msg,
                        "Launch Error", JOptionPane.ERROR_MESSAGE);
                } finally {
                    playButton.setEnabled(true);
                }
            }
        }.execute();
    }

    private void setStatus(String text) {
        SwingUtilities.invokeLater(() -> statusLabel.setText(text));
    }
}
