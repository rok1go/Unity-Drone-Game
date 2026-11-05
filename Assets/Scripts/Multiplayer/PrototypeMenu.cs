using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

public class PrototypeMenu : MonoBehaviour
{
    private enum Screen { Home, Play, Create, Join, Lobby, Pause, Loading }
    public bool Visible { get; private set; }
    private MultiplayerPrototype app;
    private UIDocument document;
    private PanelSettings panel;
    private VisualElement root, content;
    private Label error, status;
    private Screen screen;
    private string lobbySignature;
    private TextField nickname, code;

    public void Initialize(MultiplayerPrototype controller)
    {
        app = controller;
        panel = ScriptableObject.CreateInstance<PanelSettings>();
        panel.name = "Main menu";
        panel.themeStyleSheet = Resources.Load<ThemeStyleSheet>("UI/FpvTheme");
        panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
        panel.referenceResolution = new Vector2Int(1600, 900);
        panel.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
        panel.match = 0.5f; panel.sortingOrder = 150;
        document = gameObject.AddComponent<UIDocument>(); document.panelSettings = panel;
        root = document.rootVisualElement;
        root.styleSheets.Add(Resources.Load<StyleSheet>("UI/PrototypeMenu"));
        root.style.unityFontDefinition = FontDefinition.FromFont(Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
        root.AddToClassList("prototype-menu");
        ShowHome();
    }

    private void Draw(string title, string subtitle, bool wide = false)
    {
        Visible = true; root.style.display = DisplayStyle.Flex; root.Clear();
        var backdrop = new VisualElement { pickingMode = PickingMode.Ignore }; backdrop.AddToClassList("menu-backdrop"); root.Add(backdrop);
        HudGlassPanel.Attach(backdrop, new Color(0.035f, 0.065f, 0.09f, 0.60f),
            new Color(0.015f, 0.03f, 0.045f, 0.96f), false, 0f, 0f);
        content = new VisualElement(); content.AddToClassList(wide ? "menu-wide" : "menu-card"); root.Add(content);
        var heading = Text(title, "menu-title"); content.Add(heading);
        if (!string.IsNullOrEmpty(subtitle)) content.Add(Text(subtitle, "menu-subtitle"));
        status = Text("", "menu-status"); error = Text("", "menu-error");
    }

    private static Label Text(string value, string style)
    {
        var label = new Label(value) { enableRichText = false }; label.AddToClassList(style); return label;
    }

    private static Button ActionButton(VisualElement parent, string title, Action action, bool primary = false)
    {
        var button = new Button(action) { text = title };
        button.AddToClassList("menu-button");
        if (primary) button.AddToClassList("primary");
        parent.Add(button); return button;
    }

    private void Footer()
    {
        content.Add(status); content.Add(error);
        content.SetEnabled(!app.Busy);
    }

    public void ShowHome()
    {
        screen = Screen.Home;
        Draw("ПРЯТКИ", "Люди против дронов");
        ActionButton(content, "Играть", ShowPlay, true);
        ActionButton(content, "Тренировка", app.StartTraining);
        ActionButton(content, "Выйти", Quit);
        Footer();
    }

    private void ShowPlay()
    {
        screen = Screen.Play;
        Draw("Играть", "Создай лобби или войди по коду друга.");
        nickname = new TextField("Твоё имя") { value = app.PlayerName, maxLength = 24 }; content.Add(nickname);
        ActionButton(content, "Создать лобби", () => { app.SavePlayerName(nickname.value); ShowCreate(); }, true);
        ActionButton(content, "Присоединиться", () => { app.SavePlayerName(nickname.value); ShowJoin(); });
        ActionButton(content, "Назад", ShowHome); Footer();
    }

    private void ShowCreate()
    {
        screen = Screen.Create;
        Draw("Создать лобби", "Ты будешь хостом матча.");
        var fields = new SettingsFields(content, new PrototypeLobbySettings(), true);
        ActionButton(content, "Создать", () => app.CreateLobby(fields.Read()), true);
        ActionButton(content, "Назад", ShowPlay); Footer();
    }

    private void ShowJoin()
    {
        screen = Screen.Join;
        Draw("Присоединиться", "Введи код лобби, который прислал хост.");
        code = new TextField("Код") { maxLength = 12 }; code.AddToClassList("code-input"); content.Add(code);
        code.RegisterCallback<KeyDownEvent>(evt =>
        {
            if (evt.keyCode == KeyCode.Return && !app.Busy) { app.JoinLobby(code.value); evt.StopPropagation(); }
        });
        ActionButton(content, "Войти", () => app.JoinLobby(code.value), true);
        ActionButton(content, "Назад", ShowPlay); Footer(); code.Focus();
    }

    public void ShowLobby() { screen = Screen.Lobby; lobbySignature = null; RefreshLobby(); }
    public void EnsureLobby() { if (screen != Screen.Lobby) ShowLobby(); else RefreshLobby(); }

    public void RefreshLobby()
    {
        if (screen != Screen.Lobby) return;
        var state = app.State;
        string signature = app.JoinCode + JsonUtility.ToJson(state.Settings) + app.IsHost +
            string.Join("|", state.Members.Select(m => m.Id + ":" + m.Name + ":" + m.Team + ":" + m.Ready));
        if (signature == lobbySignature) return;
        lobbySignature = signature;
        var settings = state.Settings ?? new PrototypeLobbySettings();
        Draw(settings.Name, "Лобби  ·  " + state.Members.Length + " / " + settings.MaxPlayers, true);
        var codeRow = new VisualElement(); codeRow.AddToClassList("code-row"); content.Add(codeRow);
        codeRow.Add(Text("Код: " + (string.IsNullOrEmpty(app.JoinCode) ? "…" : app.JoinCode), "lobby-code"));
        ActionButton(codeRow, "Скопировать", () => { GUIUtility.systemCopyBuffer = app.JoinCode ?? ""; status.text = "Код скопирован."; });
        var columns = new VisualElement(); columns.AddToClassList("lobby-columns"); content.Add(columns);
        var players = new VisualElement(); players.AddToClassList("players-column"); columns.Add(players);
        players.Add(Text("Игроки", "section-title"));
        foreach (var member in state.Members)
        {
            var row = new VisualElement(); row.AddToClassList("player-row");
            var name = Text(member.Name + (member.Id == app.LocalId ? " · ты" : "") +
                (member.Id == 0 ? " · хост" : ""), "player-name"); row.Add(name);
            row.Add(Text(member.Team == PrototypeTeam.Humans ? "Человек" : "Дрон", "player-team"));
            row.Add(Text(member.Ready ? "Готов" : "Не готов", member.Ready ? "player-ready" : "player-waiting"));
            players.Add(row);
        }
        if (state.Members.Length == 0) players.Add(Text("Ждём список игроков…", "menu-subtitle"));
        var teams = new VisualElement(); teams.AddToClassList("inline-buttons"); players.Add(teams);
        ActionButton(teams, "За людей", () => app.ChooseTeam(PrototypeTeam.Humans));
        ActionButton(teams, "За дронов", () => app.ChooseTeam(PrototypeTeam.Drones));
        var mine = state.Members.FirstOrDefault(m => m.Id == app.LocalId);
        ActionButton(players, mine != null && mine.Ready ? "Снять готовность" : "Готов",
            () => app.SetReady(mine == null || !mine.Ready), true);
        var options = new VisualElement(); options.AddToClassList("settings-column"); columns.Add(options);
        options.Add(Text("Настройки матча", "section-title"));
        if (app.IsHost)
        {
            var fields = new SettingsFields(options, settings, false);
            ActionButton(options, "Применить настройки", () => app.ChangeSettings(fields.Read()));
            options.Add(Text("После изменения все подтверждают готовность заново.", "field-note"));
        }
        else
        {
            options.Add(Text("Прятки: " + settings.HidingSeconds + " сек.\nПоиск: " + settings.HuntingSeconds +
                " сек.\nДронов на команду: " + settings.DroneStock, "settings-summary"));
            options.Add(Text("Настройки меняет хост.", "field-note"));
        }
        if (app.IsHost) ActionButton(content, "Начать матч", app.StartMatch, true);
        else content.Add(Text("Матч запустит хост, когда все будут готовы.", "field-note"));
        ActionButton(content, app.IsHost ? "Закрыть лобби" : "Выйти из лобби", () => app.LeaveWithReason());
        Footer();
    }

    public void ShowPause()
    {
        screen = Screen.Pause;
        Draw("Меню", app.InTraining ? "Тренировка" : "Матч продолжается");
        ActionButton(content, "Продолжить", app.Resume, true);
        if (app.IsHost && app.InMatch) ActionButton(content, "Вернуться в лобби", app.ReturnToLobby);
        ActionButton(content, "Главное меню", () => app.LeaveWithReason()); Footer();
    }

    public void ShowLoading(string text)
    {
        screen = Screen.Loading; Draw("Подожди", text); Footer();
    }

    public void Hide() { Visible = false; root.style.display = DisplayStyle.None; }
    public void ShowError(string text) { if (error != null) error.text = text; }
    public void SetBusy(string message)
    {
        if (status != null) status.text = message ?? "";
        if (content != null) content.SetEnabled(string.IsNullOrEmpty(message));
    }

    private sealed class SettingsFields
    {
        private readonly TextField name;
        private readonly IntegerField players, hiding, hunting, drones;
        private readonly int fixedPlayers;
        private readonly string fixedName;
        public SettingsFields(VisualElement parent, PrototypeLobbySettings settings, bool creating)
        {
            fixedPlayers = settings.MaxPlayers;
            fixedName = settings.Name;
            if (creating)
            {
                name = new TextField("Название") { value = settings.Name, maxLength = 32 }; parent.Add(name);
                players = new IntegerField("Игроков (2–8)") { value = settings.MaxPlayers }; parent.Add(players);
            }
            hiding = new IntegerField("Прятки, сек. (10–120)") { value = settings.HidingSeconds };
            hunting = new IntegerField("Поиск, сек. (60–600)") { value = settings.HuntingSeconds };
            drones = new IntegerField("Дронов (1–12)") { value = settings.DroneStock };
            parent.Add(hiding); parent.Add(hunting); parent.Add(drones);
        }
        public PrototypeLobbySettings Read() => new PrototypeLobbySettings
        {
            Name = name != null ? name.value : fixedName, MaxPlayers = players != null ? players.value : fixedPlayers,
            HidingSeconds = hiding.value, HuntingSeconds = hunting.value, DroneStock = drones.value
        };
    }

    private static void Quit()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    private void OnDestroy()
    {
        if (document != null) Destroy(document);
        if (panel != null) Destroy(panel);
    }
}
