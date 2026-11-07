using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

public partial class PrototypeMenu : MonoBehaviour
{
    private enum Screen { Home, Play, Create, Join, Lobby, Pause, Loading, Training, TrainingGeneration, Settings }
    public bool Visible { get; private set; }
    private MultiplayerPrototype app;
    private UIDocument document;
    private PanelSettings panel;
    private VisualElement root, content;
    private Label error, status;
    private Screen screen;
    private string lobbySignature;
    private TextField nickname, code;
    private ScrollView lobbyOptionsScroll;

    public void Initialize(MultiplayerPrototype controller)
    {
        app = controller;
        panel = ScriptableObject.CreateInstance<PanelSettings>();
        panel.name = "Main menu";
        panel.themeStyleSheet = Resources.Load<ThemeStyleSheet>("UI/MenuTheme");
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
        var button = new Button(() => { GameAudio.PlayFlat(SoundBank.Switch, 0.35f); action(); }) { text = title };
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
        ActionButton(content, "Тренировка", ShowTraining);
        ActionButton(content, "Настройки", () => ShowSettings(false));
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

    private void ShowTraining()
    {
        screen = Screen.Training;
        Draw("Тренировка", "Выбери карту для локальной игры.");
        ActionButton(content, "Обычная карта", ShowTrainingGeneration, true);
        var training = new ScrollView(); training.style.maxHeight = 320; content.Add(training);
        var fields = new SettingsFields(training, app.TrainingSettings, false,
            value => app.TrainingSettings = value);
        ActionButton(content, "Воксельный пригород", () => app.StartTraining(true));
        ActionButton(content, "Назад", ShowHome);
        Footer();
    }

    private void ShowTrainingGeneration()
    {
        screen = Screen.TrainingGeneration;
        Draw("Обычная карта", "Настрой генерацию перед запуском.");
        var generator = app.TrainingGenerator;
        if (generator != null)
        {
            var seed = new IntegerField("Сид") { value = generator.seed };
            var width = new IntegerField("Ширина, м") { value = Mathf.RoundToInt(generator.mapSize.x) };
            var depth = new IntegerField("Длина, м") { value = Mathf.RoundToInt(generator.mapSize.y) };
            var buildings = new IntegerField("Зданий (1–16)") { value = generator.buildingCount };
            var roofs = new Toggle("Крыши") { value = generator.generateRoofs };
            content.Add(seed); content.Add(width); content.Add(depth); content.Add(buildings); content.Add(roofs);
            ActionButton(content, "Начать тренировку", () => app.StartTraining(false,
                seed.value, new Vector2(width.value, depth.value), buildings.value, roofs.value), true);
        }
        else content.Add(Text("Генератор карты отсутствует в сцене.", "menu-error"));
        ActionButton(content, "Назад", ShowTraining);
        Footer();
    }

    private void ShowCreate()
    {
        screen = Screen.Create;
        Draw("Создать лобби", "Ты будешь хостом матча.");
        var scroll = new ScrollView(); scroll.style.maxHeight = 470; content.Add(scroll);
        var fields = new SettingsFields(scroll, PrototypeLobbySettings.Load(), true);
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
        Vector2 optionsOffset = lobbyOptionsScroll != null ? lobbyOptionsScroll.scrollOffset : Vector2.zero;
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
            var scroll = new ScrollView(); scroll.style.maxHeight = 380; options.Add(scroll);
            lobbyOptionsScroll = scroll;
            scroll.schedule.Execute(() => scroll.scrollOffset = optionsOffset);
            var fields = new SettingsFields(scroll, settings, false, value => app.ChangeSettings(value));
            ActionButton(options, "По умолчанию", () => app.ChangeSettings(new PrototypeLobbySettings
                { Name = settings.Name, MaxPlayers = settings.MaxPlayers }));
            options.Add(Text("После изменения все подтверждают готовность заново.", "field-note"));
        }
        else
        {
            var scroll = new ScrollView(); scroll.style.maxHeight = 380; options.Add(scroll);
            new SettingsFields(scroll, settings, false); scroll.SetEnabled(false);
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
        ActionButton(content, "Настройки", () => ShowSettings(true));
        if (app.IsHost && app.InMatch) ActionButton(content, "Вернуться в лобби", app.ReturnToLobby);
        ActionButton(content, "Главное меню", () => app.LeaveWithReason()); Footer();
    }

    public void ShowLoading(string text)
    {
        screen = Screen.Loading; Draw("Подожди", text); Footer();
    }

    public void Hide() { if (displayBefore != null) FinishDisplay(false); bindingAction = null; Visible = false; root.style.display = DisplayStyle.None; }
    public void ShowError(string text) { if (error != null) error.text = text; }
    public void SetBusy(string message)
    {
        if (status != null) status.text = message ?? "";
        if (content != null) content.SetEnabled(string.IsNullOrEmpty(message));
    }

    private sealed class SettingsFields
    {
        private readonly PrototypeLobbySettings value;
        public SettingsFields(VisualElement parent, PrototypeLobbySettings settings, bool creating,
            Action<PrototypeLobbySettings> changed = null)
        {
            value = JsonUtility.FromJson<PrototypeLobbySettings>(JsonUtility.ToJson(settings));
            void Commit() { value.Clamp(); changed?.Invoke(Read()); }
            void Number(string label, int initial, Action<int> assign)
            {
                var field = new IntegerField(label) { value = initial, isDelayed = true }; parent.Add(field);
                field.RegisterValueChangedCallback(e => { assign(e.newValue); Commit(); });
            }
            void Flag(string label, bool initial, Action<bool> assign)
            {
                var field = new Toggle(label) { value = initial }; parent.Add(field);
                field.RegisterValueChangedCallback(e => { assign(e.newValue); Commit(); });
            }
            void Choice(string label, string[] choices, int initial, Action<int> assign)
            {
                var field = new DropdownField(label, choices.ToList(), initial); parent.Add(field);
                field.RegisterValueChangedCallback(e => { assign(field.index); Commit(); });
            }
            if (creating)
            {
                var name = new TextField("Название") { value = value.Name, maxLength = 32, isDelayed = true }; parent.Add(name);
                name.RegisterValueChangedCallback(e => { value.Name = e.newValue; Commit(); });
                Number("Игроков (2–8)", value.MaxPlayers, n => value.MaxPlayers = n);
            }
            Choice("Карта", new[] { "Обычная", "Воксельный пригород" }, value.Suburb ? 1 : 0, n => value.Suburb = n == 1);
            Choice("Время суток", new[] { "Утро", "День", "Вечер", "Ночь", "Случайно" }, (int)value.Time, n => value.Time = (TimeOfDay)n);
            Choice("Погода", new[] { "Ясно", "Облачно", "Дождь", "Туман" }, (int)value.Weather, n => value.Weather = (Weather)n);
            // Town generator: applied to the next round's map, never to the one being played.
            Choice("Размер карты", new[] { "Маленькая", "Средняя" }, value.MapSize, n => value.MapSize = n);
            Choice("Застройка", new[] { "Частный сектор", "Многоэтажный район", "Смешанная", "Случайная" }, value.Zoning, n => value.Zoning = n);
            Choice("Плотность застройки", new[] { "Редкая", "Обычная", "Плотная" }, value.Density, n => value.Density = n);
            Number("Многоэтажек, макс. (0–6)", value.Apartments, n => value.Apartments = n);
            Number("Seed карты (0 = новая каждый раунд)", value.Seed, n => value.Seed = n);
            Number("Прятки, сек. (10–120)", value.HidingSeconds, n => value.HidingSeconds = n);
            Number("Поиск, сек. (60–600)", value.HuntingSeconds, n => value.HuntingSeconds = n);
            Number("Дронов (0 = ∞)", value.DroneStock, n => value.DroneStock = n);
            Number("Возрождение, сек.", value.RespawnSeconds, n => value.RespawnSeconds = n);
            Number("Жизней человека", value.HumanLives, n => value.HumanLives = n);
            Flag("Предметы", value.Items, b => value.Items = b);
            Flag("Электричество", value.Electricity, b => value.Electricity = b);
            Flag("Перенос предметов", value.Carrying, b => value.Carrying = b);
            Flag("Баррикады", value.Barricades, b => value.Barricades = b);
            Flag("Положение лёжа", value.Prone, b => value.Prone = b);
            Flag("Перелезание", value.Vaulting, b => value.Vaulting = b);
            Flag("Наклоны", value.Leaning, b => value.Leaning = b);
            Flag("Урон от падения", value.FallDamage, b => value.FallDamage = b);
            Choice("Сила урона от падения", new[] { "Слабый", "Обычный", "Сильный" }, value.FallDamageLevel, n => value.FallDamageLevel = n);
        }
        public PrototypeLobbySettings Read() => JsonUtility.FromJson<PrototypeLobbySettings>(JsonUtility.ToJson(value));
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
        if (displayBefore != null) FinishDisplay(false);
        if (document != null) Destroy(document);
        if (panel != null) Destroy(panel);
    }
}
