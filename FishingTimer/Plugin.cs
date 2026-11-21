using Dalamud.Bindings.ImGui;
using Dalamud.Configuration;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using System;
using System.Diagnostics;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace FishingTimer
{
    [Serializable]
    public class Configuration : IPluginConfiguration
    {
        public int Version { get; set; } = 1;

        public bool LockWindow { get; set; } = false;
        public bool CenterText { get; set; } = true;
        public float TimerFontSize { get; set; } = 48f;
        public Vector4 TimerColor { get; set; } = new Vector4(0.2f, 1.0f, 0.4f, 1.0f);
        public Vector4 BackgroundColor { get; set; } = new Vector4(0.06f, 0.06f, 0.06f, 0.85f);
        public Vector4 BorderColor { get; set; } = new Vector4(0.2f, 1.0f, 0.4f, 0.85f);

        public bool ShowStatusWindow { get; set; } = false;
        public bool StatusLockWindow { get; set; } = false;
        public bool StatusCenterText { get; set; } = true;
        public float StatusFontSize { get; set; } = 18f;
        public Vector4 StatusColor { get; set; } = new Vector4(0.2f, 1.0f, 0.4f, 1.0f);
        public Vector4 StatusBackgroundColor { get; set; } = new Vector4(0.06f, 0.06f, 0.06f, 0.85f);
        public Vector4 StatusBorderColor { get; set; } = new Vector4(0.2f, 1.0f, 0.4f, 0.85f);
    }

    public sealed class Plugin : IDalamudPlugin
    {
        public string Name => "Fishing Timer";

        private const string CommandName = "/fishingtimer";
        private const string ShortCommandName = "/ftimer";
        private const float BaseFontPt = 36f;

        private readonly IDalamudPluginInterface _pluginInterface;
        private readonly Configuration _config;

        [PluginService] private static ICondition Condition { get; set; } = null!;
        [PluginService] private static IFramework Framework { get; set; } = null!;
        [PluginService] private static ICommandManager CommandManager { get; set; } = null!;

        private readonly Stopwatch _fishingStopwatch = new();
        private bool _isFishingSessionActive = false;
        private bool _isLineOut = false;
        private bool _showConfigWindow = false;
        private bool _showPreview = false;

        private string _debugInfo = "Not fishing";

        private readonly IFontHandle _hiResFont;

        public Plugin(IDalamudPluginInterface pluginInterface)
        {
            _pluginInterface = pluginInterface;
            _config = _pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

            if (_config.TimerFontSize < 16f)
            {
                _config.TimerFontSize = 48f;
            }

            if (_config.StatusFontSize < 12f)
            {
                _config.StatusFontSize = 18f;
            }

            if (_config.TimerColor.W == 0f && _config.BackgroundColor.W == 0f && _config.BorderColor.W == 0f)
            {
                _config.TimerColor = new Vector4(_config.TimerColor.X, _config.TimerColor.Y, _config.TimerColor.Z, 1.0f);
                _config.BackgroundColor = new Vector4(_config.BackgroundColor.X, _config.BackgroundColor.Y, _config.BackgroundColor.Z, 0.85f);
                _config.BorderColor = new Vector4(_config.BorderColor.X, _config.BorderColor.Y, _config.BorderColor.Z, 0.85f);
            }

            if (_config.StatusColor.W == 0f && _config.StatusBackgroundColor.W == 0f && _config.StatusBorderColor.W == 0f)
            {
                _config.StatusColor = new Vector4(0.2f, 1.0f, 0.4f, 1.0f);
                _config.StatusBackgroundColor = new Vector4(0.06f, 0.06f, 0.06f, 0.85f);
                _config.StatusBorderColor = new Vector4(0.2f, 1.0f, 0.4f, 0.85f);
            }

            _hiResFont = _pluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(
                new GameFontStyle(GameFontFamily.Axis, BaseFontPt)
            );

            var commandInfo = new CommandInfo(OnCommand)
            {
                HelpMessage = "Opens or closes the Fishing Timer settings window."
            };
            CommandManager.AddHandler(CommandName, commandInfo);
            CommandManager.AddHandler(ShortCommandName, commandInfo);

            Framework.Update += OnFrameworkUpdate;
            _pluginInterface.UiBuilder.Draw += DrawUI;
            _pluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
            _pluginInterface.UiBuilder.OpenMainUi += ToggleConfigUi;
        }

        private void OnCommand(string command, string args)
        {
            ToggleConfigUi();
        }

        private void ToggleConfigUi()
        {
            _showConfigWindow = !_showConfigWindow;
        }

        private unsafe void OnFrameworkUpdate(IFramework framework)
        {
            bool lineInWater = Condition[ConditionFlag.Fishing];
            bool gatheringActive = Condition[ConditionFlag.Gathering];

            FishingState currentState = FishingState.None;
            var eventFramework = EventFramework.Instance();
            if (eventFramework != null)
            {
                var fishingHandler = eventFramework->EventHandlerModule.FishingEventHandler;
                if (fishingHandler != null)
                {
                    currentState = fishingHandler->State;
                }
            }

            bool hasRodOutAtWater = lineInWater || (gatheringActive && currentState != FishingState.None);

            if (hasRodOutAtWater)
            {
                _isFishingSessionActive = true;

                if (lineInWater && !_isLineOut)
                {
                    _isLineOut = true;
                    _fishingStopwatch.Restart();
                }
                else if (lineInWater && _isLineOut)
                {
                    if (_fishingStopwatch.IsRunning && currentState == FishingState.Bite)
                    {
                        _fishingStopwatch.Stop();
                    }
                }
                else if (!lineInWater && _isLineOut)
                {
                    _isLineOut = false;
                    _fishingStopwatch.Stop();
                }

                _debugInfo = $"FishingState: {currentState} ({(int)currentState})";
            }
            else if (_isFishingSessionActive)
            {
                _isFishingSessionActive = false;
                _isLineOut = false;
                _fishingStopwatch.Reset();
                _debugInfo = "Not fishing";
            }
        }

        private void DrawUI()
        {
            DrawTimerOverlay();
            DrawStatusOverlay();
            DrawConfigWindow();
        }

        private void DrawTimerOverlay()
        {
            if (!_isFishingSessionActive && !_showPreview) return;

            ImGui.SetNextWindowSize(new Vector2(200, 100), ImGuiCond.FirstUseEver);

            ImGuiWindowFlags windowFlags = ImGuiWindowFlags.NoTitleBar;

            if (_config.LockWindow)
            {
                windowFlags |= ImGuiWindowFlags.NoMove
                             | ImGuiWindowFlags.NoResize
                             | ImGuiWindowFlags.NoInputs;
            }

            ImGui.PushStyleColor(ImGuiCol.WindowBg, _config.BackgroundColor);
            ImGui.PushStyleColor(ImGuiCol.Border, _config.BorderColor);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1.0f);

            if (ImGui.Begin("FishingTimerOverlay", windowFlags))
            {
                double elapsedSeconds = Math.Floor(_fishingStopwatch.Elapsed.TotalSeconds * 10.0) / 10.0;

                string timerText = $"{elapsedSeconds:F1} s";
                string previewText = "[Preview Mode]";
                bool showPreviewText = _showPreview && !_isFishingSessionActive;

                Vector2 windowSize = ImGui.GetWindowSize();
                float spacing = ImGui.GetStyle().ItemSpacing.Y;

                Vector2 previewSize = showPreviewText ? ImGui.CalcTextSize(previewText) : Vector2.Zero;

                Vector2 timerSize;
                if (_hiResFont.Available)
                {
                    using (_hiResFont.Lock())
                    {
                        ImGui.SetWindowFontScale(_config.TimerFontSize / BaseFontPt);
                        timerSize = ImGui.CalcTextSize(timerText);
                        ImGui.SetWindowFontScale(1.0f);
                    }
                }
                else
                {
                    ImGui.SetWindowFontScale(_config.TimerFontSize / 16f);
                    timerSize = ImGui.CalcTextSize(timerText);
                    ImGui.SetWindowFontScale(1.0f);
                }

                float totalHeight = timerSize.Y;
                if (showPreviewText) totalHeight += previewSize.Y + spacing;

                float currentY = ImGui.GetCursorPosY();
                if (_config.CenterText)
                {
                    currentY = Math.Max(8f, (windowSize.Y - totalHeight) * 0.5f);
                    ImGui.SetCursorPos(new Vector2(Math.Max(8f, (windowSize.X - timerSize.X) * 0.5f), currentY));
                }

                if (_hiResFont.Available)
                {
                    using (_hiResFont.Lock())
                    {
                        ImGui.SetWindowFontScale(_config.TimerFontSize / BaseFontPt);
                        ImGui.TextColored(_config.TimerColor, timerText);
                        ImGui.SetWindowFontScale(1.0f);
                    }
                }
                else
                {
                    ImGui.SetWindowFontScale(_config.TimerFontSize / 16f);
                    ImGui.TextColored(_config.TimerColor, timerText);
                    ImGui.SetWindowFontScale(1.0f);
                }
                currentY += timerSize.Y + spacing;

                if (showPreviewText)
                {
                    if (_config.CenterText)
                    {
                        ImGui.SetCursorPos(new Vector2(Math.Max(8f, (windowSize.X - previewSize.X) * 0.5f), currentY));
                    }
                    ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.2f, 1.0f), previewText);
                }
            }
            ImGui.End();

            ImGui.PopStyleVar(1);
            ImGui.PopStyleColor(2);
        }

        private void DrawStatusOverlay()
        {
            if (!_config.ShowStatusWindow || (!_isFishingSessionActive && !_showPreview)) return;

            ImGui.SetNextWindowSize(new Vector2(240, 56), ImGuiCond.FirstUseEver);

            ImGuiWindowFlags windowFlags = ImGuiWindowFlags.NoTitleBar;

            if (_config.StatusLockWindow)
            {
                windowFlags |= ImGuiWindowFlags.NoMove
                             | ImGuiWindowFlags.NoResize
                             | ImGuiWindowFlags.NoInputs;
            }

            ImGui.PushStyleColor(ImGuiCol.WindowBg, _config.StatusBackgroundColor);
            ImGui.PushStyleColor(ImGuiCol.Border, _config.StatusBorderColor);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1.0f);

            if (ImGui.Begin("FishingTimerStatusOverlay", windowFlags))
            {
                Vector2 windowSize = ImGui.GetWindowSize();
                Vector2 textSize;

                if (_hiResFont.Available)
                {
                    using (_hiResFont.Lock())
                    {
                        ImGui.SetWindowFontScale(_config.StatusFontSize / BaseFontPt);
                        textSize = ImGui.CalcTextSize(_debugInfo);
                        ImGui.SetWindowFontScale(1.0f);
                    }
                }
                else
                {
                    ImGui.SetWindowFontScale(_config.StatusFontSize / 16f);
                    textSize = ImGui.CalcTextSize(_debugInfo);
                    ImGui.SetWindowFontScale(1.0f);
                }

                if (_config.StatusCenterText)
                {
                    float posX = Math.Max(8f, (windowSize.X - textSize.X) * 0.5f);
                    float posY = Math.Max(6f, (windowSize.Y - textSize.Y) * 0.5f);
                    ImGui.SetCursorPos(new Vector2(posX, posY));
                }

                if (_hiResFont.Available)
                {
                    using (_hiResFont.Lock())
                    {
                        ImGui.SetWindowFontScale(_config.StatusFontSize / BaseFontPt);
                        ImGui.TextColored(_config.StatusColor, _debugInfo);
                        ImGui.SetWindowFontScale(1.0f);
                    }
                }
                else
                {
                    ImGui.SetWindowFontScale(_config.StatusFontSize / 16f);
                    ImGui.TextColored(_config.StatusColor, _debugInfo);
                    ImGui.SetWindowFontScale(1.0f);
                }
            }
            ImGui.End();

            ImGui.PopStyleVar(1);
            ImGui.PopStyleColor(2);
        }

        private void DrawConfigWindow()
        {
            if (!_showConfigWindow) return;

            ImGui.SetNextWindowSize(new Vector2(410, 560), ImGuiCond.FirstUseEver);

            if (ImGui.Begin("Fishing Timer Settings", ref _showConfigWindow, ImGuiWindowFlags.NoCollapse))
            {
                bool changed = false;

                ImGui.Checkbox("Show preview (for positioning)", ref _showPreview);

                ImGui.Spacing();
                ImGui.TextColored(new Vector4(0.4f, 0.85f, 1.0f, 1.0f), "Timer Window Settings");
                ImGui.Separator();

                bool lockWin = _config.LockWindow;
                if (ImGui.Checkbox("Lock timer window position and size", ref lockWin))
                {
                    _config.LockWindow = lockWin;
                    changed = true;
                }

                bool centerText = _config.CenterText;
                if (ImGui.Checkbox("Center timer in window", ref centerText))
                {
                    _config.CenterText = centerText;
                    changed = true;
                }

                Vector4 bgColor = _config.BackgroundColor;
                if (ImGui.ColorEdit4("Timer Background & Opacity", ref bgColor, ImGuiColorEditFlags.AlphaBar))
                {
                    _config.BackgroundColor = bgColor;
                    changed = true;
                }

                Vector4 borderColor = _config.BorderColor;
                if (ImGui.ColorEdit4("Timer Border & Opacity", ref borderColor, ImGuiColorEditFlags.AlphaBar))
                {
                    _config.BorderColor = borderColor;
                    changed = true;
                }

                float fontSize = _config.TimerFontSize;
                if (ImGui.SliderFloat("Timer Font Size", ref fontSize, 16f, 200f, "%.0f"))
                {
                    _config.TimerFontSize = fontSize;
                    changed = true;
                }

                Vector4 timerColor = _config.TimerColor;
                if (ImGui.ColorEdit4("Timer Font Color & Opacity", ref timerColor, ImGuiColorEditFlags.AlphaBar))
                {
                    _config.TimerColor = timerColor;
                    changed = true;
                }

                ImGui.Spacing();
                ImGui.Spacing();
                ImGui.TextColored(new Vector4(0.4f, 0.85f, 1.0f, 1.0f), "Status Window Settings");
                ImGui.Separator();

                bool showStatusWin = _config.ShowStatusWindow;
                if (ImGui.Checkbox("Show separate status window", ref showStatusWin))
                {
                    _config.ShowStatusWindow = showStatusWin;
                    changed = true;
                }

                bool statusLockWin = _config.StatusLockWindow;
                if (ImGui.Checkbox("Lock status window position and size", ref statusLockWin))
                {
                    _config.StatusLockWindow = statusLockWin;
                    changed = true;
                }

                bool statusCenterText = _config.StatusCenterText;
                if (ImGui.Checkbox("Center status text in window", ref statusCenterText))
                {
                    _config.StatusCenterText = statusCenterText;
                    changed = true;
                }

                Vector4 statusBgColor = _config.StatusBackgroundColor;
                if (ImGui.ColorEdit4("Status Background & Opacity", ref statusBgColor, ImGuiColorEditFlags.AlphaBar))
                {
                    _config.StatusBackgroundColor = statusBgColor;
                    changed = true;
                }

                Vector4 statusBorderColor = _config.StatusBorderColor;
                if (ImGui.ColorEdit4("Status Border & Opacity", ref statusBorderColor, ImGuiColorEditFlags.AlphaBar))
                {
                    _config.StatusBorderColor = statusBorderColor;
                    changed = true;
                }

                float statusFontSize = _config.StatusFontSize;
                if (ImGui.SliderFloat("Status Font Size", ref statusFontSize, 12f, 120f, "%.0f"))
                {
                    _config.StatusFontSize = statusFontSize;
                    changed = true;
                }

                Vector4 statusColor = _config.StatusColor;
                if (ImGui.ColorEdit4("Status Font Color & Opacity", ref statusColor, ImGuiColorEditFlags.AlphaBar))
                {
                    _config.StatusColor = statusColor;
                    changed = true;
                }

                ImGui.Spacing();
                ImGui.Separator();
                if (ImGui.Button("Reset All to Defaults"))
                {
                    _config.LockWindow = false;
                    _config.CenterText = true;
                    _config.TimerFontSize = 48f;
                    _config.TimerColor = new Vector4(0.2f, 1.0f, 0.4f, 1.0f);
                    _config.BackgroundColor = new Vector4(0.06f, 0.06f, 0.06f, 0.85f);
                    _config.BorderColor = new Vector4(0.2f, 1.0f, 0.4f, 0.85f);

                    _config.ShowStatusWindow = false;
                    _config.StatusLockWindow = false;
                    _config.StatusCenterText = true;
                    _config.StatusFontSize = 18f;
                    _config.StatusColor = new Vector4(0.2f, 1.0f, 0.4f, 1.0f);
                    _config.StatusBackgroundColor = new Vector4(0.06f, 0.06f, 0.06f, 0.85f);
                    _config.StatusBorderColor = new Vector4(0.2f, 1.0f, 0.4f, 0.85f);
                    changed = true;
                }

                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), $"  ({_debugInfo})");

                if (changed)
                {
                    _pluginInterface.SavePluginConfig(_config);
                }
            }
            ImGui.End();
        }

        public void Dispose()
        {
            CommandManager.RemoveHandler(CommandName);
            CommandManager.RemoveHandler(ShortCommandName);
            Framework.Update -= OnFrameworkUpdate;
            _pluginInterface.UiBuilder.Draw -= DrawUI;
            _pluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
            _pluginInterface.UiBuilder.OpenMainUi -= ToggleConfigUi;
            _hiResFont.Dispose();
        }
    }
}