TurulLauncher 4.3.3 PRI / XAML startup fix

Gyokerok:
- Windows App SDK / .NET 10 unpackaged dotnet publish kihagyhatja a TurulMC.Launcher.pri fajlt.
- Enelkul a MainWindow.InitializeComponent() XamlParseException 0x802B000A hibaval leallhat.

Javitas:
- TurulMC.Launcher.csproj: a generated project PRI bekerul a ResolvedFileToPublish listaba.
- publish.cmd: build utan kotelezoen ellenorzi, hogy publish\GUI\TurulMC.Launcher.pri letezik-e.

Teszt:
1. publish-all.cmd
2. Ellenorizd: dir publish\GUI\TurulMC.Launcher.pri
3. Inditsd: publish\GUI\TurulMC.Launcher.exe
4. Ha elindul, csak utana installer + GitHub release.
